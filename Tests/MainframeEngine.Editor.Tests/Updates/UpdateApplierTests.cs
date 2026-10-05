using System.Diagnostics;

namespace MainframeEngine.Editor.Tests.Updates;

public sealed class UpdateApplierTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-apply").FullName;
    private readonly List<ProcessStartInfo> _started = [];
    private readonly List<string> _log = [];

    public UpdateApplierTests()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "old.txt"), "old");
        Directory.CreateDirectory(Path.Combine(Staged, "Content"));
        File.WriteAllText(Path.Combine(Staged, "MainframeEngine.Editor"), "new exe");
        File.WriteAllText(Path.Combine(Staged, "Content", "new.txt"), "new");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Root => Path.Combine(_directory, "My Tools", "MainframeEngine-1.0.0-linux-x64");
    private string Staged => Path.Combine(_directory, "updates", "1.1.0", "app", "MainframeEngine-1.1.0-linux-x64");
    private string Updates => Path.Combine(_directory, "updates");
    private string Backup => Root + ".old";

    private UpdateApplier Applier(Func<int, TimeSpan, bool>? wait = null, Action<string, string>? copy = null) => new()
    {
        StagedRoot = Staged,
        Rid = "linux-x64",
        ToVersion = "1.1.0",
        UpdatesDirectory = Updates,
        WaitForExit = wait ?? ((_, _) => true),
        Start = _started.Add,
        CopyDirectory = copy ?? UpdateApplier.CopyDirectoryRecursive,
        Log = _log.Add,
    };

    private ApplyUpdateRequest Request(string? project = null) => new(Root, 4242, "1.0.0", project);

    [Fact]
    public void ReplacesTheInstallRecordsAndRelaunches()
    {
        var project = Path.Combine(_directory, "My Games", "Space Game");
        Assert.Equal(0, Applier().Apply(Request(project)));

        Assert.Equal("new", File.ReadAllText(Path.Combine(Root, "Content", "new.txt")));
        Assert.False(File.Exists(Path.Combine(Root, "old.txt")));
        Assert.True(File.Exists(Path.Combine(Backup, "old.txt"))); // the next start deletes it
        var result = UpdateResult.Load(UpdatePaths.ResultFile(Updates))!;
        Assert.Equal(new UpdateResult("1.0.0", "1.1.0", true, null), result);
        var start = Assert.Single(_started);
        Assert.Equal(Path.Combine(Root, "MainframeEngine.Editor"), start.FileName);
        Assert.Equal(["--project", project], start.ArgumentList);
        Assert.False(start.UseShellExecute);
    }

    [Fact]
    public void AnOldEditorThatDoesNotExitChangesNothing()
    {
        Assert.Equal(1, Applier(wait: (_, _) => false).Apply(Request()));
        Assert.True(File.Exists(Path.Combine(Root, "old.txt")));
        Assert.False(Directory.Exists(Backup));
        Assert.False(UpdateResult.Load(UpdatePaths.ResultFile(Updates))!.Ok);
        Assert.Equal(Path.Combine(Root, "MainframeEngine.Editor"), Assert.Single(_started).FileName); // the old editor again
    }

    [Fact]
    public void ACopyFailureRestoresThePreviousVersion()
    {
        void FailHalfway(string from, string to)
        {
            Directory.CreateDirectory(to);
            File.WriteAllText(Path.Combine(to, "partial.txt"), "x");
            throw new IOException("Disk full");
        }

        Assert.Equal(1, Applier(copy: FailHalfway).Apply(Request()));

        Assert.True(File.Exists(Path.Combine(Root, "old.txt")));
        Assert.False(File.Exists(Path.Combine(Root, "partial.txt")));
        Assert.False(Directory.Exists(Backup));
        var result = UpdateResult.Load(UpdatePaths.ResultFile(Updates))!;
        Assert.False(result.Ok);
        Assert.Contains("Disk full", result.Error, StringComparison.Ordinal);
        Assert.Single(_started);
    }

    [Fact]
    public void AnUnexpectedCopyFailureRestoresThePreviousVersion()
    {
        static void Fail(string from, string to) => throw new NotSupportedException("Unsupported path");

        Assert.Equal(1, Applier(copy: Fail).Apply(Request()));

        Assert.True(File.Exists(Path.Combine(Root, "old.txt")));
        Assert.False(Directory.Exists(Backup));
        Assert.Contains("Unsupported path", UpdateResult.Load(UpdatePaths.ResultFile(Updates))!.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelaunchFailureRestoresThePreviousVersion()
    {
        var applier = Applier();
        var attempts = 0;
        applier = new UpdateApplier
        {
            StagedRoot = applier.StagedRoot,
            Rid = applier.Rid,
            ToVersion = applier.ToVersion,
            UpdatesDirectory = applier.UpdatesDirectory,
            WaitForExit = (_, _) => true,
            Start = info =>
            {
                if (attempts++ == 0)
                    throw new System.ComponentModel.Win32Exception("cannot start");
                _started.Add(info);
            },
            Log = _log.Add,
        };

        Assert.Equal(1, applier.Apply(Request()));
        Assert.True(File.Exists(Path.Combine(Root, "old.txt")));
        Assert.False(UpdateResult.Load(UpdatePaths.ResultFile(Updates))!.Ok); // the failure overwrote the success record
        Assert.Single(_started); // the old editor
    }

    [Fact]
    public void AStaleBackupFromAnEarlierRunIsReplaced()
    {
        Directory.CreateDirectory(Backup);
        File.WriteAllText(Path.Combine(Backup, "stale.txt"), "stale");
        Assert.Equal(0, Applier().Apply(Request()));
        Assert.False(File.Exists(Path.Combine(Backup, "stale.txt")));
        Assert.True(File.Exists(Path.Combine(Backup, "old.txt")));
    }

    [Fact]
    public void AProcessThatIsAlreadyGoneCountsAsExited()
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true })!;
        process.WaitForExit();
        Assert.True(UpdateApplier.DefaultWaitForExit(process.Id, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void MacRelaunchesGoThroughOpen()
    {
        var info = UpdateApplier.LaunchInfo("/Applications/Mainframe Engine.app", "osx-arm64", "/Users/me/My Games/Space");
        Assert.Equal("open", info.FileName);
        Assert.Equal(["-n", "/Applications/Mainframe Engine.app", "--args", "--project", "/Users/me/My Games/Space"], info.ArgumentList);
        Assert.Equal(["-n", "/Applications/Mainframe Engine.app"], UpdateApplier.LaunchInfo("/Applications/Mainframe Engine.app", "osx-arm64", null).ArgumentList);
    }

    [Fact]
    public void RequestsRoundTripThroughArguments()
    {
        var request = new ApplyUpdateRequest(Path.GetFullPath(Root), 99, "1.0.0", "/p/My Game");
        var arguments = request.ToArguments();
        Assert.Equal(ApplyUpdateRequest.Flag, arguments[0]);
        Assert.True(ApplyUpdateRequest.IsApplyUpdate(arguments));
        Assert.Equal(request, ApplyUpdateRequest.Parse(arguments));
        Assert.Equal(request with { Project = null }, ApplyUpdateRequest.Parse((request with { Project = null }).ToArguments()));
    }

    [Theory]
    [InlineData("--apply-update")]
    [InlineData("--apply-update", "/x")]
    [InlineData("--apply-update", "/x", "--wait-pid", "abc", "--from", "1.0.0")]
    [InlineData("--apply-update", "/x", "--wait-pid", "1")]
    [InlineData("--apply-update", "/x", "--wait-pid", "1", "--from", "1.0.0", "--bogus")]
    public void BadArgumentsAreRejected(params string[] args) =>
        Assert.Throws<ArgumentException>(() => ApplyUpdateRequest.Parse(args));

    [Fact]
    public void TheNextStartReportsAndCleansUp()
    {
        Directory.CreateDirectory(Backup);
        Directory.CreateDirectory(Path.Combine(Updates, "1.1.0", "app"));
        Directory.CreateDirectory(Path.Combine(Updates, "junk"));
        File.WriteAllText(UpdatePaths.LogFile(Updates), "log");
        new UpdateResult("1.0.0", "1.1.0", true, null).Save(UpdatePaths.ResultFile(Updates));

        var result = UpdateCleanup.Run(Root, Updates, new ReleaseVersion(1, 1, 0));

        Assert.Equal(new UpdateResult("1.0.0", "1.1.0", true, null), result);
        Assert.False(File.Exists(UpdatePaths.ResultFile(Updates)));
        Assert.False(Directory.Exists(Backup));
        Assert.False(Directory.Exists(Path.Combine(Updates, "1.1.0")));
        Assert.False(Directory.Exists(Path.Combine(Updates, "junk")));
        Assert.True(File.Exists(UpdatePaths.LogFile(Updates)));
        Assert.Null(UpdateCleanup.Run(Root, Updates, new ReleaseVersion(1, 1, 0))); // nothing to report the second time
    }

    [Fact]
    public void CleanUpKeepsAnotherEditorsDownloadOfANewerVersion()
    {
        Directory.CreateDirectory(Path.Combine(Updates, "1.2.0", "app"));
        Directory.CreateDirectory(Path.Combine(Updates, "1.0.0", "app"));
        UpdateCleanup.Run(Root, Updates, new ReleaseVersion(1, 1, 0));
        Assert.True(Directory.Exists(Path.Combine(Updates, "1.2.0")));
        Assert.False(Directory.Exists(Path.Combine(Updates, "1.0.0")));
    }

    [Fact]
    public void CleanUpDoesNotThrowWhenTheUpdatesFolderCannotBeListed()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Needs POSIX permissions to make a folder unlistable.");
            return;
        }

        Directory.CreateDirectory(Path.Combine(Updates, "1.0.0"));
        var original = File.GetUnixFileMode(Updates);
        File.SetUnixFileMode(Updates, UnixFileMode.None);
        try
        {
            try
            {
                _ = Directory.GetDirectories(Updates);
                Assert.Skip("The folder is still listable (running as root?).");
            }
            catch (UnauthorizedAccessException)
            {
            }

            Assert.Null(UpdateCleanup.Run(Root, Updates, new ReleaseVersion(1, 1, 0)));
        }
        finally
        {
            File.SetUnixFileMode(Updates, original);
        }
    }
}
