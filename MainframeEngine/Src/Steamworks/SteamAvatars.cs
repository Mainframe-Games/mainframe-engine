using System.Collections.Concurrent;

namespace MainframeEngine;

/// <summary>Size of a Steam avatar (<see cref="Steam.GetAvatarAsync"/>).</summary>
public enum SteamAvatarSize
{
    /// <summary>32×32 pixels.</summary>
    Small,

    /// <summary>64×64 pixels.</summary>
    Medium,

    /// <summary>184×184 pixels.</summary>
    Large,
}

// Avatars (issue #58; docs/design/steamworks.md#avatars). Kept apart from Steam.cs: Shutdown and RunCallbacks call in here.
public static partial class Steam
{
    private static readonly Lock AvatarSync = new();
    private static readonly Task<Texture2D?> NoAvatar = Task.FromResult<Texture2D?>(null);

    // Pending and loaded avatars by (SteamID, size). A failed fetch is removed again, so asking later retries it.
    private static readonly Dictionary<(ulong SteamId, SteamAvatarSize Size), TaskCompletionSource<Texture2D?>> Avatars = [];

    // Fetches finish on thread-pool threads; RunCallbacks completes them on the main thread.
    private static readonly ConcurrentQueue<AvatarResult> AvatarResults = new();

    // Bumped by Shutdown: results of fetches started before it are dropped.
    private static int _avatarGeneration;

    private static readonly TextureImportSettings AvatarSettings = new()
    {
        ColorSpace = TextureImportColorSpace.Srgb,
        Mipmaps = false,
        Wrap = TextureWrap.Clamp,
    };

    /// <summary>How long <see cref="GetAvatarAsync"/> waits for Steam before it gives up and returns null (default 15 s).</summary>
    public static TimeSpan AvatarTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Avatars cached or being fetched (tests).</summary>
    internal static int AvatarCacheCount
    {
        get
        {
            lock (AvatarSync)
                return Avatars.Count;
        }
    }

    /// <summary>
    /// The Steam avatar of <paramref name="steamId"/> (the logged-in user, a friend, a lobby member) as an sRGB RGBA8
    /// <see cref="Texture2D"/>: no mipmaps, clamped, created in code (not saved with scenes). Completes inside
    /// <see cref="RunCallbacks"/> on the main thread; null without Steam, when the user has no avatar, or after
    /// <see cref="AvatarTimeout"/>. Textures are cached per (SteamID, size) until <see cref="Shutdown"/>, so asking again
    /// returns the same texture (and a pending request the same task); a failed request is retried on the next call.
    /// Show it in the game UI with <see cref="RegisterAvatarAsync"/>.
    /// </summary>
    public static Task<Texture2D?> GetAvatarAsync(ulong steamId, SteamAvatarSize size = SteamAvatarSize.Large)
    {
        if (!Enum.IsDefined(size))
            throw new ArgumentOutOfRangeException(nameof(size), size, "Not a SteamAvatarSize.");
        if (!Valid || steamId == 0)
            return NoAvatar;

        var key = (steamId, size);
        TaskCompletionSource<Texture2D?> source;
        int generation;
        lock (AvatarSync)
        {
            if (Avatars.TryGetValue(key, out var existing))
                return existing.Task;
            source = new TaskCompletionSource<Texture2D?>();
            Avatars.Add(key, source);
            generation = _avatarGeneration;
        }

        Task<(uint Width, uint Height, byte[] Rgba)?> fetch;
        try
        {
            fetch = SteamAvatarApi.GetAvatarAsync(steamId, size);
        }
        catch (Exception e)
        {
            Log.Warning($"[Steam] avatar of {steamId} ({size}) threw {e.GetType().Name}: {e.Message}");
            fetch = Task.FromResult<(uint, uint, byte[])?>(null);
        }

        _ = WaitForAvatarAsync(fetch, key, source, generation);
        return source.Task;
    }

