using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace MainframeEngine.Tests.Saves;

public sealed record TestSave(string Name, int Score, List<int> Items);

public sealed record OldSave(string Name);

public sealed record BigSave(string Data);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TestSave))]
[JsonSerializable(typeof(OldSave))]
[JsonSerializable(typeof(BigSave))]
internal sealed partial class SaveTestJson : JsonSerializerContext;

/// <summary>A scratch save folder and helpers shared by the save tests.</summary>
public abstract class SaveTestBase : IDisposable
{
    protected static readonly SaveSchema<TestSave> Schema = new(SaveTestJson.Default.TestSave, version: 1);

    private readonly List<SaveServer> _servers = [];

    protected SaveTestBase() => Directory.CreateDirectory(Root);

    /// <summary>The storage root (a fresh temporary folder per test).</summary>
    protected string Root { get; } = Path.Combine(Path.GetTempPath(), "mf-save-tests", Guid.NewGuid().ToString("N"));

    protected string PathOf(string name) => Path.Combine(Root, name);

    protected SaveServer CreateServer(SaveOptions? options = null, SceneTree? tree = null, ISaveStorage? storage = null)
    {
        var server = new SaveServer(storage ?? new LocalSaveStorage(Root), options ?? new SaveOptions { GameName = "Test Game", GameVersion = "1.2.0" }, tree);
        _servers.Add(server);
        return server;
    }

    protected static TestSave Data(string name, int score = 1) => new(name, score, [1, 2, 3]);

    /// <summary>Runs <see cref="SaveServer.Process"/> on this thread until <paramref name="task"/> completes.</summary>
    protected static T Pump<T>(SaveServer server, Task<T> task)
    {
        Pump(server, (Task)task);
        return task.Result;
    }

    protected static void Pump(SaveServer server, Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The save server did not complete the task.");
            server.Process(new GameTime());
            Thread.Sleep(1);
        }

        task.GetAwaiter().GetResult();
    }

    /// <summary>Polls <paramref name="condition"/> (background work such as uploads) for up to 15 s.</summary>
    protected static void WaitFor(Func<bool> condition, SaveServer? pump = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition did not become true.");
            pump?.Process(new GameTime());
            Thread.Sleep(2);
        }
    }

    /// <summary>Extra clean-up after the servers are disposed and the folder deleted.</summary>
    protected virtual void Cleanup()
    {
    }

    public void Dispose()
    {
        foreach (var server in _servers)
            server.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }

        Cleanup();
    }
}

/// <summary>Wraps a storage: counts the bytes read and the writes per path, and can hold writes at a gate.</summary>
internal sealed class InstrumentedStorage(ISaveStorage inner) : ISaveStorage
{
    private long _bytesRead;

    public ConcurrentDictionary<string, int> Writes { get; } = new(StringComparer.Ordinal);

    public long BytesRead => Interlocked.Read(ref _bytesRead);

    /// <summary>When set, a write of a <c>.msave</c> signals <see cref="Entered"/> then waits for <see cref="Gate"/>.</summary>
    public ManualResetEventSlim? Gate { get; set; }

    public ManualResetEventSlim Entered { get; } = new();

    public TimeSpan WriteDelay { get; set; }

    public string Name => "instrumented";

    public IReadOnlyList<string> List(string folder) => inner.List(folder);

    public Stream? OpenRead(string path) => inner.OpenRead(path) is { } stream ? new CountingStream(stream, this) : null;

    public void WriteDurable(string path, ReadOnlySpan<byte> bytes, int backups)
    {
        if (path.EndsWith(SaveServer.FileExtension, StringComparison.Ordinal))
        {
            Entered.Set();
            Gate?.Wait(TimeSpan.FromSeconds(15));
            if (WriteDelay > TimeSpan.Zero)
                Thread.Sleep(WriteDelay);
        }

        Writes.AddOrUpdate(path, 1, static (_, n) => n + 1);
        inner.WriteDurable(path, bytes, backups);
    }

    public bool Delete(string path) => inner.Delete(path);

    public DateTimeOffset? GetModifiedUtc(string path) => inner.GetModifiedUtc(path);

    public bool Move(string path, string newPath) => inner.Move(path, newPath);

    private sealed class CountingStream(Stream inner, InstrumentedStorage owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Interlocked.Add(ref owner._bytesRead, read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            Interlocked.Add(ref owner._bytesRead, read);
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>An in-memory <see cref="ICloudSaveBackend"/>: what the M13 backends implement, for tests.</summary>
public sealed class FakeCloudSaveBackend : ICloudSaveBackend
{
    private readonly ConcurrentDictionary<string, (byte[] File, byte[] Thumbnail)> _slots = new(StringComparer.Ordinal);
    private int _failures;
    private int _uploads;

    public string Name => "fake";

    public long MaxBytes { get; set; } = 1 << 20;

    /// <summary>The next uploads that throw.</summary>
    public int FailNextUploads
    {
        get => Volatile.Read(ref _failures);
        set => Volatile.Write(ref _failures, value);
    }

    /// <summary>Successful uploads so far.</summary>
    public int Uploads => Volatile.Read(ref _uploads);

    public bool Has(string slot) => _slots.ContainsKey(slot);

    public byte[] File(string slot) => _slots[slot].File;

    /// <summary>Puts a file into the cloud directly, as another device would.</summary>
    public void Put(string slot, byte[] file, byte[]? thumbnail = null) => _slots[slot] = (file, thumbnail ?? []);

    public Task<IReadOnlyList<CloudSaveMeta>> ListAsync(CancellationToken ct)
    {
        var list = new List<CloudSaveMeta>();
        foreach (var (_, entry) in _slots)
        {
            if (CloudSaveMeta.FromFile(entry.File) is { } meta)
                list.Add(meta);
        }

        return Task.FromResult<IReadOnlyList<CloudSaveMeta>>(list);
    }

    public async Task UploadAsync(string slot, ReadOnlyMemory<byte> file, ReadOnlyMemory<byte> thumbnail, CancellationToken ct)
    {
        await Task.Yield();
        if (Interlocked.Decrement(ref _failures) >= 0)
            throw new IOException("The fake cloud is offline.");
        Interlocked.Exchange(ref _failures, 0);
        _slots[slot] = (file.ToArray(), thumbnail.ToArray());
        Interlocked.Increment(ref _uploads);
    }

    public async Task<CloudSaveFile?> DownloadAsync(string slot, CancellationToken ct)
    {
        await Task.Yield();
        return _slots.TryGetValue(slot, out var entry) ? new CloudSaveFile(slot, entry.File, entry.Thumbnail) : null;
    }
}
