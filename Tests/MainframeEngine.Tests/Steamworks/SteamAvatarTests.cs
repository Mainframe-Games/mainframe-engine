using System.Diagnostics;

namespace MainframeEngine.Tests.Steamworks;

/// <summary>
/// Steam avatars as textures (#58). Without Steam (app id 0) every request is null and nothing is cached.
/// <see cref="OwnAvatarLoadsWhenSteamIsRunning"/> is the live check: it fetches this user's avatar as Spacewar on a
/// developer machine with Steam open, and is skipped elsewhere (CI has no Steam client).
/// </summary>
[Collection(nameof(SerialSteam))]
public sealed class SteamAvatarTests
{
    private const uint Spacewar = 480;
    private const ulong SomeUser = 76561197960287930;

    [Fact]
    public async Task AvatarsAreNullAndNotCachedWithoutSteam()
    {
        Steam.TryInitialize(0);

        foreach (var size in Enum.GetValues<SteamAvatarSize>())
            Assert.Null(await Steam.GetAvatarAsync(SomeUser, size));
        Assert.Null(await new SteamFriend(SomeUser, "someone").GetAvatarAsync());
        Steam.RunCallbacks();

        Assert.Equal(0, Steam.AvatarCacheCount);
    }

    [Fact]
    public async Task ArgumentsAreStillCheckedWithoutSteam()
    {
        Steam.TryInitialize(0);

        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = Steam.GetAvatarAsync(SomeUser, (SteamAvatarSize)7); });
        await Assert.ThrowsAsync<ArgumentNullException>(() => Steam.RegisterAvatarAsync(null!, "avatar", SomeUser));
    }

    [Fact]
    public void AvatarPixelsBecomeAnSrgbTexture()
    {
        var rgba = new byte[4 * 2 * 4];
        rgba[0] = 200;
        rgba[3] = 255;

        var texture = Steam.CreateAvatarTexture(4, 2, rgba, (SomeUser, SteamAvatarSize.Small));

        Assert.NotNull(texture);
        Assert.Equal(4, texture.Width);
        Assert.Equal(2, texture.Height);
        Assert.Equal(TextureImportColorSpace.Srgb, texture.ImportSettings.ColorSpace);
        Assert.False(texture.ImportSettings.Mipmaps);
        Assert.Equal(TextureWrap.Clamp, texture.ImportSettings.Wrap);
        var (pixels, _, _) = texture.DecodePixels();
        Assert.Equal(rgba, pixels);
        Assert.Contains(SomeUser.ToString(System.Globalization.CultureInfo.InvariantCulture), texture.ResourceName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4u, 2u, 31)]
    [InlineData(0u, 2u, 0)]
    [InlineData(5000u, 1u, 20000)]
    public void MismatchedAvatarPixelsAreRejected(uint width, uint height, int length) =>
        Assert.Null(Steam.CreateAvatarTexture(width, height, new byte[length], (SomeUser, SteamAvatarSize.Small)));

    [Fact]
    public async Task OwnAvatarLoadsWhenSteamIsRunning()
    {
        try
        {
            if (!Steam.TryInitialize(Spacewar))
                Assert.Skip($"Needs the Steam client running: {Steam.Status} ({Steam.StatusMessage})");

            var request = Steam.GetAvatarAsync(Steam.SteamId);
            Assert.Same(request, Steam.GetAvatarAsync(Steam.SteamId)); // one fetch per (SteamID, size)

            // The result completes inside RunCallbacks, as it would once per frame.
            var clock = Stopwatch.StartNew();
            while (!request.IsCompleted && clock.Elapsed < Steam.AvatarTimeout + TimeSpan.FromSeconds(5))
            {
                Steam.RunCallbacks();
                await Task.Delay(16, TestContext.Current.CancellationToken);
            }

            var avatar = await request;
            Assert.NotNull(avatar);
            Assert.Equal(184, avatar.Width);
            Assert.Equal(184, avatar.Height);
            var (rgba, _, _) = avatar.DecodePixels();
            Assert.Contains(rgba, static b => b != 0);
            Assert.Same(avatar, await Steam.GetAvatarAsync(Steam.SteamId)); // cached
            Assert.Equal(1, Steam.AvatarCacheCount);
        }
        finally
        {
            Steam.Shutdown();
        }

        Assert.Equal(0, Steam.AvatarCacheCount);
    }
}
