namespace MainframeEngine.UI.Rml;

/// <summary>A failed <c>mfrmlui</c> call: the status code and the library's last error message.</summary>
public sealed class RmlException : Exception
{
    public RmlException()
    {
    }

    public RmlException(string message) : base(message)
    {
    }

    public RmlException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public RmlException(string operation, int status) : base(Describe(operation, status))
    {
        Status = status;
    }

    /// <summary>The <c>MFRMLUI_ERROR_*</c> status (0 when not from a status code).</summary>
    public int Status { get; }

    internal static string Describe(string operation, int status) =>
        $"{operation} failed: {StatusName(status)} ({status}){(RmlCore.LastError is { Length: > 0 } e ? $": {e}" : "")}";

    internal static string StatusName(int status) => status switch
    {
        RmlNative.Ok => "OK",
        RmlNative.ErrorInvalidArgument => "invalid argument",
        RmlNative.ErrorNotInitialised => "not initialised",
        RmlNative.ErrorAlreadyInitialised => "already initialised",
        RmlNative.ErrorFailed => "failed",
        RmlNative.ErrorException => "native exception",
        RmlNative.ErrorInUse => "in use",
        RmlNative.ErrorNotFound => "not found",
        RmlNative.ErrorTypeMismatch => "type mismatch",
        _ => "unknown error",
    };

    /// <summary>Throws for a negative status.</summary>
    internal static void ThrowIfFailed(int status, string operation)
    {
        if (status < 0)
            throw new RmlException(operation, status);
    }
}
