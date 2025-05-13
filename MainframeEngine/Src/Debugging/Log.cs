namespace MainframeEngine;

public static class Log
{
    // static Log()
    // {
    //     Console.WriteLine($"This is {RED}Red{NORMAL}, {GREEN}Green{NORMAL}, {YELLOW}Yellow{NORMAL}, {BLUE}Blue{NORMAL}, {MAGENTA}Magenta{NORMAL}, {CYAN}Cyan{NORMAL}, {GREY}Grey{NORMAL}! ");
    //     Console.WriteLine($"This is {BOLD}Bold{NOBOLD}, {UNDERLINE}Underline{NOUNDERLINE}, {REVERSE}Reverse{NOREVERSE}! ");
    //     Print("Test");
    //     Info("Test");
    //     Warning("Test");
    //     Error("Test");
    //     Error(new Exception());
    // }
    
    private static readonly string NL          = Environment.NewLine;
    private static readonly string NORMAL      = Console.IsOutputRedirected ? "" : "\x1b[39m";
    private static readonly string RED         = Console.IsOutputRedirected ? "" : "\x1b[91m";
    private static readonly string GREEN       = Console.IsOutputRedirected ? "" : "\x1b[92m";
    private static readonly string YELLOW      = Console.IsOutputRedirected ? "" : "\x1b[93m";
    private static readonly string BLUE        = Console.IsOutputRedirected ? "" : "\x1b[94m";
    private static readonly string MAGENTA     = Console.IsOutputRedirected ? "" : "\x1b[95m";
    private static readonly string CYAN        = Console.IsOutputRedirected ? "" : "\x1b[96m";
    private static readonly string GREY        = Console.IsOutputRedirected ? "" : "\x1b[97m";
    private static readonly string BOLD        = Console.IsOutputRedirected ? "" : "\x1b[1m";
    private static readonly string NOBOLD      = Console.IsOutputRedirected ? "" : "\x1b[22m";
    private static readonly string UNDERLINE   = Console.IsOutputRedirected ? "" : "\x1b[4m";
    private static readonly string NOUNDERLINE = Console.IsOutputRedirected ? "" : "\x1b[24m";
    private static readonly string REVERSE     = Console.IsOutputRedirected ? "" : "\x1b[7m";
    private static readonly string NOREVERSE   = Console.IsOutputRedirected ? "" : "\x1b[27m";
    
    private static readonly string TimeStampColor = NORMAL;
    
    // private static string TimeStamp => DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss:fff");
    private static string TimeStamp => DateTime.Now.ToString("HH:mm:ss:fff");

    private static void PrintToConsole(string message, ConsoleColor color)
    {
        var c = Console.ForegroundColor;
        // Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = c;
    }
    
    public static void Print(string message)
    {
        PrintToConsole($"{TimeStampColor}[{TimeStamp}]{GREY} [Debug]\t{message}", Console.ForegroundColor);
    }
    
    public static void Info(string message)
    {
        PrintToConsole($"{TimeStampColor}[{TimeStamp}]{BLUE} [INFO]\t{message}", ConsoleColor.Cyan);
    }
    
    public static void Warning(string message)
    {
        PrintToConsole($"{TimeStampColor}[{TimeStamp}]{YELLOW} [WARN]\t{message}", ConsoleColor.Yellow);
    }
    
    public static void Error(string message)
    {
        PrintToConsole($"{TimeStampColor}[{TimeStamp}]{RED} [ERROR]\t{message}", ConsoleColor.Red);
    }
    
    public static void Error(Exception exception)
    {
        PrintToConsole($"{TimeStampColor}[{TimeStamp}]{RED} [FATAL]\t{exception}", ConsoleColor.Red);
    }
}