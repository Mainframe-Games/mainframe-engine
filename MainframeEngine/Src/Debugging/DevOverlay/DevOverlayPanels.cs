using MainframeEngine.UI.Rml;

namespace MainframeEngine;

/// <summary>
/// The <see cref="DevOverlay"/>'s built-in panels: frame, renderer, shadows (with the shadow-map images), GPU memory,
/// audio mixer, physics and network. Each one reads its numbers from the servers of the overlay's tree through a
/// snapshot (<c>DevOverlayStats.cs</c>) filled at the refresh rate; a panel whose server is not registered shows "—".
/// </summary>
internal static class DevOverlayPanels
{
    private const string Missing = """<div data-if="!has" class="dev-row"><span class="v">—</span></div>""";

    public static void AddBuiltIns(DevOverlay overlay, Engine? engine)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        var sources = new DevOverlaySources(overlay.Servers, engine);
        AddFrame(overlay, sources);
        AddRenderer(overlay, sources);
        AddShadows(overlay, sources);
        AddGpu(overlay, sources);
        AddAudio(overlay, sources);
        AddPhysics(overlay, sources);
        AddNetwork(overlay, sources);
    }

    // Runs the snapshot refresh at the overlay's rate and marks every value of the panel changed (the views only touch
    // the DOM where the text differs, so values nobody changed cost nothing).
    private static void RefreshAt(DevOverlay overlay, DevOverlayPanel panel, Action refresh) =>
        overlay.Refreshed += () =>
        {
            refresh();
            panel.Model?.DirtyAll();
        };

    private static void AddFrame(DevOverlay overlay, DevOverlaySources sources)
    {
        var stats = new FrameStats(sources);
        var panel = overlay.AddPanel("frame", "Frame", """
            <div class="dev-row"><span>FPS</span><span class="v">{{fps | format(1)}}</span></div>
            <div class="dev-row"><span>Frame ms (avg/min/max)</span><span class="v">{{ms | format(2)}} / {{min | format(2)}} / {{max | format(2)}}</span></div>
            <div class="dev-row"><span>Frame</span><span class="v">{{frame}}</span></div>
            <div class="dev-row"><span>VSync</span><input type="checkbox" data-checked="vsync"/></div>
            <div class="dev-row"><span>UI draw calls</span><span class="v">{{ui}}</span></div>
            """, p => p.Model!
            .Bind("fps", stats, static s => s.Fps)
            .Bind("ms", stats, static s => s.Ms)
            .Bind("min", stats, static s => s.MinMs)
            .Bind("max", stats, static s => s.MaxMs)
            .Bind("frame", stats, static s => (int)s.Frame)
            .Bind("ui", stats, static s => s.UiDrawCalls)
            .Bind("vsync", stats, static s => s.VSync, static (s, v) => s.VSync = v));

        // Accumulated every visible frame, published at the refresh (nothing is published for an empty window).
        overlay.Frame += stats.Accumulate;
        overlay.Refreshed += () =>
        {
            if (stats.Refresh())
                panel.Model?.DirtyAll();
        };
    }

    private static void AddRenderer(DevOverlay overlay, DevOverlaySources sources)
    {
        var stats = new RendererStats(sources);
        var panel = overlay.AddPanel("renderer", "Renderer", """
            <div data-if="has">
              <div class="dev-row"><span>Exposure</span><span class="v">{{exposure | format(2)}}</span></div>
              <div class="dev-row"><input class="dev-slider" type="range" min="0.1" max="8" step="0.05" data-value="exposure"/><button class="dev-button" data-event-click="reset()">Reset</button></div>
              <div class="dev-row"><span>Colour pipeline</span></div>
              <div class="dev-row dev-wrap"><span class="v">{{pipeline}}</span></div>
              <div class="dev-heading">Meshes</div>
              <div class="dev-row"><span>Draw calls</span><span class="v">{{draws}}</span></div>
              <div class="dev-row"><span>Surfaces / instances / culled</span><span class="v">{{surfaces}} / {{instances}} / {{culled}}</span></div>
              <div class="dev-row"><span>Binds: pipeline / material</span><span class="v">{{pipelineBinds}} / {{materialBinds}}</span></div>
              <div class="dev-row"><span>Shadow draws / ID draws</span><span class="v">{{shadowDraws}} / {{idDraws}}</span></div>
              <div class="dev-row"><span>Resident meshes / materials / textures</span><span class="v">{{meshes}} / {{materials}} / {{textures}}</span></div>
              <div class="dev-row"><span>Pipelines (cache hits)</span><span class="v">{{pipelines}} ({{cacheHits}})</span></div>
            </div>
            """ + Missing, p => p.Model!
            .Bind("has", stats, static s => s.Has)
            .Bind("exposure", stats, static s => s.Exposure, static (s, v) => s.Exposure = v)
            .Event("reset", stats.ResetExposure)
            .Bind("pipeline", stats, static s => s.Pipeline)
            .Bind("draws", stats, static s => s.Draws)
            .Bind("surfaces", stats, static s => s.Surfaces)
            .Bind("instances", stats, static s => s.Instances)
            .Bind("culled", stats, static s => s.Culled)
            .Bind("pipelineBinds", stats, static s => s.PipelineBinds)
            .Bind("materialBinds", stats, static s => s.MaterialBinds)
            .Bind("shadowDraws", stats, static s => s.ShadowDraws)
            .Bind("idDraws", stats, static s => s.IdDraws)
            .Bind("meshes", stats, static s => s.Meshes)
            .Bind("materials", stats, static s => s.Materials)
            .Bind("textures", stats, static s => s.Textures)
            .Bind("pipelines", stats, static s => s.Pipelines)
            .Bind("cacheHits", stats, static s => s.CacheHits));
        RefreshAt(overlay, panel, stats.Refresh);
    }

    private static void AddShadows(DevOverlay overlay, DevOverlaySources sources)
    {
        var stats = new ShadowStats(sources);
        var servers = sources.Servers;
        var cascadeImages = string.Concat(Enumerable.Range(0, ShadowSystem.MaxCascades)
            .Select(i => $"""<img class="dev-map" src="engine://{CascadeTexture(i)}"/>"""));
        var panel = overlay.AddPanel("shadows", "Shadows", """
            <div data-if="has">
              <div class="dev-row"><span>Cascade colours</span><input type="checkbox" data-checked="debugCascades"/></div>
              <div class="dev-row"><span>Stable cascades</span><input type="checkbox" data-checked="stable"/></div>
              <div class="dev-row"><span>Filter</span><select data-value="filter"><option value="0">Hard</option><option value="1">PCF 3x3</option><option value="2">Poisson 16</option></select></div>
              <div class="dev-row"><span>Filter radius</span><span class="v">{{radius | format(2)}} texels</span></div>
              <div class="dev-row"><input class="dev-slider" type="range" min="0.5" max="8" step="0.1" data-value="radius"/></div>
              <div class="dev-row"><span>Passes (rendered / planned)</span><span class="v">{{passes}} / {{planned}}</span></div>
              <div class="dev-row"><span>Draws / instances / culled</span><span class="v">{{draws}} / {{casters}} / {{culled}}</span></div>
              <div class="dev-row"><span>Cascades</span><span class="v">{{cascadeRes}}²</span></div>
              <div class="dev-row"><span>Atlas (packs)</span><span class="v">{{atlasSize}}² ({{atlasPacked}})</span></div>
              <div class="dev-row"><span>Map memory</span><span class="v">{{mapMiB | format(1)}} MiB</span></div>
              <div class="dev-row"><span>CPU / GPU ms</span><span class="v">{{cpuMs | format(3)}} / {{gpuMs | format(3)}}</span></div>
              <div class="dev-heading">Maps</div>
              <div class="dev-maps"><!--cascades--></div>
              <div class="dev-maps"><img class="dev-atlas" src="engine://dev-shadow-atlas"/></div>
            </div>
            """.Replace("<!--cascades-->", cascadeImages, StringComparison.Ordinal) + Missing, p =>
            {
                p.Model!
                    .Bind("has", stats, static s => s.Has)
                    .Bind("debugCascades", stats, static s => s.DebugCascades, static (s, v) => s.DebugCascades = v)
                    .Bind("stable", stats, static s => s.Stable, static (s, v) => s.Stable = v)
                    .Bind("filter", stats, static s => s.Filter, static (s, v) => s.Filter = v)
                    .Bind("radius", stats, static s => s.Radius, static (s, v) => s.Radius = v)
                    .Bind("passes", stats, static s => s.Passes)
                    .Bind("planned", stats, static s => s.Planned)
                    .Bind("draws", stats, static s => s.Draws)
                    .Bind("casters", stats, static s => s.Casters)
                    .Bind("culled", stats, static s => s.Culled)
                    .Bind("cascadeRes", stats, static s => s.CascadeRes)
                    .Bind("atlasSize", stats, static s => s.AtlasSize)
                    .Bind("atlasPacked", stats, static s => s.AtlasPacked)
                    .Bind("mapMiB", stats, static s => s.MapMiB)
                    .Bind("cpuMs", stats, static s => s.CpuMs)
                    .Bind("gpuMs", stats, static s => s.GpuMs);

                // The depth maps come straight from the shadow system's images (shown as grey), re-resolved every frame.
                if (servers.Get<UiServer>() is not { } ui)
                    return;
                for (var i = 0; i < ShadowSystem.MaxCascades; i++)
                {
                    var cascade = i;
                    ui.RegisterTexture(CascadeTexture(i), () => ShadowStats.Texture(servers, cascade));
                }

                ui.RegisterTexture(AtlasTexture, () => ShadowStats.Texture(servers, -1));
            });
        RefreshAt(overlay, panel, stats.Refresh);
    }

    private static string CascadeTexture(int cascade) => $"dev-shadow-cascade-{cascade}";

    private const string AtlasTexture = "dev-shadow-atlas"; // the RML above names it literally

    private static readonly RmlStructType<GpuTypeRow> GpuRowType = new RmlStructType<GpuTypeRow>()
        .Member("index", static r => r.Index)
        .Member("heap", static r => (int)r.Heap)
        .Member("flags", static r => r.Flags)
        .Member("allocations", static r => r.Allocations)
        .Member("usedMiB", static r => r.UsedMiB)
        .Member("reservedMiB", static r => r.ReservedMiB);

    private static void AddGpu(DevOverlay overlay, DevOverlaySources sources)
    {
        var stats = new GpuStats(sources);
        var panel = overlay.AddPanel("gpu", "GPU memory", """
            <div data-if="has">
              <div class="dev-row"><span>Allocations</span><span class="v">{{allocations}} in {{deviceMemories}} VkDeviceMemory</span></div>
              <div class="dev-row"><span>Blocks / dedicated</span><span class="v">{{blocks}} / {{dedicated}}</span></div>
              <div class="dev-row"><span>Used / reserved</span><span class="v">{{usedMiB | format(1)}} / {{reservedMiB | format(1)}} MiB</span></div>
              <div class="dev-row" data-for="t : types"><span>type {{t.index}} (heap {{t.heap}}, {{t.flags}}): {{t.allocations}} allocs</span><span class="v">{{t.usedMiB | format(1)}} / {{t.reservedMiB | format(1)}} MiB</span></div>
              <div class="dev-heading">Uploads and deletions</div>
              <div class="dev-row"><span>Staging ring (KiB)</span><span class="v">{{ringUsedKiB}} / {{ringKiB}}</span></div>
              <div class="dev-row"><span>Pending uploads / uploaded</span><span class="v">{{uploadsPending}} / {{uploadedMiB | format(1)}} MiB</span></div>
              <div class="dev-row"><span>Deletion queue (frame)</span><span class="v">{{deletionsPending}} ({{frameNo}})</span></div>
              <div class="dev-row"><span>Pipeline cache KiB / shader modules</span><span class="v">{{pipelineCacheKiB}} / {{shaderModules}}</span></div>
            </div>
            """ + Missing, p => p.Model!
            .Bind("has", stats, static s => s.Has)
            .Bind("allocations", stats, static s => s.Allocations)
            .Bind("deviceMemories", stats, static s => s.DeviceMemories)
            .Bind("blocks", stats, static s => s.Blocks)
            .Bind("dedicated", stats, static s => s.Dedicated)
            .Bind("usedMiB", stats, static s => s.UsedMiB)
            .Bind("reservedMiB", stats, static s => s.ReservedMiB)
            .BindList("types", stats.Types, GpuRowType)
            .Bind("ringUsedKiB", stats, static s => s.RingUsedKiB)
            .Bind("ringKiB", stats, static s => s.RingKiB)
            .Bind("uploadsPending", stats, static s => s.UploadsPending)
            .Bind("uploadedMiB", stats, static s => s.UploadedMiB)
            .Bind("deletionsPending", stats, static s => s.DeletionsPending)
            .Bind("frameNo", stats, static s => s.Frame)
            .Bind("pipelineCacheKiB", stats, static s => s.PipelineCacheKiB)
            .Bind("shaderModules", stats, static s => s.ShaderModules));
        RefreshAt(overlay, panel, stats.Refresh);
    }

    private static readonly RmlStructType<AudioBus> BusType = new RmlStructType<AudioBus>()
        .Member("name", static b => b.Name)
        .Member("volume", static b => AudioStats.FromBusDb(b.VolumeDb), static (b, v) => b.VolumeDb = AudioStats.ToBusDb(v))
        .Member("mute", static b => b.Mute, static (b, v) => b.Mute = v)
        .Member("solo", static b => b.Solo, static (b, v) => b.Solo = v)
        .Member("meter", static b => AudioStats.Meter(b));

    private static void AddAudio(DevOverlay overlay, DevOverlaySources sources)
    {
        var stats = new AudioStats(sources);
        var panel = overlay.AddPanel("audio", "Audio", """
            <div data-if="has">
              <div class="dev-row"><span>Device</span><span class="v">{{device}}</span></div>
              <div class="dev-row"><span>Voices / steals / underruns</span><span class="v">{{voices}}/{{totalVoices}}  {{steals}}  {{underruns}}</span></div>
              <div class="dev-bus" data-for="b : buses">
                <span class="dev-bus-name">{{b.name}}</span>
                <input class="dev-slider" type="range" min="-60" max="6" step="0.5" data-value="b.volume"/>
                <span class="dev-tag">M</span><input type="checkbox" data-checked="b.mute"/>
                <span class="dev-tag">S</span><input type="checkbox" data-checked="b.solo"/>
                <div class="dev-meter-track"><div class="dev-meter" data-style-width="(b.meter * 100) + '%'"></div></div>
              </div>
            </div>
            """ + Missing, p => p.Model!
            .Bind("has", stats, static s => s.Has)
            .Bind("device", stats, static s => s.Device)
            .Bind("voices", stats, static s => s.Voices)
            .Bind("totalVoices", stats, static s => s.TotalVoices)
            .Bind("steals", stats, static s => s.Steals)
            .Bind("underruns", stats, static s => s.Underruns)
            .BindList("buses", stats.Buses, BusType));
        RefreshAt(overlay, panel, stats.Refresh);
    }

    private static void AddPhysics(DevOverlay overlay, DevOverlaySources sources)
    {
        var stats = new PhysicsStats(sources);
        var root = overlay.Tree.Root;
        var panel = overlay.AddPanel("physics", "Physics", """
            <div class="dev-row"><span>Collision shapes</span><input type="checkbox" data-checked="shapes"/></div>
            <div class="dev-row"><span>Light gizmos</span><input type="checkbox" data-checked="lightGizmos"/></div>
            <div class="dev-row"><span>Axis gizmo</span><input type="checkbox" data-checked="axisGizmo"/></div>
            <div data-if="has">
              <div class="dev-row"><span>3D bodies (awake)</span><span class="v">{{bodies3d}} ({{awake3d}})</span></div>
              <div class="dev-row"><span>2D bodies (awake)</span><span class="v">{{bodies2d}} ({{awake2d}})</span></div>
            </div>
            """ + Missing, p => p.Model!
            .Bind("has", stats, static s => s.Has)
            .Bind("shapes", stats, static s => s.Shapes, static (s, v) => s.Shapes = v)
            .Bind("lightGizmos", stats, static s => s.LightGizmos, static (s, v) => s.LightGizmos = v)
            .Bind("axisGizmo", stats, static s => s.AxisGizmo, static (s, v) => s.AxisGizmo = v)
            .Bind("bodies3d", stats, static s => s.Bodies3D)
            .Bind("awake3d", stats, static s => s.Awake3D)
            .Bind("bodies2d", stats, static s => s.Bodies2D)
            .Bind("awake2d", stats, static s => s.Awake2D));
        RefreshAt(overlay, panel, () => stats.Refresh(root));
    }

    private static void AddNetwork(DevOverlay overlay, DevOverlaySources sources)
    {
        var stats = new NetworkPanelStats(sources);
        var panel = overlay.AddPanel("network", "Network", """
            <div data-if="active">
              <div class="dev-row"><span>Mode</span><span class="v">{{mode}}</span></div>
              <div class="dev-row"><span>Tick / networked nodes</span><span class="v">{{tick}} / {{nodes}}</span></div>
              <div class="dev-row"><span>Clients</span><span class="v">{{clients}}</span></div>
              <div class="dev-row"><span>Out / in</span><span class="v">{{outBps | format(0)}} / {{inBps | format(0)}} B/s</span></div>
            </div>
            <div data-if="!active" class="dev-row"><span class="v">Offline</span></div>
            """, p => p.Model!
            .Bind("active", stats, static s => s.Active)
            .Bind("mode", stats, static s => s.Mode)
            .Bind("tick", stats, static s => s.Tick)
            .Bind("nodes", stats, static s => s.Nodes)
            .Bind("clients", stats, static s => s.Clients)
            .Bind("outBps", stats, static s => s.OutBps)
            .Bind("inBps", stats, static s => s.InBps));
        RefreshAt(overlay, panel, stats.Refresh);
    }
}
