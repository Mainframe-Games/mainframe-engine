using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>
/// macOS: what the app bundle the process runs from says about the app (ADR 0182). A game launched from a bundle
/// (<c>bin/…/&lt;name&gt;.app</c> in development, the packaged <c>.app</c> for players) already has its Dock icon from the
/// bundle's <c>.icns</c>; on macOS an SDL window icon only replaces that Dock icon, so <see cref="GameHost"/> skips it then.
/// False everywhere else and for unbundled executables.
/// </summary>
internal static partial class MacAppBundle
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8Encoding = 0x08000100; // kCFStringEncodingUTF8

    private static readonly Lazy<bool> s_hasIcon = new(QueryHasIcon);

    /// <summary>True when the main bundle's Info.plist names an icon (a non-empty <c>CFBundleIconFile</c>).</summary>
    public static bool HasIcon => OperatingSystem.IsMacOS() && s_hasIcon.Value;

    private static bool QueryHasIcon()
    {
        try
        {
            var bundle = CFBundleGetMainBundle(); // not owned: no release
            if (bundle == 0)
                return false;
            var key = CFStringCreateWithCString(0, "CFBundleIconFile", Utf8Encoding);
            if (key == 0)
                return false;
            try
            {
                var value = CFBundleGetValueForInfoDictionaryKey(bundle, key); // not owned
                return value != 0 && CFGetTypeID(value) == CFStringGetTypeID() && CFStringGetLength(value) > 0;
            }
            finally
            {
                CFRelease(key);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport(CoreFoundation)]
    private static partial nint CFBundleGetMainBundle();

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string text, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFBundleGetValueForInfoDictionaryKey(nint bundle, nint key);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFGetTypeID(nint value);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFStringGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringGetLength(nint text);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);
}
