using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MainframeEngine.Editor;

/// <summary>Moves files and folders to the system trash (recoverable). Implementations never delete permanently.</summary>
public interface ITrash
{
    /// <summary>False when this platform has no trash the editor can use.</summary>
    bool IsSupported { get; }

    /// <summary>Moves <paramref name="path"/> (a file or folder) to the trash; false with a user-facing message on failure.</summary>
    bool TryMoveToTrash(string path, out string? error);
}

/// <summary>The current platform's trash: Finder's on macOS, the Recycle Bin on Windows, the freedesktop.org trash on Linux.</summary>
public static class SystemTrash
{
    public static ITrash Default { get; } =
        OperatingSystem.IsMacOS() ? new MacTrash()
        : OperatingSystem.IsWindows() ? new WindowsTrash()
        : OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD() ? new FreedesktopTrash()
        : new UnsupportedTrash();

    internal static bool TryResolve(string path, out string fullPath, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            error = null;
            return true;
        }

        error = $"'{Path.GetFileName(fullPath)}' does not exist.";
        return false;
    }
}

internal sealed class UnsupportedTrash : ITrash
{
    public bool IsSupported => false;

    public bool TryMoveToTrash(string path, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        error = $"This system has no trash to move '{Path.GetFileName(Path.TrimEndingDirectorySeparator(path))}' to.";
        return false;
    }
}

/// <summary>
/// macOS: <c>[[NSFileManager defaultManager] trashItemAtURL:[NSURL fileURLWithPath:path] resultingItemURL:nil error:&amp;error]</c>
/// through the Objective-C runtime (Finder's "Move to Trash", including "Put Back").
/// </summary>
internal sealed unsafe partial class MacTrash : ITrash
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    private static readonly Lazy<bool> FoundationLoaded = new(static () =>
        OperatingSystem.IsMacOS() && NativeLibrary.TryLoad("/System/Library/Frameworks/Foundation.framework/Foundation", out _));

    public bool IsSupported => FoundationLoaded.Value;

    public bool TryMoveToTrash(string path, out string? error)
    {
        if (!SystemTrash.TryResolve(path, out var full, out error))
            return false;
        var name = Path.GetFileName(full);
        if (!IsSupported)
        {
            error = $"Could not move '{name}' to the Trash: Foundation is not available.";
            return false;
        }

        var pool = objc_autoreleasePoolPush();
        var utf8 = Marshal.StringToCoTaskMemUTF8(full);
        try
        {
            fixed (byte* nsStringName = "NSString\0"u8, nsUrlName = "NSURL\0"u8, managerName = "NSFileManager\0"u8)
            fixed (byte* fromUtf8 = "stringWithUTF8String:\0"u8, fileUrl = "fileURLWithPath:\0"u8, defaultManager = "defaultManager\0"u8)
            fixed (byte* trashItem = "trashItemAtURL:resultingItemURL:error:\0"u8)
            {
                var text = Send(objc_getClass(nsStringName), sel_registerName(fromUtf8), utf8);
                var url = text == 0 ? 0 : Send(objc_getClass(nsUrlName), sel_registerName(fileUrl), text);
                var manager = Send(objc_getClass(managerName), sel_registerName(defaultManager));
                if (url == 0 || manager == 0)
                {
                    error = $"Could not move '{name}' to the Trash: the path could not be converted.";
                    return false;
                }

                nint nsError = 0;
                if (SendTrash(manager, sel_registerName(trashItem), url, 0, &nsError) != 0)
                {
                    error = null;
                    return true;
                }

                error = $"Could not move '{name}' to the Trash: {Describe(nsError)}";
                return false;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
            objc_autoreleasePoolPop(pool);
        }
    }

    private static string Describe(nint nsError)
    {
        if (nsError == 0)
            return "unknown error.";
        fixed (byte* localized = "localizedDescription\0"u8, utf8String = "UTF8String\0"u8)
        {
            var description = Send(nsError, sel_registerName(localized));
            var chars = description == 0 ? 0 : Send(description, sel_registerName(utf8String));
            return chars == 0 ? "unknown error." : Marshal.PtrToStringUTF8(chars) ?? "unknown error.";
        }
    }

    [LibraryImport(ObjC)]
    private static partial nint objc_getClass(byte* name);

    [LibraryImport(ObjC)]
    private static partial nint sel_registerName(byte* name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector, nint argument);

    // - (BOOL)trashItemAtURL:(NSURL *)url resultingItemURL:(NSURL **)outResultingURL error:(NSError **)error
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendTrash(nint receiver, nint selector, nint url, nint resultingUrl, nint* error);

    [LibraryImport(ObjC)]
    private static partial nint objc_autoreleasePoolPush();

    [LibraryImport(ObjC)]
    private static partial void objc_autoreleasePoolPop(nint pool);
}

