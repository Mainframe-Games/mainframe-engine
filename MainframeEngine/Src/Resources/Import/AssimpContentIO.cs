using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Assimp;
using AiFile = Silk.NET.Assimp.File;

namespace MainframeEngine;

/// <summary>
/// Assimp's <c>aiFileIO</c> over <see cref="ContentFiles"/>: <see cref="ModelImporter"/> passes it when a model lies under
/// a mount (a pack), so the model and the files it references (a glTF's <c>.bin</c> buffers and textures) come from the
/// mount. Read-only: opening for writing fails. Each open file holds its stream in a <see cref="GCHandle"/> in the
/// native <c>aiFile</c>'s user data, freed on close.
/// </summary>
internal static unsafe class AssimpContentIO
{
    /// <summary>The callbacks; Assimp copies nothing, so the caller keeps the struct alive for the import.</summary>
    public static FileIO Create() => new(new PfnFileOpenProc(&Open), new PfnFileCloseProc(&Close));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static AiFile* Open(FileIO* io, byte* path, byte* mode)
    {
        try
        {
            var name = Marshal.PtrToStringUTF8((nint)path);
            var access = Marshal.PtrToStringUTF8((nint)mode) ?? "rb";
            // Assimp probes for files by opening them, so a missing file is a null, not an error.
            if (string.IsNullOrEmpty(name) || access.Contains('w', StringComparison.Ordinal) || access.Contains('a', StringComparison.Ordinal) ||
                !ContentFiles.Exists(name))
                return null;

            var stream = ContentFiles.OpenRead(name);
            var file = (AiFile*)NativeMemory.AllocZeroed((nuint)sizeof(AiFile));
            *file = new AiFile(
                new PfnFileReadProc(&Read), new PfnFileReadProc(&Write), new PfnFileTellProc(&Tell), new PfnFileTellProc(&Size),
                new PfnFileSeek(&Seek), new PfnFileFlushProc(&Flush), (byte*)GCHandle.ToIntPtr(GCHandle.Alloc(stream)));
            return file;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Close(FileIO* io, AiFile* file)
    {
        if (file is null)
            return;
        var handle = GCHandle.FromIntPtr((nint)file->UserData);
        (handle.Target as Stream)?.Dispose();
        handle.Free();
        NativeMemory.Free(file);
    }

    // fread semantics: reads up to count elements of size bytes and returns how many whole elements it read.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint Read(AiFile* file, byte* buffer, nuint size, nuint count)
    {
        if (size == 0 || count == 0)
            return 0;
        try
        {
            var stream = StreamOf(file);
            var span = new Span<byte>(buffer, checked((int)(size * count)));
            var total = 0;
            while (total < span.Length && stream.Read(span[total..]) is var read and > 0)
                total += read;
            return (nuint)total / size;
        }
        catch (Exception e) when (e is IOException or OverflowException)
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint Write(AiFile* file, byte* buffer, nuint size, nuint count) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint Tell(AiFile* file) => (nuint)StreamOf(file).Position;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint Size(AiFile* file) => (nuint)StreamOf(file).Length;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static Return Seek(AiFile* file, nuint offset, Origin origin)
    {
        try
        {
            var stream = StreamOf(file);
            var target = origin switch
            {
                Origin.Set => (long)offset,
                Origin.Cur => stream.Position + (long)offset,
                Origin.End => stream.Length + (long)offset,
                _ => -1,
            };
            if (target < 0 || target > stream.Length)
                return Return.Failure;
            stream.Position = target;
            return Return.Success;
        }
        catch (IOException)
        {
            return Return.Failure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Flush(AiFile* file)
    {
    }

    private static Stream StreamOf(AiFile* file) => (Stream)GCHandle.FromIntPtr((nint)file->UserData).Target!;
}
