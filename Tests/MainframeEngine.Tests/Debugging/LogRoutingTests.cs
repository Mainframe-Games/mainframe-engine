using System.Globalization;

namespace MainframeEngine.Tests.Debugging;

// Sinks and the level are process-wide: serial with the other Log/Console tests. Other test classes may still log from
// their threads, so assertions look only at entries carrying this test's own markers.
[Collection(nameof(SerialConsole))]
public sealed class LogRoutingTests : IDisposable
{
    private readonly Log.Level _level = Log.LogLevel;
    private readonly MemoryLogSink _memory = new(256);
    private readonly TextWriter _out = Console.Out;
    private readonly StringWriter _console = new();

    public LogRoutingTests()
    {
        Console.SetOut(_console);
        Log.AddSink(_memory);
    }

    public void Dispose()
    {
        Log.RemoveSink(_memory);
        Log.AddSink(Log.ConsoleSink);
        Log.LogLevel = _level;
        Console.SetOut(_out);
        _console.Dispose();
    }

    private LogEntry[] Mine(string marker) => [.. _memory.Snapshot().Where(e => e.Message.Contains(marker, StringComparison.Ordinal))];

    [Fact]
    public void EntriesCarryLevelCategoryCallSiteAndUtcTime()
    {
        var before = DateTime.UtcNow;
        Log.Warning("[Audio] routing-1 device lost");

        var entry = Assert.Single(Mine("routing-1"));
        Assert.Equal(Log.Level.Warning, entry.Level);
        Assert.Equal("Audio", entry.Category);
        Assert.Equal("routing-1 device lost", entry.Message);
        Assert.Equal(nameof(EntriesCarryLevelCategoryCallSiteAndUtcTime), entry.CallerMember);
        Assert.EndsWith(nameof(LogRoutingTests) + ".cs", entry.CallerFile, StringComparison.Ordinal);
        Assert.True(entry.CallerLine > 0);
        Assert.Equal(DateTimeKind.Utc, entry.Timestamp.Kind);
        Assert.InRange(entry.Timestamp, before, DateTime.UtcNow);
    }

    [Theory]
    [InlineData("[UI] routing-2", "UI", "routing-2")]
    [InlineData("routing-2 no category", "", "routing-2 no category")]
    [InlineData("[x]routing-2", "", "[x]routing-2")]
    [InlineData("[two words] routing-2", "", "[two words] routing-2")]
    [InlineData("[Physics]\trouting-2", "Physics", "routing-2")]
    [InlineData("[] routing-2", "", "[] routing-2")]
    public void CategoryPrefixIsSplitOff(string message, string category, string body)
    {
        Log.Info(message);

        var entry = Assert.Single(Mine("routing-2"));
        Assert.Equal(category, entry.Category);
        Assert.Equal(body, entry.Message);
    }

    [Fact]
    public void CategoriesAreInterned()
    {
        Log.Info("[Interned] routing-3a");
        Log.Info("[Interned] routing-3b");

        Assert.Same(Mine("routing-3a")[0].Category, Mine("routing-3b")[0].Category);
    }

    [Fact]
    public void WriteTakesAnExplicitCategory()
    {
        Log.Write(Log.Level.Error, "Build", "routing-4 [not a category]");

        var entry = Assert.Single(Mine("routing-4"));
        Assert.Equal((Log.Level.Error, "Build", "routing-4 [not a category]"), (entry.Level, entry.Category, entry.Message));
    }

    [Fact]
    public void FilteredLevelsNeverReachSinks()
    {
        Log.LogLevel = Log.Level.Error;

        Log.Debug("routing-5");
        Log.Info($"routing-5 {42}");
        Log.Warning("routing-5");
        Log.Error("routing-5 kept");

        var entry = Assert.Single(Mine("routing-5"));
        Assert.Equal(Log.Level.Error, entry.Level);
        Assert.False(Log.IsEnabled(Log.Level.Verbose)); // a format flag, never a severity
    }

    [Fact]
    public void FilteredMessagesAllocateNothing()
    {
        Log.LogLevel = Log.Level.Error;
        var value = 1234.5f;
        var name = "player";
        void Window()
        {
            for (var i = 0; i < 1000; i++)
            {
                Log.Debug($"routing-6 frame {i} at {value:0.00} for {name}");
                Log.Info($"routing-6 {i}");
                Log.Warning($"routing-6 {value}");
                Log.Info("routing-6 constant");
            }
        }

        Window(); // JIT warm-up
        Assert.Equal(0, AllocationGate.SmallestWindow(Window));
        Assert.Empty(Mine("routing-6"));
    }

    [Fact]
    public void EnabledMessagesToAMemorySinkAllocateNothingBeyondTheirText()
    {
        Log.RemoveSink(Log.ConsoleSink);
        Log.LogLevel = Log.DefaultLevel;
        void Window()
        {
            for (var i = 0; i < 300; i++)
                Log.Info("routing-7 constant message");
        }

        Window();
        Assert.Equal(0, AllocationGate.SmallestWindow(Window));
        Assert.NotEmpty(Mine("routing-7"));
    }