/// <summary>
/// Windows: <c>SHFileOperationW(FO_DELETE)</c> with <c>FOF_ALLOWUNDO</c> (the Recycle Bin), silent, plus
/// <c>FOF_WANTNUKEWARNING</c> so a file that cannot be recycled asks before it would be destroyed.
/// </summary>
internal sealed unsafe partial class WindowsTrash : ITrash
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofWantNukeWarning = 0x4000;

    public bool IsSupported => OperatingSystem.IsWindows();

    public bool TryMoveToTrash(string path, out string? error)
    {
        if (!SystemTrash.TryResolve(path, out var full, out error))
            return false;
        var name = Path.GetFileName(full);
        if (!IsSupported)
        {
            error = $"Could not move '{name}' to the Recycle Bin: not running on Windows.";
            return false;
        }

        // pFrom is a list of NUL-terminated paths ending with an empty one: the path, NUL, NUL.
        var from = Marshal.StringToHGlobalUni(full + "\0");
        try
        {
            var operation = new ShFileOpStruct
            {
                Func = FoDelete,
                From = from,
                Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning,
            };
            var result = SHFileOperationW(&operation);
            if (result == 0 && operation.AnyOperationsAborted == 0)
                return true;
            error = operation.AnyOperationsAborted != 0
                ? $"Moving '{name}' to the Recycle Bin was cancelled."
                : $"Could not move '{name}' to the Recycle Bin (error 0x{result:X}).";
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }

    // SHFILEOPSTRUCTW (64-bit layout; the editor ships 64-bit only).
    [StructLayout(LayoutKind.Sequential)]
    private struct ShFileOpStruct
    {
        public nint Hwnd;
        public uint Func;
        public nint From;
        public nint To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public nint NameMappings;
        public nint ProgressTitle;
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHFileOperationW(ShFileOpStruct* operation);
}

/// <summary>
/// The freedesktop.org trash (Linux desktops): the item is renamed into <c>&lt;trash&gt;/files/</c> under a unique name
/// and described by <c>&lt;trash&gt;/info/&lt;name&gt;.trashinfo</c> (<c>Path=</c>, <c>DeletionDate=</c>). Only the home
/// trash is supported, so items on another filesystem fail (a rename cannot cross filesystems; nothing is copied).
/// </summary>
public sealed unsafe partial class FreedesktopTrash : ITrash
{
    private const int CrossDeviceErrno = 18; // EXDEV on Linux and macOS

    /// <param name="trashDirectory">The trash folder; default <c>$XDG_DATA_HOME/Trash</c> or <c>~/.local/share/Trash</c>.</param>
    public FreedesktopTrash(string? trashDirectory = null)
    {
        TrashDirectory = trashDirectory is null ? DefaultDirectory() : Path.GetFullPath(trashDirectory);
    }

    /// <summary>The trash folder (holding <c>files/</c> and <c>info/</c>); null when no home folder is known.</summary>
    public string? TrashDirectory { get; }

    public bool IsSupported => !OperatingSystem.IsWindows() && TrashDirectory is not null;

    public bool TryMoveToTrash(string path, out string? error)
    {
        if (!SystemTrash.TryResolve(path, out var full, out error))
            return false;
        var name = Path.GetFileName(full);
        if (!IsSupported || OperatingSystem.IsWindows())
        {
            error = $"Could not move '{name}' to the trash: no trash folder on this system.";
            return false;
        }

        var files = Path.Combine(TrashDirectory!, "files");
        var info = Path.Combine(TrashDirectory!, "info");
        try
        {
            const UnixFileMode privateFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(files, privateFolder);
            Directory.CreateDirectory(info, privateFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = $"Could not move '{name}' to the trash: {e.Message}";
            return false;
        }

        // Reserve a unique name by creating its .trashinfo exclusively (the spec's atomic step), then rename.
        var content = $"[Trash Info]\nPath={EscapePath(full)}\nDeletionDate={DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}\n";
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var attempt = 1; attempt < 10_000; attempt++)
        {
            var candidate = attempt == 1 ? name : $"{stem}.{attempt}{extension}";
            var target = Path.Combine(files, candidate);
            var infoPath = Path.Combine(info, candidate + ".trashinfo");
            if (File.Exists(target) || Directory.Exists(target))
                continue;
            try
            {
                using var stream = new FileStream(infoPath, FileMode.CreateNew, FileAccess.Write);
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
            catch (IOException) when (File.Exists(infoPath))
            {
                continue;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error = $"Could not move '{name}' to the trash: {e.Message}";
                return false;
            }

            if (Rename(full, target, out var errno))
                return true;

            TryDelete(infoPath);
            error = errno == CrossDeviceErrno
                ? $"Could not move '{name}' to the trash: it is on a different drive than the trash folder."
                : $"Could not move '{name}' to the trash: {Marshal.GetPInvokeErrorMessage(errno)}";
            return false;
        }

        error = $"Could not move '{name}' to the trash: no free name in the trash folder.";
        return false;
    }

    private static string? DefaultDirectory()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(dataHome) && Path.IsPathRooted(dataHome))
            return Path.Combine(dataHome, "Trash");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".local", "share", "Trash");
    }

    /// <summary>The absolute path, percent-encoded per segment (the spec's <c>Path=</c> form).</summary>
    internal static string EscapePath(string fullPath) =>
        string.Join('/', fullPath.Split('/').Select(Uri.EscapeDataString));

    private static bool Rename(string from, string to, out int errno)
    {
        var fromUtf8 = Marshal.StringToCoTaskMemUTF8(from);
        var toUtf8 = Marshal.StringToCoTaskMemUTF8(to);
        try
        {
            if (rename((byte*)fromUtf8, (byte*)toUtf8) == 0)
            {
                errno = 0;
                return true;
            }

            errno = Marshal.GetLastPInvokeError();
            return false;
        }
        finally
        {
            Marshal.FreeCoTaskMem(fromUtf8);
            Marshal.FreeCoTaskMem(toUtf8);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A stray .trashinfo without its file is ignored by trash implementations.
        }
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int rename(byte* from, byte* to);
}
