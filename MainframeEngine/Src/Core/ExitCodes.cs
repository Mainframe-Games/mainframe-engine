using System.Globalization;

namespace MainframeEngine;

/// <summary>
/// Names a process exit code for people (the editor's Play chip and Output row, crash reports): on Unix a code above 128
/// is a signal (<c>134</c> → <c>SIGABRT</c>, <c>139</c> → <c>SIGSEGV</c>), on Windows an NTSTATUS (<c>0xC0000005</c> →
/// access violation, <c>0xE0434352</c> → unhandled .NET exception).
/// </summary>
public static class ExitCodes
{
    /// <summary>The Windows status of an unhandled .NET exception (<c>0xE0434352</c>).</summary>
    public const uint ClrException = 0xE0434352;

    /// <summary>Describes <paramref name="code"/> as this operating system reports it.</summary>
    public static string Describe(int code) => Describe(code, OperatingSystem.IsWindows());

    /// <summary>Describes <paramref name="code"/> for Windows (<paramref name="windows"/>) or Unix.</summary>
    public static string Describe(int code, bool windows)
    {
        if (code == 0)
            return "exit code 0";
        if (windows)
        {
            var status = unchecked((uint)code);
            var name = status switch
            {
                0xC0000005 => "access violation",
                0xC00000FD => "stack overflow",
                0xC0000409 => "stack buffer overrun (fail fast)",
                0xC0000374 => "heap corruption",
                0xC000001D => "illegal instruction",
                0xC0000094 => "integer division by zero",
                0xC000013A => "closed with Ctrl+C",
                0x80000003 => "breakpoint",
                ClrException => "unhandled .NET exception",
                0xE0434F4D => "unhandled .NET exception (COM+)",
                0x40010004 => "killed (debugger terminated)",
                _ => null,
            };
            if (name is not null)
                return $"{name} (0x{status.ToString("X8", CultureInfo.InvariantCulture)})";
            return status >= 0xC0000000
                ? $"NTSTATUS 0x{status.ToString("X8", CultureInfo.InvariantCulture)}"
                : $"exit code {code.ToString(CultureInfo.InvariantCulture)}";
        }

        if (SignalName(code) is { } signal)
            return $"{signal}, {SignalMeaning(code - 128)} (exit code {code.ToString(CultureInfo.InvariantCulture)})";
        return $"exit code {code.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>The signal a Unix exit code above 128 stands for (<c>139</c> → <c>SIGSEGV</c>), or null.</summary>
    public static string? SignalName(int code)
    {
        // A shell reports death by signal N as 128 + N; .NET's Process.ExitCode does the same on Unix.
        if (code is <= 128 or > 128 + 64)
            return null;
        return (code - 128) switch
        {
            1 => "SIGHUP",
            2 => "SIGINT",
            3 => "SIGQUIT",
            4 => "SIGILL",
            5 => "SIGTRAP",
            6 => "SIGABRT",
            7 => OperatingSystem.IsLinux() ? "SIGBUS" : "SIGEMT",
            8 => "SIGFPE",
            9 => "SIGKILL",
            10 => OperatingSystem.IsLinux() ? "SIGUSR1" : "SIGBUS",
            11 => "SIGSEGV",
            13 => "SIGPIPE",
            14 => "SIGALRM",
            15 => "SIGTERM",
            var n => "signal " + n.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>True when <paramref name="code"/> means the process was killed by a crash (not an ordinary error exit).</summary>
    public static bool IsAbnormal(int code, bool windows)
    {
        if (windows)
            return unchecked((uint)code) >= 0xC0000000 || unchecked((uint)code) == ClrException;
        return code > 128 && code <= 128 + 64 && code - 128 is not (2 or 15);
    }

    private static string SignalMeaning(int signal) => signal switch
    {
        4 => "illegal instruction",
        6 => "aborted (a fail fast, a stack overflow or a native abort)",
        8 => "arithmetic error",
        9 => "killed",
        11 => "a native crash (segmentation fault)",
        7 or 10 => "bus error",
        2 => "interrupted",
        15 => "terminated",
        _ => "signalled",
    };
}