    [Fact]
    public void InterpolatedMessagesUseTheInvariantCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var value = 1.5;
            Log.Info($"routing-8 {value}");
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        Assert.Equal("routing-8 1.5", Assert.Single(Mine("routing-8")).Message);
    }

    [Fact]
    public void AThrowingSinkDoesNotStopTheOthers()
    {
        var thrower = new ThrowingSink();
        Log.AddSink(thrower);
        var error = Console.Error;
        using var errors = new StringWriter();
        Console.SetError(errors);
        try
        {
            Log.Info("routing-9");
        }
        finally
        {
            Console.SetError(error);
            Log.RemoveSink(thrower);
        }

        Assert.Single(Mine("routing-9"));
        Assert.Contains("ThrowingSink failed", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ASinkThatLogsDoesNotRecurse()
    {
        var echo = new EchoSink();
        Log.AddSink(echo);
        try
        {
            Log.Info("routing-10");
        }
        finally
        {
            Log.RemoveSink(echo);
        }

        Assert.Equal(1, echo.Calls(e => e.Message.Contains("routing-10", StringComparison.Ordinal)));
        Assert.Single(Mine("routing-10"));
        Assert.Empty(Mine("echo of"));
    }

    [Fact]
    public void SinksAreAddedOnceAndRemoved()
    {
        var sink = new MemoryLogSink(8);
        Log.AddSink(sink);
        Log.AddSink(sink);
        Assert.Equal(1, Log.Sinks.Count(s => ReferenceEquals(s, sink)));
        Assert.True(Log.RemoveSink(sink));
        Assert.False(Log.RemoveSink(sink));

        Log.Info("routing-11");
        Assert.DoesNotContain(sink.Snapshot(), e => e.Message.Contains("routing-11", StringComparison.Ordinal));
    }

    [Fact]
    public void ConsoleOutputKeepsTheClassicFormat()
    {
        Log.Info("[Window] routing-12");

        Assert.Contains("[INFO]\t[Window] routing-12", _console.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DispatchReplaysAnEntryFromAnotherProcess()
    {
        var entry = new LogEntry(Log.Level.Error, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), "Game", "routing-13", "Main", "/g/Main.cs", 7);
        Log.Dispatch(entry);

        Assert.Equal(entry, Assert.Single(Mine("routing-13")));
    }

    private sealed class ThrowingSink : ILogSink
    {
        public void Write(in LogEntry entry) => throw new InvalidOperationException("boom");
    }

    private sealed class EchoSink : ILogSink
    {
        private readonly List<LogEntry> _seen = [];

        public void Write(in LogEntry entry)
        {
            lock (_seen)
                _seen.Add(entry);
            Log.Info("echo of " + entry.Message);
        }

        public int Calls(Func<LogEntry, bool> match)
        {
            lock (_seen)
                return _seen.Count(match);
        }
    }
}

public sealed class LogSinkTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mf-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static LogEntry Entry(string message, Log.Level level = Log.Level.Info, string category = "") =>
        new(level, new DateTime(2026, 10, 5, 1, 2, 3, 456, DateTimeKind.Utc), category, message, "Member", "/src/File.cs", 12);

    [Fact]
    public void MemorySinkKeepsTheNewestEntriesInOrder()
    {
        var sink = new MemoryLogSink(4);
        for (var i = 0; i < 6; i++)
            sink.Write(Entry("m" + i));

        Assert.Equal(4, sink.Count);
        Assert.Equal(6, sink.TotalWritten);
        Assert.Equal(["m2", "m3", "m4", "m5"], sink.Snapshot().Select(e => e.Message));
    }

    [Fact]
    public void MemorySinkCopySinceReadsOnlyNewEntries()
    {
        var sink = new MemoryLogSink(4);
        var buffer = new LogEntry[8];
        sink.Write(Entry("a"));
        sink.Write(Entry("b"));

        Assert.Equal(2, sink.CopySince(0, buffer, out var next));
        Assert.Equal(["a", "b"], buffer[..2].Select(e => e.Message));
        Assert.Equal(2, next);

        Assert.Equal(0, sink.CopySince(next, buffer, out next));
        for (var i = 0; i < 5; i++)
            sink.Write(Entry("n" + i));

        // 5 new entries but only 4 fit in the ring: the overwritten one is skipped.
        Assert.Equal(4, sink.CopySince(next, buffer, out next));
        Assert.Equal(["n1", "n2", "n3", "n4"], buffer[..4].Select(e => e.Message));
        Assert.Equal(7, next);

        // A small destination reads in pages.
        Assert.Equal(1, sink.CopySince(5, buffer.AsSpan(0, 1), out next));
        Assert.Equal("n3", buffer[0].Message);
        Assert.Equal(6, next);
    }

    [Fact]
    public void MemorySinkFiltersLevelsAndClears()
    {
        var sink = new MemoryLogSink(4) { Levels = Log.Level.Error | Log.Level.Fatal };
        sink.Write(Entry("info"));
        sink.Write(Entry("error", Log.Level.Error));

        Assert.Equal(["error"], sink.Snapshot().Select(e => e.Message));
        sink.Clear();
        Assert.Equal(0, sink.Count);
        Assert.Empty(sink.Snapshot());
    }

    [Fact]
    public void MemorySinkWritesWithoutAllocating()
    {
        var sink = new MemoryLogSink(16);
        var entry = Entry("steady");
        for (var i = 0; i < 32; i++)
            sink.Write(entry);

        Assert.Equal(0, AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 1000; i++)
                sink.Write(entry);
        }));
    }

    [Fact]
    public void FileSinkWritesUtf8LinesWithCallSitesForWarnings()
    {
        using (var sink = new FileLogSink(_directory, "game"))
        {
            sink.Write(Entry("hello ✓", category: "Audio"));
            sink.Write(Entry("careful", Log.Level.Warning));
            Assert.Equal(Path.Combine(_directory, "game.log"), sink.FilePath);
        }

        var lines = File.ReadAllText(Path.Combine(_directory, "game.log")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("2026-10-05T01:02:03.456Z [INFO] [Audio] hello ✓", lines[0]);
        Assert.Equal("2026-10-05T01:02:03.456Z [WARN] careful (File.cs:12 Member)", lines[1]);
        Assert.DoesNotContain('\r', File.ReadAllText(Path.Combine(_directory, "game.log")));
    }

    [Fact]
    public void FileSinkKeepsThePreviousRunsAndDropsTheOldest()
    {
        for (var run = 0; run < 4; run++)
        {
            using var sink = new FileLogSink(_directory, "game", maxFiles: 3);
            sink.Write(Entry("run " + run));
        }

        Assert.Equal(["game.1.log", "game.2.log", "game.log"],
            Directory.GetFiles(_directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Contains("run 3", File.ReadAllText(Path.Combine(_directory, "game.log")), StringComparison.Ordinal);
        Assert.Contains("run 2", File.ReadAllText(Path.Combine(_directory, "game.1.log")), StringComparison.Ordinal);
        Assert.Contains("run 1", File.ReadAllText(Path.Combine(_directory, "game.2.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void FileSinkRotatesWhenTheFileGrowsTooLarge()
    {
        using (var sink = new FileLogSink(_directory, "big", maxBytes: 2048, maxFiles: 3))
        {
            for (var i = 0; i < 200; i++)
                sink.Write(Entry($"line {i} " + new string('x', 40)));
        }

        var files = Directory.GetFiles(_directory);
        Assert.Equal(3, files.Length);
        Assert.All(files, f => Assert.InRange(new FileInfo(f).Length, 1, 2048 + 200));
        Assert.Contains("line 199", File.ReadAllText(Path.Combine(_directory, "big.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void FileSinkIgnoresWritesAfterDispose()
    {
        var sink = new FileLogSink(_directory);
        sink.Dispose();
        sink.Write(Entry("late"));
        sink.Dispose();

        Assert.DoesNotContain("late", File.ReadAllText(sink.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void LogEntryFormatsOneLine()
    {
        Assert.Equal("2026-10-05T01:02:03.456Z [ERROR] [Net] lost (File.cs:12 Member)",
            Entry("lost", Log.Level.Error, "Net").Format(includeCallSite: true));
        Assert.Equal("2026-10-05T01:02:03.456Z [Debug] x", Entry("x", Log.Level.Debug).Format(includeCallSite: false));
    }
}

[Collection(nameof(SerialEnvironment))]
public sealed class UserDataPathsTests
{
    [Fact]
    public void OverrideVariableReplacesTheBaseFolder()
    {
        var previous = Environment.GetEnvironmentVariable(UserDataPaths.OverrideVariable);
        var root = Path.Combine(Path.GetTempPath(), "mf-userdata");
        try
        {
            Environment.SetEnvironmentVariable(UserDataPaths.OverrideVariable, root);
            Assert.Equal(Path.GetFullPath(root), UserDataPaths.BaseDirectory);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "My Game", "logs"), UserDataPaths.LogDirectory("My Game"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(UserDataPaths.OverrideVariable, previous);
        }
    }

    [Fact]
    public void DefaultBaseFolderIsPerUserAndPerOs()
    {
        var previous = Environment.GetEnvironmentVariable(UserDataPaths.OverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(UserDataPaths.OverrideVariable, null);
            var path = UserDataPaths.BaseDirectory;
            Assert.True(Path.IsPathRooted(path));
            if (OperatingSystem.IsMacOS())
                Assert.EndsWith(Path.Combine("Library", "Application Support"), path, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(UserDataPaths.OverrideVariable, previous);
        }
    }

    [Theory]
    [InlineData("My Game", "My Game")]
    [InlineData("a/b\\c:d", "a_b_c_d")]
    [InlineData("  ..  ", "game")]
    [InlineData("Trailing.", "Trailing")]
    public void SafeNameReplacesInvalidCharacters(string name, string expected) =>
        Assert.Equal(expected, UserDataPaths.SafeName(name));
}

[CollectionDefinition(nameof(SerialEnvironment), DisableParallelization = true)]
public sealed class SerialEnvironment;
