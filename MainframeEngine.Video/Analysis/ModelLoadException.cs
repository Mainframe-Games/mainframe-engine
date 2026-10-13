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
}
