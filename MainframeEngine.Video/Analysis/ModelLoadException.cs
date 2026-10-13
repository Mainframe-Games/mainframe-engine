namespace MainframeEngine.Video.Analysis;

/// <summary>The cached speech model failed to load, most likely because it is corrupt. The message is user-facing.</summary>
public sealed class ModelLoadException : Exception
{
    public ModelLoadException(string message, string modelPath, Exception inner)
        : base(message, inner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ModelPath = modelPath;
    }

    /// <summary>Path to the model file that failed to load.</summary>
    public string ModelPath { get; }

    /// <summary>
    /// True when it was Whisper's native library, not the model file, that failed to load: the library is missing, built
    /// for another architecture, or Whisper.net's own loader gave up. Deleting the model would not help then.
    /// </summary>
    public bool IsNativeLibraryFailure => IsNativeLoadFailure(InnerException);

    /// <summary>Whether <paramref name="error"/> or one of its inner exceptions is a native library load failure.</summary>
    public static bool IsNativeLoadFailure(Exception? error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                return true;
            }

            // Whisper.net's NativeLibraryLoader reports its failures with these messages, not with its own exception type.
            if (e.Message.StartsWith("Failed to load native whisper library", StringComparison.Ordinal)
                || e.Message.StartsWith("Native Library not found", StringComparison.Ordinal)
                || e.Message.StartsWith("Cannot load the library on this platform", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
