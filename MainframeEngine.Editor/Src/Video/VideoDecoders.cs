using System.ComponentModel;
using System.Diagnostics;
using MainframeEngine.Video.Media;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// One running preview decode: raw RGBA frames or float stereo samples on <see cref="Output"/>. It is an interface so the
/// preview tests run on a fake, never on ffmpeg. Disposing it kills the process; a reader blocked on
/// <see cref="Output"/> then sees the end of the stream.
/// </summary>
public interface IVideoDecoder : IDisposable
{
    /// <summary>The decoded bytes. One thread reads it.</summary>
    Stream Output { get; }

    /// <summary>True once the decode has failed: it could not start, or it exited with an error. False once disposed.</summary>
    bool Failed { get; }
}

/// <summary>Starts preview decodes. The preview's audio task calls it too, so implementations must be thread-safe.</summary>
public interface IVideoDecoderFactory
{
    /// <summary>
    /// Starts a decode with <see cref="MainframeEngine.Video.Media.Preview.PreviewDecoding"/>'s arguments. A decode that
    /// cannot start comes back as a decoder whose <see cref="IVideoDecoder.Failed"/> is true instead of throwing.
    /// </summary>
    IVideoDecoder Start(IReadOnlyList<string> arguments);
}

/// <summary>Runs the user's ffmpeg for each decode. A missing binary gives a decoder whose <see cref="IVideoDecoder.Failed"/> is true.</summary>
public sealed class FfmpegVideoDecoderFactory(string ffmpegPath) : IVideoDecoderFactory
{
    public IVideoDecoder Start(IReadOnlyList<string> arguments) => new FfmpegVideoDecoder(ffmpegPath, arguments);
}

/// <summary>
/// An ffmpeg process with stdout as <see cref="Output"/>. Its stderr is drained into a string (ffmpeg blocks once that
/// pipe fills), which <see cref="ToString"/> reports when the decode fails.
/// </summary>
internal sealed class FfmpegVideoDecoder : IVideoDecoder
{
    private readonly Process? _process;
    private readonly Task<string>? _standardError;
    private readonly string? _startError;
    private int _disposed;

    public FfmpegVideoDecoder(string ffmpegPath, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(ffmpegPath);
        ArgumentNullException.ThrowIfNull(arguments);
        try
        {
            _process = FfmpegProcess.Start(ffmpegPath, arguments, redirectStandardOutput: true);
        }
        catch (Win32Exception e)
        {
            // No such binary (or not executable): a failed decoder, so the preview logs it and pauses.
            _startError = $"could not run ffmpeg at {ffmpegPath}: {e.Message}";
            Output = Stream.Null;
            return;
        }

        _standardError = _process.StandardError.ReadToEndAsync();
        // Disposing the process closes stderr under the pending read; observe that fault so it is not reported as unobserved.
        _standardError.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        Output = _process.StandardOutput.BaseStream;
    }

    public Stream Output { get; }

    public bool Failed
    {
        get
        {
            if (_startError is not null)
                return true;
            if (Volatile.Read(ref _disposed) != 0)
                return false;
            try
            {
                return _process!.HasExited && _process.ExitCode != 0;
            }
            catch (InvalidOperationException)
            {
                return false; // disposed on another thread between the check and the read: it was stopped, not failed
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || _process is null)
            return;
        FfmpegProcess.TryKill(_process);
        _process.Dispose();
    }

    /// <summary>Why the decode failed: the start error, or the exit code and the last lines ffmpeg printed.</summary>
    public override string ToString()
    {
        if (_startError is not null)
            return _startError;
        int exitCode;
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || _process is null || !_process.HasExited)
                return "ffmpeg decode";
            exitCode = _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return "ffmpeg decode"; // disposed on another thread meanwhile
        }

        var stderr = _standardError is { IsCompletedSuccessfully: true } done ? done.Result.Trim() : "";
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tail = lines.Length == 0 ? "" : ": " + string.Join(' ', lines[Math.Max(0, lines.Length - 3)..]);
        return $"ffmpeg exited with code {exitCode}{tail}";
    }
}

/// <summary>A decode that never started (the factory threw): <see cref="Failed"/> from the outset, with the reason.</summary>
internal sealed class FailedVideoDecoder(string reason) : IVideoDecoder
{
    public Stream Output => Stream.Null;

    public bool Failed => true;

    public void Dispose()
    {
    }

    public override string ToString() => reason;
}
