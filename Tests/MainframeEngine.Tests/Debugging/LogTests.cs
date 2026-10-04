namespace MainframeEngine.Tests.Debugging;

// Log writes to the process-wide Console and has a static level: run these serially.
[Collection(nameof(SerialConsole))]
public sealed class LogTests : IDisposable
{
    private readonly TextWriter _originalOut = Console.Out;
    private readonly Log.Level _originalLevel = Log.LogLevel;
    private readonly StringWriter _output = new();

    public LogTests() => Console.SetOut(_output);

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Log.LogLevel = _originalLevel;
        _output.Dispose();
    }

    [Fact]
    public void DefaultLevelEnablesEverythingExceptVerbose()
    {
        Assert.Equal((Log.Level)~0 & ~Log.Level.Verbose, _originalLevel);
    }

    [Fact]
    public void MessagesBelowTheLevelAreFiltered()
    {
        Log.LogLevel = Log.Level.Warning | Log.Level.Error;

        Log.Debug("debug-message");
        Log.Info("info-message");
        Log.Warning("warning-message");
        Log.Error("error-message");
        Log.Fatal(new InvalidOperationException("fatal-message"));

        var text = _output.ToString();
        Assert.DoesNotContain("debug-message", text, StringComparison.Ordinal);
        Assert.DoesNotContain("info-message", text, StringComparison.Ordinal);
        Assert.DoesNotContain("fatal-message", text, StringComparison.Ordinal);
        Assert.Contains("[WARN]\twarning-message", text, StringComparison.Ordinal);
        Assert.Contains("[ERROR]\terror-message", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoneSilencesEverything()
    {
        Log.LogLevel = Log.Level.None;

        Log.Error("error-message");

        Assert.Equal(string.Empty, _output.ToString());
    }

    [Fact]
    public void VerboseAppendsTheCallSite()
    {
        Log.LogLevel = Log.Level.Info | Log.Level.Verbose;

        Log.Info("located");

        var text = _output.ToString();
        Assert.Contains("located", text, StringComparison.Ordinal);
        Assert.Contains($"[{nameof(LogTests)}.cs:", text, StringComparison.Ordinal);
        Assert.Contains(nameof(VerboseAppendsTheCallSite), text, StringComparison.Ordinal);
    }
}

[CollectionDefinition(nameof(SerialConsole), DisableParallelization = true)]
public sealed class SerialConsole;
