using System.Runtime.CompilerServices;

namespace MainframeEngine;

public static class Log
{
    [Flags]
    public enum Level
    {
        None = 0,
        Debug = 1 << 0,
        Info = 1 << 1,
        Warning = 1 << 2,
        Error = 1 << 3,
        Fatal = 1 << 4,
        
        /// <summary>
        /// Includes sourceMemberName, sourceFile, sourceLineNumber
        /// </summary>
        Verbose = 1 << 5
    }
    
    static Log()
    {
        LogLevel |= Level.Verbose;
        
        Console.WriteLine($"This is {RED}Red{NORMAL}, {GREEN}Green{NORMAL}, {YELLOW}Yellow{NORMAL}, {BLUE}Blue{NORMAL}, {MAGENTA}Magenta{NORMAL}, {CYAN}Cyan{NORMAL}, {GREY}Grey{NORMAL}! ");
        Console.WriteLine($"This is {BOLD}Bold{NOBOLD}, {UNDERLINE}Underline{NOUNDERLINE}, {REVERSE}Reverse{NOREVERSE}! ");
        Debug("Test");
        Info("Test");
        Warning("Test");
        Error("Test");
        Fatal(new Exception("Test"));
    }
    
    private static readonly string NL          = Environment.NewLine;
    private static readonly string NORMAL      = Console.IsOutputRedirected ? string.Empty : "\x1b[39m";
    private static readonly string RED         = Console.IsOutputRedirected ? string.Empty : "\x1b[91m";
    private static readonly string GREEN       = Console.IsOutputRedirected ? string.Empty : "\x1b[92m";
    private static readonly string YELLOW      = Console.IsOutputRedirected ? string.Empty : "\x1b[93m";
    private static readonly string BLUE        = Console.IsOutputRedirected ? string.Empty : "\x1b[94m";
    private static readonly string MAGENTA     = Console.IsOutputRedirected ? string.Empty : "\x1b[95m";
    private static readonly string CYAN        = Console.IsOutputRedirected ? string.Empty : "\x1b[96m";
    private static readonly string GREY        = Console.IsOutputRedirected ? string.Empty : "\x1b[97m";
    private static readonly string BOLD        = Console.IsOutputRedirected ? string.Empty : "\x1b[1m";
    private static readonly string NOBOLD      = Console.IsOutputRedirected ? string.Empty : "\x1b[22m";
    private static readonly string UNDERLINE   = Console.IsOutputRedirected ? string.Empty : "\x1b[4m";
    private static readonly string NOUNDERLINE = Console.IsOutputRedirected ? string.Empty : "\x1b[24m";
    private static readonly string REVERSE     = Console.IsOutputRedirected ? string.Empty : "\x1b[7m";
    private static readonly string NOREVERSE   = Console.IsOutputRedirected ? string.Empty : "\x1b[27m";
    
    private static readonly string TimeStampColor = NORMAL;
    
    private static string TimeStamp => DateTime.Now.ToString("HH:mm:ss.fff");
    public static Level LogLevel = (Level)~0 & ~Level.Verbose;

    private static void PrintToConsole(string message, string color, string memberName, string sourceFilePath, int sourceLineNumber)
    {
        if (LogLevel.HasFlag(Level.Verbose))
        {
            var fileName = Path.GetFileName(sourceFilePath);
            Console.WriteLine($"{TimeStampColor}[{TimeStamp}]{color} {message} {TimeStampColor}[{fileName}:{sourceLineNumber} {memberName}]");
        }
        else
        {
            Console.WriteLine($"{TimeStampColor}[{TimeStamp}]{color} {message}");
        }
    }

    public static void Debug(
        string message,
        [CallerMemberName] string sourceMemberName = "",
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        if (LogLevel.HasFlag(Level.Debug))
            PrintToConsole($"[Debug]\t{message}", GREY, sourceMemberName, sourceFilePath, sourceLineNumber);
    }

    public static void Info(
        string message,
        [CallerMemberName] string sourceMemberName = "",
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        if (LogLevel.HasFlag(Level.Info))
            PrintToConsole($"[INFO]\t{message}", BLUE, sourceMemberName, sourceFilePath, sourceLineNumber);
    }

    public static void Warning(
        string message,
        [CallerMemberName] string sourceMemberName = "",
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        if (LogLevel.HasFlag(Level.Warning))
            PrintToConsole($"[WARN]\t{message}", YELLOW, sourceMemberName, sourceFilePath, sourceLineNumber);
    }

    public static void Error(
        string message,
        [CallerMemberName] string sourceMemberName = "",
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        if (LogLevel.HasFlag(Level.Error))
            PrintToConsole($"[ERROR]\t{message}", RED, sourceMemberName, sourceFilePath, sourceLineNumber);
    }

    public static void Fatal(
        Exception exception,
        [CallerMemberName] string sourceMemberName = "",
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        if (LogLevel.HasFlag(Level.Fatal))
            PrintToConsole($"[FATAL]\t{exception}", RED, sourceMemberName, sourceFilePath, sourceLineNumber);
    }
}