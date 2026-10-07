using System.Text.Json;
using MainframeEngine.Audio;

namespace MainframeEngine.Editor.Music;

/// <summary>What a render wrote.</summary>
/// <param name="OutputPath">Project path of the written file.</param>
/// <param name="Frames">Frames in the file.</param>
/// <param name="SampleRate">The file's rate.</param>
/// <param name="Loop">The <c>.meta</c>'s <c>loop</c>.</param>
/// <param name="LoopStart">The <c>.meta</c>'s <c>loopStart</c> in seconds.</param>
/// <param name="LoopEnd">The <c>.meta</c>'s <c>loopEnd</c> in seconds (0 = the end of the file).</param>
public readonly record struct SongRenderResult(string OutputPath, long Frames, int SampleRate, bool Loop, double LoopStart, double LoopEnd)
{
    public double Seconds => (double)Frames / SampleRate;
}

/// <summary>What part of the song a render covers, in engine frames.</summary>
/// <param name="StartFrame">Song frame the engine starts at.</param>
/// <param name="RenderFrames">Frames the engine renders.</param>
/// <param name="KeepFrom">First rendered frame kept in the file (seamless loops drop the first pass).</param>
public readonly record struct SongRenderPlan(long StartFrame, long RenderFrames, long KeepFrom, bool Loop, double LoopStart, double LoopEnd)
{
    public long OutputFrames => RenderFrames - KeepFrom;
}

/// <summary>
/// Offline render of a song to a sound file in the project (faster than real time, on the calling thread; run it on
/// a worker for the UI). Without a loop: from 0 to the end of the last clip plus <see cref="SongRenderSettings.TailSeconds"/>.
/// With a loop and <see cref="SongRenderSettings.SeamlessLoop"/>: the loop region twice, keeping the second pass, so
/// tails from the loop's end are already in its start (the whole file is the loop). With a loop, not seamless: from 0 to
/// the loop end, with the loop region as the file's loop points. An <c>.ogg</c> output (the default) is a temporary
/// 32-bit float WAV encoded by the plugin helper (<see cref="IPluginHost.Encode"/>, Vorbis VBR at
/// <see cref="SongRenderSettings.Quality"/>); a <c>.wav</c> output is written directly (32-bit float, or 16-bit PCM with
/// <see cref="SongRenderSettings.SampleFormat"/>). Either way the output is replaced only once complete (a failed render
/// leaves the previous file intact), then the output's <c>.meta</c> import settings are written (<c>loop</c>,
/// <c>loopStart</c>, <c>loopEnd</c> in seconds, <c>loadMode: Stream</c>).
/// </summary>
/// <remarks>
/// Without a helper (none for this platform, or none passed) an <c>.ogg</c> output is written as <c>.wav</c> next to it,
/// and the log says so.
/// </remarks>
public static class SongRenderer
{
    public const string DefaultFolder = "Content/Music";

    /// <summary>
    /// The project path the song renders to: its setting, else <c>Content/Music/&lt;name&gt;.ogg</c>; <c>.ogg</c> only
    /// when <paramref name="canEncode"/> (a plugin helper exists), anything else as <c>.wav</c>.
    /// </summary>
    public static string OutputPathFor(Song song, string songName, bool canEncode)
    {
        ArgumentNullException.ThrowIfNull(song);
        return OutputPathFor(song.Render.Output, songName, canEncode);
    }

    /// <inheritdoc cref="OutputPathFor(Song, string, bool)"/>
    public static string OutputPathFor(string? output, string songName, bool canEncode)
    {
        var requested = RequestedOutput(output, songName);
        return canEncode && IsOgg(requested) ? requested : Path.ChangeExtension(requested, ".wav");
    }

    // The output as set (or the .ogg default), before falling back to .wav.
    private static string RequestedOutput(string? output, string songName) =>
        string.IsNullOrWhiteSpace(output) ? $"{DefaultFolder}/{songName}.ogg" : output.Replace('\\', '/');

    private static bool IsOgg(string path) => string.Equals(Path.GetExtension(path), ".ogg", StringComparison.OrdinalIgnoreCase);

