using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MainframeEngine.UI.Rml;

/// <summary>Font style for <see cref="RmlCore.LoadFontFace(ReadOnlySpan{byte}, string?, RmlFontStyle, int, bool)"/>.</summary>
public enum RmlFontStyle
{
    Normal = 0,
    Italic = 1,
}

/// <summary>
/// The RmlUi library: ABI check, initialise/shutdown with the system and file interfaces, fonts and caches. RmlUi is
/// process-global and single-threaded: initialise once, use every <c>Rml*</c> type on the thread that initialised it
/// (the engine's main thread; <see cref="UiServer"/> owns this in games), and shut down after every context.
/// </summary>
/// <remarks>
/// Callbacks into C# are static <see cref="UnmanagedCallersOnlyAttribute"/> functions whose <c>user_data</c> is a
/// <see cref="GCHandle"/> to the managed interface object. Every one of them catches all exceptions and logs them
/// with <see cref="Log.Error"/>: a managed exception must never unwind into RmlUi.
/// </remarks>
public static unsafe class RmlCore
{
    private static GCHandle _system;
    private static GCHandle _file;
    private static readonly Dictionary<ulong, Stream> Files = [];
    private static ulong _nextFile;
    // Weak, so an owner dropped without Dispose can still be finalized (its release is then deferred to this thread).
    private static readonly List<WeakReference<RmlHandle>> LiveHandles = [];
    private static readonly Queue<RmlHandle> PendingReleases = new();
    private static readonly Queue<IDisposable> DeferredDisposals = new();
    private static readonly Lock PendingLock = new();
    private static uint? _loadedAbi;

    /// <summary>The managed thread that initialised RmlUi.</summary>
    public static int OwnerThreadId { get; private set; } = -1;

    /// <summary>True between <see cref="Initialise"/> and <see cref="Shutdown"/>.</summary>
    public static bool IsInitialised { get; private set; }

    /// <summary>The system interface installed by <see cref="Initialise"/> (null before, or when RmlUi's default is used).</summary>
    public static RmlSystemInterface? SystemInterface => _system.IsAllocated ? (RmlSystemInterface?)_system.Target : null;

    /// <summary>
    /// Loads <c>mfrmlui</c> and checks its ABI: the major version must equal <see cref="RmlNative.AbiMajor"/> and the
    /// minor be at least <see cref="RmlNative.AbiMinor"/>. Throws <see cref="RmlException"/> with a clear message when
    /// the library is missing or incompatible. Cheap after the first call.
    /// </summary>
    public static uint EnsureLibrary()
    {
        if (_loadedAbi is { } known)
            return known;

        uint version;
        try
        {
            version = RmlNative.GetAbiVersion();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new RmlException(
                $"The RmlUi native library '{RmlNative.Library}' could not be loaded ({e.GetType().Name}: {e.Message}). " +
                $"It ships in MainframeEngine/runtimes/<rid>/native/ and is copied next to the application at build time " +
                $"(docs/design/natives.md); this platform's binary may be missing.", e);
        }

        if (!IsCompatible(version))
            throw new RmlException(
                $"mfrmlui ABI {version >> 16}.{version & 0xFFFF} is incompatible with this engine, which needs " +
                $"{RmlNative.AbiMajor}.{RmlNative.AbiMinor} or a later minor version. Refresh the native binaries " +
                "(Native/build.sh --stage, or the natives.yml artifacts; see docs/design/natives.md).");

        _loadedAbi = version;
        return version;
    }

    /// <summary>The ABI rule: equal major, library minor ≥ binding minor.</summary>
    internal static bool IsCompatible(uint libraryVersion) =>
        libraryVersion >> 16 == RmlNative.AbiMajor && (libraryVersion & 0xFFFF) >= RmlNative.AbiMinor;

    /// <summary>The ABI version of the loaded library (<c>(major &lt;&lt; 16) | minor</c>).</summary>
    public static uint AbiVersion => EnsureLibrary();

