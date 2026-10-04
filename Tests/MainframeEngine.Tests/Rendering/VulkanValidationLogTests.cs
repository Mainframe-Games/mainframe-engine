namespace MainframeEngine.Tests.Rendering;

[Collection(nameof(Debugging.SerialConsole))] // Record also writes to Log/Console
public sealed class VulkanValidationLogTests : IDisposable
{
    private readonly Log.Level _level = Log.LogLevel;

    public VulkanValidationLogTests() => Log.LogLevel = Log.Level.None;

    public void Dispose() => Log.LogLevel = _level;

    [Fact]
    public void RecordCountsBySeverity()
    {
        var log = new VulkanValidationLog();

        log.Record(isError: false, "w1");
        log.Record(isError: true, "e1");
        log.Record(isError: false, "w2");

        Assert.Equal(2, log.WarningCount);
        Assert.Equal(1, log.ErrorCount);
        Assert.Equal(["WARNING: w1", "ERROR: e1", "WARNING: w2"], log.Messages);
    }

    [Fact]
    public void StoredMessagesAreCappedButCountsAreNot()
    {
        var log = new VulkanValidationLog();

        for (var i = 0; i < VulkanValidationLog.MaxStoredMessages + 10; i++)
            log.Record(isError: true, "e");

        Assert.Equal(VulkanValidationLog.MaxStoredMessages + 10, log.ErrorCount);
        Assert.Equal(VulkanValidationLog.MaxStoredMessages, log.Messages.Count);
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var log = new VulkanValidationLog();
        log.Record(isError: true, "e");

        log.Reset();

        Assert.Equal(0, log.ErrorCount);
        Assert.Equal(0, log.WarningCount);
        Assert.Empty(log.Messages);
    }
}
