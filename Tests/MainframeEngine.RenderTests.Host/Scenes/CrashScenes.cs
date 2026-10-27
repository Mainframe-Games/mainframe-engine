using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// G15: the lit shapes with <c>--simulate-device-lost N</c>: the renderer submits frame N, waits for it, then throws
/// <see cref="VulkanDeviceLostException"/> as a real loss would. <see cref="CloseAfterLoss"/> (the host's catch) checks what
/// the exception carries — the frame and every pass completed, nothing in flight — and that frame N-1's breadcrumbs in the
/// other slot read begin == end == N-1 (normal frames finish every mark), then shuts the engine down through the
/// loss-safe path.
/// </summary>
public sealed class DeviceLostScene(HostOptions host) : LitShapesScene(host)
{
    /// <summary>Called by the host after <c>Run</c> returned or threw: the checks, then the teardown <c>OnClose</c> would run.</summary>
    public void CloseAfterLoss(VulkanDeviceLostException? lost)
    {
        var frame = Host.SimulateDeviceLostAtFrame;
        if (lost is null)
        {
            Fail($"No VulkanDeviceLostException at frame {frame}.");
        }
        else if (lost.Loss is not { } loss)
        {
            Fail("The exception carries no GPU loss report.");
        }
        else
        {
            if (loss.Frame != (long)frame)
                Fail($"The loss names frame {loss.Frame}, expected {frame}.");
            if (!loss.BreadcrumbsAvailable)
                Fail("Breadcrumbs were not available.");
            if (loss.InFlight.Count != 0)
                Fail($"Passes in flight after a finished frame: {string.Join(", ", loss.InFlight)}.");
            foreach (var pass in Enum.GetNames<GpuPass>())
                if (!loss.Completed.Contains(pass))
                    Fail($"Pass {pass} is not marked completed in frame {frame} ({string.Join(", ", loss.Completed)}).");
        }

        if (Renderer is VulkanRenderer renderer)
        {
            var previousSlot = (Vulkan.FrameSlot + 1) % IVulkanContext.MaxFramesInFlight;
            var marks = renderer.ReadBreadcrumbs(previousSlot);
            if (marks is null)
                Fail("The renderer has no breadcrumbs.");
            else
                foreach (var (begin, end) in marks)
                    if (begin != (uint)(frame - 1) || end != (uint)(frame - 1))
                        Fail($"Frame {frame - 1}'s marks read begin {begin}, end {end}.");
        }

        OnClose(); // the window loop ended with the exception: tear down as a clean close would (renderer last)
    }
}

/// <summary>
/// G15: the player's next-launch crash prompt (<see cref="CrashPrompt"/>) for a report of an earlier run, with an upload
/// endpoint (Send report and "Always send reports" shown). The report bundle is written to a scratch folder; the layer is
/// laid out at 1 dp = 1 px from a 960×600 reference. Self-check: the prompt loaded and shows the report's summary.
/// </summary>
public sealed class CrashPromptScene(HostOptions host) : RenderTestGame(host)
{
    public const uint CheckFrame = 30;

    private CrashPrompt? _prompt;
    private string _folder = "";

    private sealed class NoUpload : ICrashUploader
    {
        public Task<CrashUploadResult> SendAsync(CrashReportInfo report, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CrashUploadResult(false, 0, "not in render tests"));
    }

    protected override void LoadScene()
    {
        Renderer.SetClearColor(0.16f, 0.2f, 0.26f);
        _folder = Path.Combine(Host.OutputDirectory, "crash-bundle");
        Directory.CreateDirectory(_folder);
        var report = new CrashReport
        {
            Id = "0123456789abcdef0123456789abcdef",
            Kind = CrashKind.ManagedException,
            Timestamp = new DateTime(2026, 10, 11, 10, 42, 7, DateTimeKind.Utc),
        };
        report.Exception.Add(new CrashException { Type = "System.NullReferenceException", Message = "Object reference not set to an instance of an object." });
        report.Session.NodeType = "Game.PlayerController";
        report.Session.Callback = "OnPhysicsProcess";
        File.WriteAllBytes(Path.Combine(_folder, CrashReport.FileName), report.ToJson());

        CrashReporter.Uploader = new NoUpload();
        var info = new CrashReportInfo(_folder, report.Id, report.Kind, report.Timestamp, report.Summary);
        _prompt = CrashPrompt.Show(Tree, new CrashPromptOptions { GameName = "Forest Demo", Settings = new UserSettings(new ConfigFile()) },
            [info, info with { Folder = _folder + "-earlier" }]);
        if (_prompt?.Layer is { } layer)
        {
            layer.ScaleMode = UiScaleMode.ReferenceResolution;
            layer.ReferenceResolution = new Vector2(960, 600);
        }
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (gameTime.FrameCount != CheckFrame)
            return;
        if (_prompt is null)
            Fail("The prompt did not open.");
        else if (_prompt.Report?.Summary != "NullReferenceException in PlayerController.OnPhysicsProcess")
            Fail($"The prompt shows '{_prompt.Report?.Summary}'.");
        else if (!_prompt.IsLoaded || _prompt.Document.GetElementById("crash-send").IsNull)
            Fail("The prompt's Send report button is missing.");
    }

    protected override void DisposeScene() => CrashReporter.Uploader = null;
}