    /// <summary>
    /// Publishes the avatar of <paramref name="steamId"/> to the game UI as <c>engine://<paramref name="name"/></c>
    /// (<c>&lt;img src="engine://avatar-host"/&gt;</c>, e.g. in a lobby member list) through
    /// <see cref="UiServer.RegisterTexture(string, Texture2D)"/>, once it is loaded. False when there is no avatar
    /// (<see cref="GetAvatarAsync"/> returned null); the name is then left as it was.
    /// </summary>
    public static async Task<bool> RegisterAvatarAsync(UiServer ui, string name, ulong steamId, SteamAvatarSize size = SteamAvatarSize.Large)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (await GetAvatarAsync(steamId, size).ConfigureAwait(false) is not { } avatar)
            return false;
        ui.RegisterTexture(name, avatar);
        return true;
    }

    private static async Task WaitForAvatarAsync(Task<(uint Width, uint Height, byte[] Rgba)?> fetch, (ulong SteamId, SteamAvatarSize Size) key,
        TaskCompletionSource<Texture2D?> source, int generation)
    {
        (uint Width, uint Height, byte[] Rgba)? pixels = null;
        try
        {
            pixels = await fetch.WaitAsync(AvatarTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warning($"[Steam] avatar of {key.SteamId} ({key.Size}) timed out after {AvatarTimeout.TotalSeconds:0.#} s");
        }
        catch (Exception e) when (generation == Volatile.Read(ref _avatarGeneration))
        {
            Log.Warning($"[Steam] avatar of {key.SteamId} ({key.Size}) failed: {e.GetType().Name}: {e.Message}");
        }
        catch (Exception)
        {
            // Steam shut down while Facepunch was still polling: nothing to report.
        }

        AvatarResults.Enqueue(new AvatarResult(key, source, generation, pixels));
    }

    /// <summary>Completes the avatar fetches that finished since the last frame (main thread, from <see cref="RunCallbacks"/>).</summary>
    private static void CompleteAvatars()
    {
        if (AvatarResults.IsEmpty)
            return;

        while (AvatarResults.TryDequeue(out var result))
        {
            var current = result.Generation == Volatile.Read(ref _avatarGeneration);
            var texture = current && result.Pixels is { } p ? CreateAvatarTexture(p.Width, p.Height, p.Rgba, result.Key) : null;
            if (texture is null)
            {
                lock (AvatarSync)
                {
                    if (Avatars.TryGetValue(result.Key, out var cached) && cached == result.Source)
                        Avatars.Remove(result.Key);
                }
            }

            result.Source.TrySetResult(texture);
        }
    }

    /// <summary>The avatar texture for Steam's pixels, or null (with a warning) when they do not match their size.</summary>
    internal static Texture2D? CreateAvatarTexture(uint width, uint height, byte[] rgba, (ulong SteamId, SteamAvatarSize Size) key)
    {
        if (width is 0 or > 4096 || height is 0 or > 4096 || rgba.Length != (long)width * height * 4)
        {
            Log.Warning($"[Steam] avatar of {key.SteamId} ({key.Size}): {rgba.Length} bytes do not match {width}×{height} RGBA8");
            return null;
        }

        var texture = Texture2D.FromPixels((int)width, (int)height, rgba, AvatarSettings);
        texture.ResourceName = $"Steam avatar {key.SteamId} ({key.Size})";
        return texture;
    }

    /// <summary>Forgets every avatar (from <see cref="Shutdown"/>): pending requests complete with null.</summary>
    private static void ClearAvatars()
    {
        TaskCompletionSource<Texture2D?>[] sources;
        lock (AvatarSync)
        {
            _avatarGeneration++;
            sources = [.. Avatars.Values];
            Avatars.Clear();
        }

        AvatarResults.Clear();
        foreach (var source in sources)
            source.TrySetResult(null);
    }

    private readonly record struct AvatarResult(
        (ulong SteamId, SteamAvatarSize Size) Key,
        TaskCompletionSource<Texture2D?> Source,
        int Generation,
        (uint Width, uint Height, byte[] Rgba)? Pixels);
}
