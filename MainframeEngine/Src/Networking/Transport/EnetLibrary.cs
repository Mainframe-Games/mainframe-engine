using System.Runtime.InteropServices;
using ENet;

namespace MainframeEngine.Networking;

/// <summary>
/// Reference-counted <c>enet_initialize</c>/<c>enet_deinitialize</c>: the first <see cref="Acquire"/> initializes
/// ENet, the last <see cref="Release"/> shuts it down, so any number of transports can coexist. Thread-safe.
/// </summary>
internal static class EnetLibrary
{
    private static readonly Lock Sync = new();
    private static int _references;

    /// <summary>File the ENet-CSharp <c>DllImport("enet")</c> resolves to on this OS.</summary>
    public static string NativeFileName =>
        OperatingSystem.IsWindows() ? "enet.dll" :
        OperatingSystem.IsMacOS() ? "libenet.dylib" :
        "libenet.so";

    /// <summary>Number of live acquisitions (tests).</summary>
    public static int References
    {
        get
        {
            lock (Sync)
                return _references;
        }
    }

    /// <exception cref="DllNotFoundException">The ENet native library is missing for this platform.</exception>
    /// <exception cref="InvalidOperationException"><c>enet_initialize</c> failed.</exception>
    public static void Acquire()
    {
        lock (Sync)
        {
            if (_references == 0)
            {
                bool initialized;
                try
                {
                    initialized = Library.Initialize();
                }
                catch (DllNotFoundException e)
                {
                    throw new DllNotFoundException(
                        $"ENet native library '{NativeFileName}' not found for {RuntimeInformation.RuntimeIdentifier}. " +
                        "It ships from MainframeEngine/runtimes/<rid>/native (see docs/design/natives.md).", e);
                }

                if (!initialized)
                    throw new InvalidOperationException("enet_initialize failed.");
            }

            _references++;
        }
    }

    public static void Release()
    {
        lock (Sync)
        {
            if (_references == 0)
                return;
            if (--_references == 0)
                Library.Deinitialize();
        }
    }
}
