using System.Text;

namespace MainframeEngine.Tests.Saves;

public sealed class DurableFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mf-durable-tests", Guid.NewGuid().ToString("N"));

    public DurableFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string PathOf(string name) => Path.Combine(_directory, name);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void TheFirstWriteCreatesTheFolderAndLeavesNoTemp()
    {
        var path = Path.Combine(_directory, "a", "b", "slot1.msave");

        DurableFile.Write(path, Bytes("v1"), backups: 2);

        Assert.Equal("v1", File.ReadAllText(path));
        Assert.False(File.Exists(DurableFile.TempPath(path)));
        Assert.False(File.Exists(DurableFile.BackupPath(path, 1)));
    }

    [Fact]
    public void BackupsRollAndTheOldestFallsOff()
    {
        var path = PathOf("slot1.msave");
        for (var i = 1; i <= 5; i++)
            DurableFile.Write(path, Bytes("v" + i), backups: 2);

        Assert.Equal("v5", File.ReadAllText(path));
        Assert.Equal("v4", File.ReadAllText(DurableFile.BackupPath(path, 1)));
        Assert.Equal("v3", File.ReadAllText(DurableFile.BackupPath(path, 2)));
        Assert.False(File.Exists(DurableFile.BackupPath(path, 3)));
        Assert.Equal([path, DurableFile.BackupPath(path, 1), DurableFile.BackupPath(path, 2)], DurableFile.RecoveryCandidates(path, 2));
    }

    [Fact]
    public void ZeroBackupsReplacesInPlace()
    {
        var path = PathOf("project.mfproj");
        DurableFile.Write(path, Bytes("a"));
        DurableFile.Write(path, Bytes("b"));

        Assert.Equal("b", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void BackupCountIsValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DurableFile.Write(PathOf("x"), Bytes("a"), -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DurableFile.Write(PathOf("x"), Bytes("a"), DurableFile.MaxBackups + 1));
    }

    /// <summary>
    /// A crash between any two file-system steps (or half-way through writing the temp file) never loses both versions:
    /// the first complete copy in recovery order is the old or the new content.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ACrashAtAnyStepLeavesAValidCopy(int backups)
    {
        for (var crashAt = 0; ; crashAt++)
        {
            var path = PathOf($"crash-{backups}-{crashAt}.msave");
            DurableFile.Write(path, Bytes("old-old"), backups);
            DurableFile.Write(path, Bytes("old"), backups);

            var ops = new CrashingOps(crashAt);
            var crashed = false;
            try
            {
                DurableFile.Write(path, Bytes("new"), backups, ops);
            }
            catch (SimulatedCrash)
            {
                crashed = true;
            }

            var first = DurableFile.RecoveryCandidates(path, backups).Select(File.ReadAllText).FirstOrDefault(IsComplete);
            if (!crashed)
            {
                Assert.Equal("new", first); // ran to completion: every step was covered
                Assert.True(crashAt >= 2); // at least the temp write and the replace were interrupted once
                return;
            }

            Assert.True(first is "old" or "new", $"crash at step {crashAt} with {backups} backups left '{first ?? "<nothing>"}'");
        }
    }

    [Fact]
    public void RestoreMakesABackupTheMainFileAgain()
    {
        var path = PathOf("slot1.msave");
        DurableFile.Write(path, Bytes("v1"), backups: 1);
        DurableFile.Write(path, Bytes("v2"), backups: 1);
        File.WriteAllText(path, "garbage");

        var quarantined = DurableFile.QuarantineCorrupt(path);
        DurableFile.Restore(path, DurableFile.BackupPath(path, 1), backups: 1);

        Assert.Equal("v1", File.ReadAllText(path));
        Assert.NotNull(quarantined);
        Assert.Contains(".corrupt-", quarantined, StringComparison.Ordinal);
        Assert.Equal("garbage", File.ReadAllText(quarantined));
        Assert.Null(DurableFile.QuarantineCorrupt(PathOf("missing")));
    }

    private static bool IsComplete(string text) => text is "old-old" or "old" or "new";

    private sealed class SimulatedCrash : Exception;

    /// <summary>Real file operations that "lose power" before the n-th step (a write is cut in half).</summary>
    private sealed class CrashingOps(int crashAt) : DurableFile.IDurableFileOps
    {
        private int _step;

        private void Step()
        {
            if (_step++ == crashAt)
                throw new SimulatedCrash();
        }

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public void WriteFlushed(string path, ReadOnlySpan<byte> bytes)
        {
            if (_step == crashAt)
                File.WriteAllBytes(path, bytes[..(bytes.Length / 2)].ToArray()); // torn write
            Step();
            DurableFile.FileOps.Real.WriteFlushed(path, bytes);
        }

        public bool Exists(string path) => File.Exists(path);

        public void Move(string from, string to, bool overwrite)
        {
            Step();
            File.Move(from, to, overwrite);
        }

        public void Replace(string source, string destination, string backup)
        {
            Step();
            DurableFile.FileOps.Real.Replace(source, destination, backup);
        }

        public void Delete(string path)
        {
            Step();
            File.Delete(path);
        }
    }
}
