using System.Diagnostics;
using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Baked sky occlusion and bounce light for a region (ADR 0170, G8e.1; Unreal's volumetric lightmap + sky occlusion, a
/// static cousin of Godot's <c>LightmapGI</c> probes): a grid of probes, each storing how much of the sky it sees in every
/// direction (SH L1) and the light bounced to it off the terrain, trees, rocks and props (SH L1 RGB). Every lit surface in
/// the world samples the probes at its position (trilinear), so shade under a canopy sees a tenth of the sky an open
/// glade sees and takes the green-brown of what surrounds it; reflections there are occluded too. The first visible
/// volume with data in a world is used.
/// </summary>
/// <remarks>
/// <para><b>Baking.</b> <see cref="Bake"/> (the editor's Bake Lighting button, or code) traces the world on the CPU,
/// every core (<see cref="ProbeBaker"/>): the terrain's height field, trees as branch capsules plus a leaf-density grid,
/// <see cref="GIMode.Static"/> meshes, the first directional light and the sky. The result is <see cref="Data"/>; save it
/// with <see cref="LightProbeData.Save"/> (a <c>.mres</c> plus a <c>.probes</c> file). A bake is for one sun direction;
/// moving the sun far, or a boulder, makes it stale.</para>
/// <para><b>Stale bakes.</b> <see cref="LightProbeData.BakeHash"/> hashes everything the bake read. With
/// <see cref="BakeWhenStale"/>, a volume whose data is missing or does not match the scene bakes in the background when
/// it is ready (generated levels, like the Forest's valley) and renders without probes until it is done.</para>
/// <para><b>Layout.</b> <see cref="ProbeLayout.Box"/>: a grid over <see cref="Size"/> around the node at
/// <see cref="ProbeSpacing"/>. <see cref="ProbeLayout.TerrainFollowing"/>: an XZ grid over <see cref="Size"/> with
/// <see cref="LayerHeights"/> above the terrain under each column. The grid is axis-aligned: the node's rotation is
/// ignored.</para>
/// </remarks>
[Tool]
[EditorIcon("bulb")]
public sealed class LightProbeVolume : Node3D, IRenderResourceOwner
{
    private RenderServer? _server;
    private World3D? _world;
    private Task<ProbeBakeResult>? _pending;
    private CancellationTokenSource? _cancel;
    private bool _checked;

    /// <summary>The box the probes fill, centred on the node (metres).</summary>
    [Export]
    public Vector3 Size { get; set; } = new(32f, 16f, 32f);

    [Export]
    public ProbeLayout Layout { get; set; } = ProbeLayout.Box;

    /// <summary>Metres between probes (terrain-following: Y unused).</summary>
    [Export]
    public Vector3 ProbeSpacing { get; set; } = new(2f, 2f, 2f);

    /// <summary>Terrain-following: the layers' heights above the ground (metres, increasing, at most eight).</summary>
    [Export]
    public float[] LayerHeights { get; set; } = [0.3f, 1.2f, 2.5f, 4.5f, 7.5f, 12f, 18f, 27f];

    /// <summary>Terrain-following: the terrain to follow (empty: the first <see cref="Terrain3D"/> in the scene).</summary>
    [Export]
    public NodePath Terrain { get; set; } = "";

    /// <summary>Rays per probe for the sky visibility.</summary>
    [Export(Range = "16,4096,1")]
    public int RaysPerProbe { get; set; } = 256;

    /// <summary>Bounce passes (0: sky occlusion only).</summary>
    [Export(Range = "0,8,1")]
    public int Bounces { get; set; } = 3;

    /// <summary>Scales the bounce light (an art control; 1 is physical).</summary>
    [Export(Range = "0,4,0.01")]
    public float Energy { get; set; } = 1f;

    /// <summary>
    /// How much of the baked sky occlusion applies (an art control, Unreal's sky light occlusion strength): 1 is the bake,
    /// 0 the sky light everywhere as without a volume (the bounce still adds).
    /// </summary>
    [Export(Range = "0,1,0.01")]
    public float SkyOcclusion { get; set; } = 1f;

    /// <summary>
    /// The colour of the occluded sky light that <see cref="SkyOcclusion"/> below 1 leaves (sRGB; Unreal's sky light
    /// occlusion tint): under a canopy, the light its leaves scatter in place of the blocked sky. White: the sky's own
    /// colour.
    /// </summary>
    [Export]
    public System.Drawing.Color OcclusionTint { get; set; } = System.Drawing.Color.White;

    /// <summary><see cref="OcclusionTint"/> in linear RGB (what the shaders read).</summary>
    internal Vector3 OcclusionTintLinear => ColorSpace.SrgbToLinear(new Vector3(OcclusionTint.R, OcclusionTint.G, OcclusionTint.B) / 255f);

    /// <summary>The bake (null: none yet; the world renders as without a volume).</summary>
    [Export]
    public LightProbeData? Data
    {
        get;
        set
        {
            field = value;
            _checked = false;
        }
    }

    /// <summary>Bake in the background when ready if <see cref="Data"/> is missing or stale (levels generated at load).</summary>
    [Export]
    public bool BakeWhenStale { get; set; }

    /// <summary>A bake in the background is running.</summary>
    public bool IsBaking => _pending is { IsCompleted: false };

    /// <summary>Progress of the running bake, 0 … 1.</summary>
    public float BakeProgress { get; private set; }

    /// <summary>Why the last background bake failed (null when it did not).</summary>
    public string? LastBakeError { get; private set; }

    /// <summary>The last bake this node made (null before one).</summary>
    public ProbeBakeResult? LastBake { get; private set; }

    /// <summary>Raised on the main thread when a background bake finished and its data is in <see cref="Data"/>.</summary>
    [Signal]
    public event Action? Baked;

    // ── Render-server state ───────────────────────────────────────────────────────────────────────────────────
    internal Texture3DGpu? Gpu;

    /// <summary>The probe grid this node's settings describe (ground heights come from the bake).</summary>
    public ProbeGrid ResolveGrid()
    {
        var spacing = Vector3.Max(ProbeSpacing, new Vector3(0.1f));
        var size = Vector3.Max(Size, Vector3.Zero);
        var min = GlobalPosition - size * 0.5f;
        var countX = Math.Max(1, (int)MathF.Floor(size.X / spacing.X) + 1);
        var countZ = Math.Max(1, (int)MathF.Floor(size.Z / spacing.Z) + 1);
        if (Layout == ProbeLayout.TerrainFollowing)
        {
            var layers = LayerHeights.Length == 0 ? [0.5f] : LayerHeights.Take(ProbeGrid.MaxLayers).ToArray();
            return new ProbeGrid(ProbeLayout.TerrainFollowing, new Vector3(min.X, GlobalPosition.Y, min.Z), spacing, countX, layers.Length, countZ, layers);
        }

        var countY = Math.Max(1, (int)MathF.Floor(size.Y / spacing.Y) + 1);
        return new ProbeGrid(ProbeLayout.Box, min, spacing, countX, countY, countZ, []);
    }

    /// <summary>The bake's settings from the exports.</summary>
    public ProbeBakeSettings ResolveSettings() => new() { RaysPerProbe = Math.Max(16, RaysPerProbe), Bounces = Math.Max(0, Bounces) };

    /// <summary>
    /// The bake scene of this volume's tree (its root's descendants) and the hash a current bake would have. Reads nodes:
    /// call on the main thread.
    /// </summary>
    internal (ProbeBakeScene Scene, ProbeGrid Grid, string Hash) PrepareBake()
    {
        var builder = Collect(fingerprintOnly: false);
        var scene = builder.Build();
        Log.Info($"[Probes] '{Name}': bake scene: {scene.Describe()}.");
        var grid = ResolveGrid();
        return (scene, grid, ProbeBaker.Hash(scene.ContentHash, grid, ResolveSettings()));
    }

    /// <summary>
    /// The hash a bake of the scene as it is now would store (<see cref="LightProbeData.BakeHash"/>): its inputs hashed
    /// without building the bake's proxies, so checking a committed bake is cheap. Reads nodes: call on the main thread.
    /// </summary>
    public string CurrentBakeHash() => ProbeBaker.Hash(Collect(fingerprintOnly: true).Fingerprint(), ResolveGrid(), ResolveSettings());

    /// <summary>True when <see cref="Data"/> has coefficients baked from the scene as it is now.</summary>
    public bool IsBakeCurrent() => Data is { HasData: true } data && string.Equals(data.BakeHash, CurrentBakeHash(), StringComparison.Ordinal);

    // The scene this volume belongs to (its ancestors up to the viewport that shows it: an editor tab's world, the game's
    // root viewport), collected for a bake or only hashed.
    private ProbeBakeSceneBuilder Collect(bool fingerprintOnly)
    {
        Node sceneRoot = this;
        while (sceneRoot.Parent is { } parent and not SceneViewport)
            sceneRoot = parent;
        var builder = new ProbeBakeSceneBuilder(fingerprintOnly);
        if (Layout == ProbeLayout.TerrainFollowing && !Terrain.IsEmpty && GetNodeOrNull<Terrain3D>(Terrain) is { Data: not null } terrain)
            builder.AddTerrain(terrain); // the one to follow, before any other the walk finds
        builder.AddTree(sceneRoot);
        return builder;
    }

    /// <summary>
    /// Bakes now on the calling thread (every core helps) and stores the result in <see cref="Data"/> (in memory: save it
    /// with <see cref="LightProbeData.Save"/>).
    /// </summary>
    public ProbeBakeResult Bake(IProgress<float>? progress = null, CancellationToken cancellation = default)
    {
        CancelBake();
        var (scene, grid, _) = PrepareBake();
        var result = ProbeBaker.Bake(scene, grid, ResolveSettings(), progress, cancellation);
        Apply(result);
        return result;
    }

    /// <summary>
    /// Starts a bake in the background (the scene is read now, on the main thread); the node takes the data when it is
    /// done (its next process) and raises <see cref="Baked"/>. A running bake is cancelled first.
    /// </summary>
    public void BakeInBackground()
    {
        CancelBake();
        var (scene, grid, _) = PrepareBake();
        StartBake(scene, grid);
    }

    /// <summary>Cancels a running background bake.</summary>
    public void CancelBake()
    {
        _cancel?.Cancel();
        _cancel = null;
        _pending = null;
    }

    private void StartBake(ProbeBakeScene scene, ProbeGrid grid)
    {
        var settings = ResolveSettings();
        var cancel = _cancel = new CancellationTokenSource();
        var progress = new Progress(this);
        BakeProgress = 0f;
        _pending = Task.Run(() => ProbeBaker.Bake(scene, grid, settings, progress, cancel.Token), cancel.Token);
        SetProcess(true);
    }

    private void Apply(ProbeBakeResult result)
    {
        LastBake = result;
        Data = result.Data;
        _checked = true;
        Log.Info($"[Probes] '{Name}': baked {result.Probes} probes ({result.InvalidProbes} inside geometry, fixed up), " +
                 $"{result.Rays / 1e6:0.#}M rays in {result.Elapsed.TotalSeconds:0.00} s (passes: {string.Join(", ", result.PassSeconds.Select(p => p.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)))} s).");
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _world = GetWorld3D();
        _world?.AddProbeVolume(this);
    }

    protected override void OnExitTree()
    {
        CancelBake();
        _world?.RemoveProbeVolume(this);
        _world = null;
        ReleaseGpu();
        base.OnExitTree();
    }

    protected override void OnReady()
    {
        base.OnReady();
        SetProcess(BakeWhenStale);
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        // After every sibling generated its content (their ready ran first or in this frame): check the bake once.
        if (BakeWhenStale && !_checked && _pending is null)
        {
            _checked = true;
            var watch = Stopwatch.StartNew();
            if (IsBakeCurrent())
            {
                Log.Info($"[Probes] '{Name}': the bake is current (checked in {watch.Elapsed.TotalMilliseconds:0} ms).");
            }
            else
            {
                Log.Warning($"[Probes] '{Name}': the bake is {(Data is { HasData: true } ? "stale" : "missing")}; baking in the background " +
                            "(the world renders without probes until it is done). Bake and save it in the editor to commit it.");
                var (scene, grid, _) = PrepareBake();
                StartBake(scene, grid);
            }
        }

        if (_pending is { IsCompleted: true } pending)
        {
            _pending = null;
            _cancel = null;
            if (pending.IsCompletedSuccessfully)
            {
                Apply(pending.Result);
                Baked?.Invoke();
            }
            else if (pending.Exception is { } e)
            {
                LastBakeError = (e.InnerException ?? e).ToString();
                Log.Error($"[Probes] '{Name}': the bake failed: {e.InnerException?.Message ?? e.Message}");
            }
        }

        if (_pending is null && (!BakeWhenStale || _checked))
            SetProcess(false);
    }

    /// <summary>Called by the render server when it uploads the volume, so a shutdown with the node alive frees it.</summary>
    internal void TrackRenderResources(RenderServer server)
    {
        if (ReferenceEquals(_server, server))
            return;
        _server?.Untrack(this);
        _server = server;
        server.Track(this);
    }

    private void ReleaseGpu()
    {
        Gpu?.Dispose(); // deletion-queued: frames in flight keep it
        Gpu = null;
        _server?.Untrack(this);
        _server = null;
    }

    void IRenderResourceOwner.ReleaseRenderResourcesForShutdown()
    {
        Gpu?.Dispose();
        Gpu = null;
        _server = null;
    }

    /// <summary>The data the renderer binds: <see cref="Data"/> when it has coefficients.</summary>
    internal LightProbeData? RenderData => Data is { HasData: true } data ? data : null;

    private sealed class Progress(LightProbeVolume volume) : IProgress<float>
    {
        public void Report(float value) => volume.BakeProgress = value;
    }
}
