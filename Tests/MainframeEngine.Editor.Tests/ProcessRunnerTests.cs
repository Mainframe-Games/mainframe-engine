using System.Diagnostics;

namespace MainframeEngine.Editor.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task OutputAndExitCodeAreComplete()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Uses /bin/sh.");

        var result = await ProcessRunner.RunAsync("/bin/sh", ["-c", "echo one; echo two >&2; echo three; exit 3"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal(["one", "three", "two"], result.Output.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AProcessLeftRunningWithTheOutputDoesNotHoldTheResult()
    {
        // What a cold `dotnet build` does: the MSBuild worker nodes it starts inherit its stdout/stderr and outlive it.
        if (OperatingSystem.IsWindows())
            Assert.Skip("Uses /bin/sh.");

        var stopwatch = Stopwatch.StartNew();
        var result = await ProcessRunner.RunAsync("/bin/sh", ["-c", "echo built; sleep 20 & exit 0"],
            timeout: TimeSpan.FromSeconds(15), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, $"exit {result.ExitCode}, timed out: {result.TimedOut}");
        Assert.Equal(["built"], result.Output);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
    }
}
