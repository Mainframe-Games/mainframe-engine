using System.Globalization;

namespace MainframeEngine.RenderTests.Host;

/// <summary>
/// Command line: <c>&lt;scene&gt; --out &lt;dir&gt; [--capture 30,60] [--frames N] [--alloc warmup:count]
/// [--size WxH] [--hidden] [--resize WxH@frame] [--toggle-vsync frame] [--quit-error frame] [--pipeline-cache dir]
/// [--count N] [--perf warmup:frames] [--no-validation] [--ui-hidden-until frame] [--update-rate hz] [--no-shadows]</c>.
/// </summary>
public sealed record HostOptions
{
    public required string Scene { get; init; }
    public required string OutputDirectory { get; init; }
    public uint[] CaptureFrames { get; init; } = [];
    public int MaxFrames { get; init; }
    public int AllocationWarmupFrames { get; init; }
    public int AllocationMeasuredFrames { get; init; }
    public int Width { get; init; } = 320;
    public int Height { get; init; } = 240;
    public bool Hidden { get; init; }

    /// <summary>Resize the window to <see cref="ResizeTo"/> (points) on this frame; 0 = never.</summary>
    public uint ResizeAtFrame { get; init; }
    public (int Width, int Height) ResizeTo { get; init; }

    /// <summary>Flip <c>Renderer.VSync</c> (present mode → swapchain recreation) on this frame; 0 = never.</summary>
    public uint ToggleVSyncAtFrame { get; init; }

    /// <summary>Call <c>Quit(ExitCode.Error)</c> on this frame; 0 = never.</summary>
    public uint QuitWithErrorAtFrame { get; init; }

    /// <summary>Directory for the persisted pipeline cache (overrides <c>MAINFRAME_PIPELINE_CACHE_DIR</c>).</summary>
    public string? PipelineCacheDirectory { get; init; }

    /// <summary>Scene-specific size (the instance count of the <c>instances</c> scene); 0 = the scene's default.</summary>
    public int Count { get; init; }

    /// <summary>Frame-time measurement: frames skipped first, then frames measured; 0 = off.</summary>
    public int PerfWarmupFrames { get; init; }
    public int PerfMeasuredFrames { get; init; }

    /// <summary>Runs without the validation layers (performance measurements).</summary>
    public bool NoValidation { get; init; }

    /// <summary>Hide the game UI's renderer (<c>VulkanUiRenderer.Visible</c>) up to and including this frame; 0 = never.</summary>
    public uint UiHiddenUntilFrame { get; init; }

    /// <summary>Limit updates to this rate while rendering unthrottled (several renders per update); 0 = off.</summary>
    public int UpdateRate { get; init; }

    /// <summary>Turns <c>CastsShadows</c> off on every light (measures what the shadow pass costs).</summary>
    public bool NoShadows { get; init; }

    public static HostOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            throw new ArgumentException("Usage: <scene> --out <dir> [--capture 30,60] [--frames N] [--alloc warmup:count] [--size WxH] [--hidden] [--resize WxH@frame] [--toggle-vsync frame] [--quit-error frame]");

        var options = new HostOptions { Scene = args[0], OutputDirectory = "" };
        for (var i = 1; i < args.Count; i++)
        {
            string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--out":
                    options = options with { OutputDirectory = Path.GetFullPath(Next()) };
                    break;
                case "--capture":
                    options = options with
                    {
                        CaptureFrames = [.. Next().Split(',').Select(f => uint.Parse(f, CultureInfo.InvariantCulture)).Order()],
                    };
                    break;
                case "--frames":
                    options = options with { MaxFrames = int.Parse(Next(), CultureInfo.InvariantCulture) };
                    break;
                case "--alloc":
                    var parts = Next().Split(':');
                    options = options with
                    {
                        AllocationWarmupFrames = int.Parse(parts[0], CultureInfo.InvariantCulture),
                        AllocationMeasuredFrames = int.Parse(parts[1], CultureInfo.InvariantCulture),
                    };
                    break;
                case "--size":
                    var size = Next().Split('x');
                    options = options with
                    {
                        Width = int.Parse(size[0], CultureInfo.InvariantCulture),
                        Height = int.Parse(size[1], CultureInfo.InvariantCulture),
                    };
                    break;
                case "--hidden":
                    options = options with { Hidden = true };
                    break;
                case "--resize":
                    var resize = Next().Split('@');
                    var to = resize[0].Split('x');
                    options = options with
                    {
                        ResizeTo = (int.Parse(to[0], CultureInfo.InvariantCulture), int.Parse(to[1], CultureInfo.InvariantCulture)),
                        ResizeAtFrame = uint.Parse(resize[1], CultureInfo.InvariantCulture),
                    };
                    break;
                case "--toggle-vsync":
                    options = options with { ToggleVSyncAtFrame = uint.Parse(Next(), CultureInfo.InvariantCulture) };
                    break;
                case "--quit-error":
                    options = options with { QuitWithErrorAtFrame = uint.Parse(Next(), CultureInfo.InvariantCulture) };
                    break;
                case "--pipeline-cache":
                    options = options with { PipelineCacheDirectory = Path.GetFullPath(Next()) };
                    break;
                case "--count":
                    options = options with { Count = int.Parse(Next(), CultureInfo.InvariantCulture) };
                    break;
                case "--perf":
                    var perf = Next().Split(':');
                    options = options with
                    {
                        PerfWarmupFrames = int.Parse(perf[0], CultureInfo.InvariantCulture),
                        PerfMeasuredFrames = int.Parse(perf[1], CultureInfo.InvariantCulture),
                    };
                    break;
                case "--no-validation":
                    options = options with { NoValidation = true };
                    break;
                case "--ui-hidden-until":
                    options = options with { UiHiddenUntilFrame = uint.Parse(Next(), CultureInfo.InvariantCulture) };
                    break;
                case "--update-rate":
                    options = options with { UpdateRate = int.Parse(Next(), CultureInfo.InvariantCulture) };
                    break;
                case "--no-shadows":
                    options = options with { NoShadows = true };
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        if (string.IsNullOrEmpty(options.OutputDirectory))
            throw new ArgumentException("--out is required.");

        // Run at least one frame past the last capture / measurement window.
        var needed = Math.Max(
            options.CaptureFrames.Length > 0 ? (int)options.CaptureFrames[^1] + 1 : 1,
            options.AllocationMeasuredFrames > 0 ? options.AllocationWarmupFrames + options.AllocationMeasuredFrames + 2 : 1);
        if (options.PerfMeasuredFrames > 0)
            needed = Math.Max(needed, options.PerfWarmupFrames + options.PerfMeasuredFrames + 2);
        return options with { MaxFrames = Math.Max(options.MaxFrames, needed) };
    }
}