    /// <summary>Which frames a render covers at <paramref name="snapshot"/>'s rate.</summary>
    public static SongRenderPlan Plan(Song song, SongSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(snapshot);
        var rate = (double)snapshot.SampleRate;
        if (snapshot.LoopEnabled)
        {
            var length = snapshot.LoopEndFrame - snapshot.LoopStartFrame;
            if (song.Render.SeamlessLoop)
                return new SongRenderPlan(snapshot.LoopStartFrame, 2 * length, length, true, 0, 0);
            return new SongRenderPlan(0, snapshot.LoopEndFrame, 0, true, snapshot.LoopStartFrame / rate, snapshot.LoopEndFrame / rate);
        }

        var tail = (long)Math.Round(Math.Max(0, song.Render.TailSeconds) * rate);
        return new SongRenderPlan(0, Math.Max(1, snapshot.EndFrame + tail), 0, false, 0, 0);
    }

    /// <summary>Renders the song into memory (interleaved stereo at <see cref="SongRenderSettings.SampleRate"/>).</summary>
    /// <remarks>
    /// <paramref name="plugins"/> creates the render's own plugin rack (offline: its own helper, kOffline processing, a
    /// <see cref="PluginRack.OfflineBlockTimeout"/> watchdog per block); it is disposed when the render ends.
    /// </remarks>
    /// <exception cref="PluginHostException">A plugin block hit the watchdog.</exception>
    public static float[] RenderToBuffer(Song song, out SongRenderPlan plan, Func<SongTrack, int, IInstrument?>? instrumentFactory = null,
        Func<string, AudioStream?>? clipLoader = null, IProgress<double>? progress = null, Func<int, PluginRack?>? plugins = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(song);
        var rate = Math.Clamp(song.Render.SampleRate, 8000, 192000);
        using var rack = plugins?.Invoke(rate);
        var builder = new SongSnapshotBuilder(rate, instrumentFactory, clipLoader, rack);
        try
        {
            return RenderWith(builder, song, rate, rack, out plan, progress, cancellation);
        }
        finally
        {
            builder.ReleasePlugins();
        }
    }

    private static float[] RenderWith(SongSnapshotBuilder builder, Song song, int rate, PluginRack? rack, out SongRenderPlan plan,
        IProgress<double>? progress, CancellationToken cancellation)
    {
        var snapshot = builder.Build(song);
        foreach (var track in snapshot.TrackList)
        {
            if (track.Status is { } status)
                Log.Warning($"[Music] Render: track {song.Tracks.Find(t => t.Id == track.Id)?.Name}: {status}");
        }
        plan = Plan(song, snapshot);
        if (plan.OutputFrames * 2 > Array.MaxLength)
            throw new InvalidOperationException("The song is too long to render.");

        var engine = new SongEngine(rate);
        engine.SetSnapshot(snapshot);
        engine.SeekFrames(plan.StartFrame);
        engine.Play();
        var result = new float[plan.OutputFrames * 2];
        var block = new float[SongEngine.BlockFrames * 2];
        long done = 0;
        var lastReport = -1;
        while (done < plan.RenderFrames)
        {
            cancellation.ThrowIfCancellationRequested();
            var frames = (int)Math.Min(SongEngine.BlockFrames, plan.RenderFrames - done);
            var span = block.AsSpan(0, frames * 2);
            engine.Process(span);
            if (rack is { Xruns: > 0 })
                throw new PluginHostException($"A plugin did not finish a block within {PluginRack.OfflineBlockTimeout.TotalSeconds:0} s; the render stopped.");
            // Copy the part of this block that falls in the kept range.
            var keepStart = Math.Max(done, plan.KeepFrom);
            var keepEnd = done + frames;
            if (keepEnd > keepStart)
                span[(int)((keepStart - done) * 2)..].CopyTo(result.AsSpan((int)((keepStart - plan.KeepFrom) * 2)));
            done += frames;
            var percent = (int)(done * 100 / plan.RenderFrames);
            if (percent != lastReport)
            {
                lastReport = percent;
                progress?.Report(done / (double)plan.RenderFrames);
            }
        }

        return result;
    }

