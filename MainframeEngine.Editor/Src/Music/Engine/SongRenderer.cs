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
/// the loop end, with the loop region as the file's loop points. Writes 32-bit float WAV to a temporary name and then
/// replaces the output (a failed render leaves the previous file intact), then the output's <c>.meta</c> import settings
/// (<c>loop</c>, <c>loopStart</c>, <c>loopEnd</c> in seconds, <c>loadMode: Stream</c>).
/// </summary>
/// <remarks>
/// Encoding to <c>.ogg</c> needs the plugin helper's Vorbis encoder (a later phase): an output named <c>.ogg</c> is
/// written as <c>.wav</c> next to it, and the log says so.
/// </remarks>
public static class SongRenderer
{
    public const string DefaultFolder = "Content/Music";

    /// <summary>The project path the song renders to: its setting, else <c>Content/Music/&lt;name&gt;.wav</c>; always <c>.wav</c> for now.</summary>
    public static string OutputPathFor(Song song, string songName)
    {
        ArgumentNullException.ThrowIfNull(song);
        var output = string.IsNullOrWhiteSpace(song.Render.Output) ? $"{DefaultFolder}/{songName}.wav" : song.Render.Output.Replace('\\', '/');
        return Path.ChangeExtension(output, ".wav");
    }

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
    public static float[] RenderToBuffer(Song song, out SongRenderPlan plan, Func<SongTrack, int, IInstrument?>? instrumentFactory = null,
        Func<string, AudioStream?>? clipLoader = null, IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(song);
        var rate = Math.Clamp(song.Render.SampleRate, 8000, 192000);
        var builder = new SongSnapshotBuilder(rate, instrumentFactory, clipLoader);
        var snapshot = builder.Build(song);
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
    /// <c>.meta</c>. <paramref name="songName"/> names the default output (the song file's name).
    /// </summary>
    public static SongRenderResult Render(Song song, string songName, AssetDatabase database,
        Func<SongTrack, int, IInstrument?>? instrumentFactory = null, Func<string, AudioStream?>? clipLoader = null,
        IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(database);
        var requested = string.IsNullOrWhiteSpace(song.Render.Output) ? null : song.Render.Output;
        var output = OutputPathFor(song, songName);
        if (requested is not null && !string.Equals(Path.GetExtension(requested), ".wav", StringComparison.OrdinalIgnoreCase))
            Log.Warning($"[Music] '{requested}': encoding to {Path.GetExtension(requested)} needs the plugin helper (not available yet); rendering '{output}' instead.");

        var samples = RenderToBuffer(song, out var plan, instrumentFactory, clipLoader, progress, cancellation);
        var rate = Math.Clamp(song.Render.SampleRate, 8000, 192000);
        var full = database.ToAbsolutePath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".rendering.tmp";
        try
        {
            WavWriter.Write(temp, samples, 2, rate, WavSampleFormat.IeeeFloat);
            cancellation.ThrowIfCancellationRequested();
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }

        WriteMeta(database, output, plan);
        var result = new SongRenderResult(database.ToProjectPath(full), plan.OutputFrames, rate, plan.Loop, plan.LoopStart, plan.LoopEnd);
        Log.Info($"[Music] Rendered '{songName}' to {result.OutputPath} (WAV, 32-bit float, {result.Seconds:0.00} s{(plan.Loop ? ", loops" : "")}).");
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