    /// <summary>RmlUi's version string, e.g. "6.3".</summary>
    public static string RmlUiVersion
    {
        get
        {
            EnsureLibrary();
            return RmlUtf8.Read(0, &ReadRmlUiVersion) ?? "";
        }
    }

    private static int ReadRmlUiVersion(nint _, byte* buffer, int capacity) => RmlNative.GetRmlUiVersion(buffer, capacity);

    /// <summary>The library's last error message on this thread (empty when none).</summary>
    public static string LastError
    {
        get
        {
            try
            {
                return RmlUtf8.Read(0, &ReadLastError) ?? "";
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return "";
            }
        }
    }

    private static int ReadLastError(nint _, byte* buffer, int capacity) => RmlNative.GetLastError(buffer, capacity);

    /// <summary>
    /// Initialises RmlUi with the FreeType font engine. Either interface may be null (RmlUi's default: its own clock
    /// and stdout log; stdio files relative to the working directory).
    /// </summary>
    public static void Initialise(RmlSystemInterface? system = null, RmlFileInterface? file = null)
    {
        EnsureLibrary();
        if (IsInitialised)
            throw new InvalidOperationException("RmlUi is already initialised.");

        var systemCallbacks = default(RmlNative.SystemCallbacks);
        var fileCallbacks = default(RmlNative.FileCallbacks);
        if (system is not null)
        {
            _system = GCHandle.Alloc(system);
            systemCallbacks = new RmlNative.SystemCallbacks
            {
                StructSize = RmlNative.SizeOf<RmlNative.SystemCallbacks>(),
                UserData = GCHandle.ToIntPtr(_system),
                GetElapsedTime = &SysElapsedTime,
                TranslateString = &SysTranslate,
                LogMessage = &SysLog,
                SetMouseCursor = &SysSetCursor,
                SetClipboardText = &SysSetClipboard,
                GetClipboardText = &SysGetClipboard,
                ActivateKeyboard = &SysActivateKeyboard,
                DeactivateKeyboard = &SysDeactivateKeyboard,
                JoinPath = &SysJoinPath,
            };
        }

        if (file is not null)
        {
            _file = GCHandle.Alloc(file);
            fileCallbacks = new RmlNative.FileCallbacks
            {
                StructSize = RmlNative.SizeOf<RmlNative.FileCallbacks>(),
                UserData = GCHandle.ToIntPtr(_file),
                Open = &FileOpen,
                Close = &FileClose,
                Read = &FileRead,
                Seek = &FileSeek,
                Tell = &FileTell,
                Length = &FileLength,
                LoadFile = &FileLoad,
            };
        }

        var status = RmlNative.Initialise(system is null ? null : &systemCallbacks, file is null ? null : &fileCallbacks);
        if (status < 0)
        {
            FreeInterfaces();
            throw new RmlException("mfrmlui_initialise", status);
        }

        IsInitialised = true;
        OwnerThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>
    /// Destroys every context (and its data models, documents and listeners), releases every texture and geometry
    /// through the render interfaces — which must still exist — and shuts RmlUi down. Handles of destroyed objects
    /// become invalid. Render interfaces stay valid; dispose them afterwards.
    /// </summary>
    public static void Shutdown()
    {
        if (!IsInitialised)
            return;

        CallbackDepth = 0;
        ProcessPendingReleases();

        // Contexts and data models die with the library: their handles must not be released again.
        foreach (var weak in LiveHandles.ToArray())
            if (weak.TryGetTarget(out var handle) && handle.DiesWithLibrary)
                handle.MarkDestroyedByLibrary();
        LiveHandles.RemoveAll(static w => !w.TryGetTarget(out var h) || h.DiesWithLibrary);

        var status = RmlNative.Shutdown();
        RmlDebugger.Reset();
        IsInitialised = false;
        OwnerThreadId = -1;
        foreach (var stream in Files.Values)
            stream.Dispose();
        Files.Clear();
        FreeInterfaces();
        RmlException.ThrowIfFailed(status, "mfrmlui_shutdown");
    }

    private static void FreeInterfaces()
    {
        if (_system.IsAllocated)
            _system.Free();
        if (_file.IsAllocated)
            _file.Free();
        _system = default;
        _file = default;
    }

    /// <summary>Loads a font face (path through the file interface). <paramref name="weight"/>: 0 = from the font, or 1..1000.</summary>
    public static void LoadFontFace(string path, bool fallbackFace = false, int weight = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        RequireInitialised();
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var p = new RmlUtf8Arg(path, scratch);
        fixed (byte* ptr = p)
            RmlException.ThrowIfFailed(RmlNative.LoadFontFace(ptr, fallbackFace ? 1 : 0, weight), $"Loading font '{path}'");
    }

    /// <summary>Loads a font face from memory (copied by RmlUi). <paramref name="family"/> null uses the font's own.</summary>
    public static void LoadFontFace(ReadOnlySpan<byte> data, string? family, RmlFontStyle style = RmlFontStyle.Normal,
        int weight = 0, bool fallbackFace = false)
    {
        if (data.IsEmpty)
            throw new ArgumentException("Font data is empty.", nameof(data));
        RequireInitialised();
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var f = new RmlUtf8Arg(family, scratch);
        fixed (byte* d = data)
        fixed (byte* pf = f)
            RmlException.ThrowIfFailed(RmlNative.LoadFontFaceFromMemory(d, data.Length, pf, (int)style, weight, fallbackFace ? 1 : 0),
                "Loading font from memory");
    }

    /// <summary>Drops cached style sheets so the next document load re-reads them (hot reload).</summary>
    public static void ClearStyleSheetCache()
    {
        RequireInitialised();
        RmlException.ThrowIfFailed(RmlNative.ClearStyleSheetCache(), "mfrmlui_clear_style_sheet_cache");
    }

    /// <summary>Drops cached templates so the next document load re-reads them (hot reload).</summary>
    public static void ClearTemplateCache()
    {
        RequireInitialised();
        RmlException.ThrowIfFailed(RmlNative.ClearTemplateCache(), "mfrmlui_clear_template_cache");
    }

    /// <summary>Releases every texture RmlUi holds (re-created on demand), e.g. after a device loss.</summary>
    public static void ReleaseTextures(RmlRenderInterface? renderInterface = null)
    {
        RequireInitialised();
        RmlException.ThrowIfFailed(RmlNative.ReleaseTextures(renderInterface?.Handle ?? 0), "mfrmlui_release_textures");
    }

    /// <summary>Releases every compiled geometry RmlUi holds (re-created on demand).</summary>
    public static void ReleaseCompiledGeometry(RmlRenderInterface? renderInterface = null)
    {
        RequireInitialised();
        RmlException.ThrowIfFailed(RmlNative.ReleaseCompiledGeometry(renderInterface?.Handle ?? 0), "mfrmlui_release_compiled_geometry");
    }

    internal static void RequireInitialised()
    {
        if (!IsInitialised)
            throw new InvalidOperationException("RmlUi is not initialised (RmlCore.Initialise / UiServer).");
    }

    // ── Owned handle bookkeeping ─────────────────────────────────────────────────────────────────────────────

    internal static void Track(RmlHandle handle)
    {
        LiveHandles.RemoveAll(static w => !w.TryGetTarget(out _));
        LiveHandles.Add(new WeakReference<RmlHandle>(handle));
    }

    internal static void Untrack(RmlHandle handle) =>
        LiveHandles.RemoveAll(w => !w.TryGetTarget(out var h) || ReferenceEquals(h, handle));

    /// <summary>Live owned handles (diagnostics, tests).</summary>
    internal static int LiveHandleCount
    {
        get
        {
            var n = 0;
            foreach (var w in LiveHandles)
                if (w.TryGetTarget(out _))
                    n++;
            return n;
        }
    }

    // ── Re-entrancy ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Depth of C# event/data callbacks currently running inside an RmlUi call. While it is non-zero RmlUi is in the
    /// middle of dispatching (e.g. a click inside <c>ProcessMouseButtonUp</c>), so destroying a context or data model
    /// would pull objects out from under it: such disposals are deferred (<see cref="DisposeWhenSafe"/>).
    /// </summary>
    public static int CallbackDepth { get; private set; }

    /// <summary>True while a C# handler runs inside an RmlUi dispatch.</summary>
    public static bool IsInCallback => CallbackDepth > 0;

    internal static void EnterCallback() => CallbackDepth++;

    internal static void ExitCallback() => CallbackDepth = Math.Max(0, CallbackDepth - 1);

    /// <summary>Disposes now, or after the current RmlUi dispatch returns (next <see cref="ProcessPendingReleases"/>).</summary>
    internal static bool DeferIfInCallback(IDisposable disposable)
    {
        if (CallbackDepth == 0)
            return false;
        DeferredDisposals.Enqueue(disposable);
        return true;
    }

    /// <summary>A handle whose owner was collected without being disposed: released on the RmlUi thread later.</summary>
    internal static void DeferRelease(RmlHandle handle)
    {
        lock (PendingLock)
            PendingReleases.Enqueue(handle);
    }

    /// <summary>
    /// Releases handles queued from other threads (finalizers) and disposals deferred out of RmlUi callbacks. Called by
    /// the UI server every frame (outside any dispatch).
    /// </summary>
    public static void ProcessPendingReleases()
    {
        if (CallbackDepth > 0)
            return;
        while (DeferredDisposals.TryDequeue(out var disposable))
            disposable.Dispose();
        while (true)
        {
            RmlHandle? handle;
            lock (PendingLock)
                if (!PendingReleases.TryDequeue(out handle))
                    return;
            handle.ReleaseNow();
        }
    }

    // ── System callbacks ─────────────────────────────────────────────────────────────────────────────────────

    private static RmlSystemInterface Sys(nint user) => Unsafe.As<RmlSystemInterface>(GCHandle.FromIntPtr(user).Target!);

    internal static void Report(Exception e, string where) => Log.Error($"[RmlUi] Exception in {where} (swallowed at the native boundary): {e}");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static double SysElapsedTime(nint user)
    {
        try
        {
            return Sys(user).GetElapsedTime();
        }
        catch (Exception e)
        {
            Report(e, nameof(RmlSystemInterface.GetElapsedTime));
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SysTranslate(nint user, byte* input, nint output)
    {
        try
        {
            return Sys(user).TranslateString(RmlUtf8.Span(input), new RmlStringSink(output));
        }
        catch (Exception e)
        {
            Report(e, nameof(RmlSystemInterface.TranslateString));
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SysLog(nint user, int type, byte* message)
    {
        try
        {
            return Sys(user).LogMessage((RmlLogType)type, RmlUtf8.ToString(message) ?? "") ? 1 : 0;
        }
        catch (Exception e)
        {
            Report(e, nameof(RmlSystemInterface.LogMessage));
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SysSetCursor(nint user, byte* name)
    {
        try
        {
            Sys(user).SetMouseCursor(RmlUtf8.Span(name));
        }
        catch (Exception e)
        {
            Report(e, nameof(RmlSystemInterface.SetMouseCursor));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SysSetClipboard(nint user, byte* text)
    {
        try
        {
            Sys(user).SetClipboardText(RmlUtf8.ToString(text) ?? "");
        }
        catch (Exception e)
        {
            Report(e, nameof(RmlSystemInterface.SetClipboardText));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SysGetClipboard(nint user, nint output)
    {
        try
        {
            new RmlStringSink(output).Set(Sys(user).GetClipboardText().AsSpan());
        }
        catch (Exception e)
        {
            Report(e, nameof(RmlSystemInterface.GetClipboardText));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SysJoinPath(nint user, byte* documentPath, byte* path, nint output)
    {
        try
        {
            var raw = RmlUtf8.ToString(path) ?? "";
            string joined;
            try
            {
                joined = RmlPaths.Join(RmlUtf8.ToString(documentPath) ?? "", raw);
            }
            catch (Exception e)
            {
                Report(e, "JoinPath");
                joined = raw;
            }

            new RmlStringSink(output).Set(joined.AsSpan());
        }
        catch (Exception e)
        {
            Report(e, "JoinPath");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SysActivateKeyboard(nint user, float x, float y, float lineHeight)
    {
        try
        {
            Sys(user).ActivateKeyboard(new Vector2(x, y), lineHeight);
        }
        catch (Exception e)
        {
            Report(e, nameof(RmlSystemInterface.ActivateKeyboard));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SysDeactivateKeyboard(nint user)
    {
        try
        {
            Sys(user).DeactivateKeyboard();
        }
        catch (Exception e)
        {
            Report(e, nameof(RmlSystemInterface.DeactivateKeyboard));
        }
    }

    // ── File callbacks ───────────────────────────────────────────────────────────────────────────────────────

    private static RmlFileInterface Fs(nint user) => Unsafe.As<RmlFileInterface>(GCHandle.FromIntPtr(user).Target!);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong FileOpen(nint user, byte* path)
    {
        try
        {
            var stream = Fs(user).Open(RmlUtf8.ToString(path) ?? "");
            if (stream is null)
                return 0;
            var id = ++_nextFile;
            Files.Add(id, stream);
            return id;
        }
        catch (Exception e)
        {
            Report(e, nameof(RmlFileInterface.Open));
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FileClose(nint user, ulong file)
    {
        try
        {
            if (Files.Remove(file, out var stream))
                stream.Dispose();
        }
        catch (Exception e)
        {
            Report(e, "file close");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong FileRead(nint user, ulong file, byte* buffer, ulong size)
    {
        try
        {
            if (!Files.TryGetValue(file, out var stream) || buffer is null)
                return 0;
            ulong total = 0;
            while (total < size)
            {
                var chunk = (int)Math.Min(size - total, int.MaxValue);
                var read = stream.Read(new Span<byte>(buffer + total, chunk));
                if (read <= 0)
                    break;
                total += (ulong)read;
            }

            return total;
        }
        catch (Exception e)
        {
            Report(e, "file read");
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FileSeek(nint user, ulong file, long offset, int origin)
    {
        try
        {
            if (!Files.TryGetValue(file, out var stream) || !stream.CanSeek || origin is < 0 or > 2)
                return 0;
            var target = (SeekOrigin)origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => stream.Position + offset,
                _ => stream.Length + offset,
            };
            if (target < 0 || target > stream.Length)
                return 0;
            stream.Position = target;
            return 1;
        }
        catch (Exception e)
        {
            Report(e, "file seek");
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong FileTell(nint user, ulong file)
    {
        try
        {
            return Files.TryGetValue(file, out var stream) ? (ulong)stream.Position : 0;
        }
        catch (Exception e)
        {
            Report(e, "file tell");
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong FileLength(nint user, ulong file)
    {
        try
        {
            return Files.TryGetValue(file, out var stream) && stream.CanSeek ? (ulong)stream.Length : 0;
        }
        catch (Exception e)
        {
            Report(e, "file length");
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FileLoad(nint user, byte* path, nint output)
    {
        try
        {
            using var stream = Fs(user).Open(RmlUtf8.ToString(path) ?? "");
            if (stream is null)
                return 0;
            var length = checked((int)(stream.CanSeek ? stream.Length - stream.Position : 0));
            if (!stream.CanSeek)
            {
                var all = RmlStreams.ReadAll(stream, out length);
                fixed (byte* p = all)
                    return RmlNative.StringSet(output, p, length) == RmlNative.Ok ? 1 : 0;
            }

            var rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
            try
            {
                stream.ReadExactly(rented, 0, length);
                fixed (byte* p = rented)
                    return RmlNative.StringSet(output, p, length) == RmlNative.Ok ? 1 : 0;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        catch (Exception e)
        {
            Report(e, "file load");
            return 0;
        }
    }

    /// <summary>Decodes UTF-8 for diagnostics.</summary>
    internal static string Decode(ReadOnlySpan<byte> utf8) => Encoding.UTF8.GetString(utf8);
}
