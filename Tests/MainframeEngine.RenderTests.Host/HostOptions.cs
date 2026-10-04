using System.Globalization;

namespace MainframeEngine.RenderTests.Host;

/// <summary>
/// Command line: <c>&lt;scene&gt; --out &lt;dir&gt; [--capture 30,60] [--frames N] [--alloc warmup:count]
/// [--size WxH] [--hidden]</c>.
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

    public static HostOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            throw new ArgumentException("Usage: <scene> --out <dir> [--capture 30,60] [--frames N] [--alloc warmup:count] [--size WxH] [--hidden]");

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
        return options with { MaxFrames = Math.Max(options.MaxFrames, needed) };
    }
}
