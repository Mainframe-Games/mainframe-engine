namespace MainframeEngine;

public static class Log
{
    public static void Print(string message, ConsoleColor color)
    {
        var c = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = c;
    }
    
    public static void Print(string message)
    {
        Print(message, Console.ForegroundColor);
    }
    
    public static void Info(string message)
    {
        Print($"[INFO]\t{message}", ConsoleColor.Cyan);
    }
    
    public static void Warning(string message)
    {
        Print($"[WARN]\t{message}", ConsoleColor.Yellow);
    }
    
    public static void Error(string message)
    {
        Print($"[ERROR]\t{message}", ConsoleColor.Red);
    }
}