    /// <summary>
    /// Renders <paramref name="song"/> to its output in the project of <paramref name="database"/> and writes the output's
    /// <c>.meta</c>. <paramref name="songName"/> names the default output (the song file's name). <paramref name="encoder"/>
    /// encodes an <c>.ogg</c> output; without one it is written as <c>.wav</c> (with a warning).
    /// </summary>
    /// <exception cref="PluginHostException">Encoding failed (the previous output is left intact).</exception>
    public static SongRenderResult Render(Song song, string songName, AssetDatabase database,
        Func<SongTrack, int, IInstrument?>? instrumentFactory = null, Func<string, AudioStream?>? clipLoader = null,
        IProgress<double>? progress = null, IPluginHost? encoder = null, Func<int, PluginRack?>? plugins = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(database);
        var requested = RequestedOutput(song.Render.Output, songName);
        var output = OutputPathFor(song.Render.Output, songName, encoder is not null);
        var encode = IsOgg(output);
        if (!string.Equals(requested, output, StringComparison.Ordinal))
        {
            var why = IsOgg(requested)
                ? "encoding to .ogg needs the plugin helper (mfplughost), which is not available here"
                : "only .ogg and .wav are supported";
            Log.Warning($"[Music] '{requested}': {why}; rendering '{output}' instead.");
        }

        var samples = RenderToBuffer(song, out var plan, instrumentFactory, clipLoader, progress, plugins, cancellation);
        var rate = Math.Clamp(song.Render.SampleRate, 8000, 192000);
        var format = encode || song.Render.SampleFormat != SongSampleFormat.Pcm16 ? WavSampleFormat.IeeeFloat : WavSampleFormat.Pcm16;
        var quality = Math.Clamp(song.Render.Quality, 0, 10);
        var full = database.ToAbsolutePath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".rendering.tmp";
        try
        {
            WavWriter.Write(temp, samples, 2, rate, format);
            cancellation.ThrowIfCancellationRequested();
            if (encode)
                encoder!.Encode(temp, full, quality, cancellation); // writes <full>.encoding.tmp, then renames it over full
            else
                File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
            if (File.Exists(full + ".encoding.tmp"))
                File.Delete(full + ".encoding.tmp");
        }

        WriteMeta(database, output, plan);
        var result = new SongRenderResult(database.ToProjectPath(full), plan.OutputFrames, rate, plan.Loop, plan.LoopStart, plan.LoopEnd);
        var kind = encode ? $"Ogg Vorbis, quality {quality}" : $"WAV, {(format == WavSampleFormat.Pcm16 ? "16-bit PCM" : "32-bit float")}";
        Log.Info($"[Music] Rendered '{songName}' to {result.OutputPath} ({kind}, {result.Seconds:0.00} s{(plan.Loop ? ", loops" : "")}).");
        return result;
    }

    /// <summary>Sets the output's audio import settings, keeping its UID and any other settings.</summary>
    public static void WriteMeta(AssetDatabase database, string outputPath, in SongRenderPlan plan)
    {
        ArgumentNullException.ThrowIfNull(database);
        AssetMeta meta;
        try
        {
            meta = database.ReadOrCreateMeta(outputPath, create: false) ?? new AssetMeta();
        }
        catch (JsonException)
        {
            meta = new AssetMeta();
        }

        meta.Importer = "audio";
        var settings = meta.Settings ?? new Dictionary<string, JsonElement>();
        settings["loadMode"] = JsonSerializer.SerializeToElement("Stream", MusicJsonContext.Default.String);
        settings["loop"] = JsonSerializer.SerializeToElement(plan.Loop, MusicJsonContext.Default.Boolean);
        settings["loopStart"] = JsonSerializer.SerializeToElement(Math.Round(plan.LoopStart, 6), MusicJsonContext.Default.Double);
        settings["loopEnd"] = JsonSerializer.SerializeToElement(Math.Round(plan.LoopEnd, 6), MusicJsonContext.Default.Double);
        meta.Settings = settings;
        database.WriteMeta(outputPath, meta);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
[System.Text.Json.Serialization.JsonSerializable(typeof(bool))]
[System.Text.Json.Serialization.JsonSerializable(typeof(double))]
internal sealed partial class MusicJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
