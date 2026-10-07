using System.IO.MemoryMappedFiles;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// The file-backed shared memory the editor and <c>mfplughost</c> exchange audio and note events through (ADR 0147;
/// mirrors <c>Native/PluginHost/src/shm.hpp</c>). The editor creates the file and its header; the helper maps it. Header
/// (64 bytes): <c>u32 magic 'MFSM', version, maxBlock, slotCount, maxEvents, slotStride, currentInstance</c>. Each slot:
/// planar <c>f32 inL, inR, outL, outR [maxBlock]</c>, <c>u32 eventCount</c> + 12 bytes, then <c>maxEvents</c> 16-byte
/// events (<c>i32 offset, u8 type (1 on), u8 channel, u8 pitch, u8 velocity, 8 reserved</c>).
/// </summary>
public sealed unsafe class PluginSharedMemory : IDisposable
{
    public const uint Magic = 0x4D53464D;
    public const uint Version = 1;
    public const int HeaderSize = 64;
    public const int EventSize = 16;

    public const int InLeft = 0;
    public const int InRight = 1;
    public const int OutLeft = 2;
    public const int OutRight = 3;

    private static int s_counter;
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly bool _owner;
    private byte* _base;

    private PluginSharedMemory(string path, MemoryMappedFile file, long size, bool owner)
    {
        Path = path;
        Size = size;
        _file = file;
        _owner = owner;
        _view = file.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _base = pointer + _view.PointerOffset;
    }

    public string Path { get; }

    public long Size { get; }

    public int MaxBlock { get; private set; }

    public int SlotCount { get; private set; }

    public int MaxEvents { get; private set; }

    public int Stride { get; private set; }

    /// <summary>The instance the helper is calling into (0: none) — after a crash, the plugin to blame.</summary>
    public uint CurrentInstance
    {
        get => Volatile.Read(ref *(uint*)(_base + 24));
        set => Volatile.Write(ref *(uint*)(_base + 24), value);
    }

    public static int StrideFor(int maxBlock, int maxEvents) => (16 * maxBlock + 16 + EventSize * maxEvents + 63) / 64 * 64;

    /// <summary>Creates a new file in the temp folder and writes its header.</summary>
    public static PluginSharedMemory Create(int maxBlock, int slots, int maxEvents)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mfph-{Environment.ProcessId}-{Interlocked.Increment(ref s_counter)}.shm");
        var stride = StrideFor(maxBlock, maxEvents);
        var size = HeaderSize + (long)slots * stride;
        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        stream.SetLength(size);
        var file = MemoryMappedFile.CreateFromFile(stream, null, size, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
        var shm = new PluginSharedMemory(path, file, size, owner: true) { MaxBlock = maxBlock, SlotCount = slots, MaxEvents = maxEvents, Stride = stride };
        var header = (uint*)shm._base;
        header[0] = Magic;
        header[1] = Version;
        header[2] = (uint)maxBlock;
        header[3] = (uint)slots;
        header[4] = (uint)maxEvents;
        header[5] = (uint)stride;
        return shm;
    }

    /// <summary>Maps an existing file (the helper's side; tests' fake host).</summary>
    public static PluginSharedMemory Open(string path, long size)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var file = MemoryMappedFile.CreateFromFile(stream, null, size, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
        var shm = new PluginSharedMemory(path, file, size, owner: false);
        var header = (uint*)shm._base;
        if (header[0] != Magic || header[1] != Version)
        {
            shm.Dispose();
            throw new InvalidDataException("Not a plugin shared memory file.");
        }

        shm.MaxBlock = (int)header[2];
        shm.SlotCount = (int)header[3];
        shm.MaxEvents = (int)header[4];
        shm.Stride = (int)header[5];
        return shm;
    }

    /// <summary>One planar channel of a slot (<see cref="InLeft"/> … <see cref="OutRight"/>), <see cref="MaxBlock"/> frames.</summary>
    public Span<float> Channel(int slot, int which)
    {
        if ((uint)slot >= (uint)SlotCount || (uint)which > 3)
            throw new ArgumentOutOfRangeException(nameof(slot));
        return new Span<float>(_base + HeaderSize + (long)slot * Stride + (long)which * MaxBlock * 4, MaxBlock);
    }

    public ref uint EventCount(int slot) => ref *(uint*)(SlotBase(slot) + 16L * MaxBlock);

    /// <summary>Writes event <paramref name="index"/> of a slot (the count is set separately).</summary>
    public void WriteEvent(int slot, int index, int offset, bool on, int pitch, int velocity)
    {
        var e = SlotBase(slot) + 16L * MaxBlock + 16 + (long)index * EventSize;
        *(int*)e = offset;
        e[4] = on ? (byte)1 : (byte)0;
        e[5] = 0;
        e[6] = (byte)Math.Clamp(pitch, 0, 127);
        e[7] = (byte)Math.Clamp(velocity, 0, 127);
        *(ulong*)(e + 8) = 0;
    }

    /// <summary>Reads event <paramref name="index"/> of a slot (fake host).</summary>
    public (int Offset, bool On, int Pitch, int Velocity) ReadEvent(int slot, int index)
    {
        var e = SlotBase(slot) + 16L * MaxBlock + 16 + (long)index * EventSize;
        return (*(int*)e, e[4] == 1, e[6], e[7]);
    }

    public void Dispose()
    {
        if (_base == null)
            return;
        _base = null;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
        if (_owner)
        {
            try
            {
                File.Delete(Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A leftover temp file is harmless.
            }
        }
    }

    private byte* SlotBase(int slot)
    {
        if ((uint)slot >= (uint)SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot));
        return _base + HeaderSize + (long)slot * Stride;
    }
}
