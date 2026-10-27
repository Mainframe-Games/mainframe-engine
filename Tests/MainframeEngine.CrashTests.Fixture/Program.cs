using System.Runtime.InteropServices;
using MainframeEngine;

return GameHost.Run(args, typeof(CrashFixture.Crasher).Assembly);

namespace CrashFixture
{

    /// <summary>
    /// Crashes the game the way <c>++ crash=&lt;kind&gt;</c> says, on the third physics step (the session is running):
    /// <c>managed</c> (a throw in <see cref="Node.OnPhysicsProcess"/>), <c>ready</c> (a throw in a child's OnReady),
    /// <c>thread</c>, <c>task</c> (an unobserved task; the game then plays on), <c>failfast</c>, <c>stackoverflow</c>,
    /// <c>native</c> (a segmentation fault / access violation), or <c>none</c>. The ends without a report crash after 1.5 s.
    /// </summary>
    public sealed class Crasher : Node
    {
        private int _steps;
        private string _kind = "none";

        protected override void OnReady()
        {
            foreach (var arg in GameHost.UserArgs)
                if (arg.StartsWith("crash=", StringComparison.Ordinal))
                    _kind = arg["crash=".Length..];
            Log.Info($"[Fixture] Crasher ready: crash={_kind}");
            CrashReporter.SetContext("fixture", () => "crash=" + _kind);
        }

        protected override void OnPhysicsProcess(float delta)
        {
            if (++_steps == 2 && _kind == "task")
            {
                StartFaultingTask();
                return;
            }

            if (_kind == "task" && _steps > 2)
            {
                // The finalizer of the faulted task raises UnobservedTaskException.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                return;
            }

            // The ends without a report come later: the file log flushes once a second, and the next start reads its tail.
            if (_steps != (_kind is "failfast" or "stackoverflow" or "native" ? 90 : 3))
                return;
            Log.Info($"[Fixture] Crashing now: {_kind}");
            switch (_kind)
            {
                case "managed":
                    throw new InvalidOperationException("The fixture failed in OnPhysicsProcess.");
                case "ready":
                    AddChild(new ThrowsInReady { Name = "Child" });
                    break;
                case "thread":
                    var thread = new Thread(static () => throw new FormatException("The fixture's worker thread failed.")) { IsBackground = true };
                    thread.Start();
                    thread.Join();
                    break;
                case "failfast":
                    Environment.FailFast("The fixture failed fast.");
                    break;
                case "stackoverflow":
                    Recurse(0);
                    break;
                case "native":
                    NativeFault();
                    break;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void StartFaultingTask() =>
            _ = Task.Run(static () => throw new TimeoutException("The fixture's task failed and nobody looked."));

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int Recurse(int depth)
        {
            Span<byte> pad = stackalloc byte[256];
            pad[0] = (byte)depth;
            return Recurse(depth + 1) + pad[0];
        }

        private static void NativeFault()
        {
            if (OperatingSystem.IsWindows())
                RaiseException(0xC0000005, 0, 0, 0);
            else
                _ = strlen(8); // a read of address 8 inside libc: a native segmentation fault, not a NullReferenceException
        }

        [DllImport("libc", EntryPoint = "strlen")]
        private static extern nint strlen(nint text);

        [DllImport("kernel32.dll")]
        private static extern void RaiseException(uint code, uint flags, uint count, nint arguments);
    }

    /// <summary>A child whose <see cref="Node.OnReady"/> throws (the report must name it, not its parent).</summary>
    public sealed class ThrowsInReady : Node
    {
        protected override void OnReady() => throw new ArgumentException("The fixture's child failed in OnReady.");
    }
}
