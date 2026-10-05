namespace MainframeEngine.Editor;

/// <summary>A user-facing Demo download failure (the message is shown in the dialog).</summary>
public sealed class DemoDownloadException(string message, Exception? inner = null) : Exception(message, inner);
