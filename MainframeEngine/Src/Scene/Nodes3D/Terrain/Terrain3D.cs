using System.Globalization;
using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A square heightmap terrain (G8a, modelled on TerraBrush): <see cref="Data"/> holds the knobs and layers; the node
/// builds chunk meshes and collision from them and answers exact queries. Terrain-local X and Z run from 0 to
/// <see cref="TerrainData.SizeMeters"/> from the node's <see cref="Node3D.GlobalPosition"/> (the map corner); rotation
/// and scale are not supported.
/// </summary>
/// <remarks>
/// <para>Chunks are internal, unsaved <see cref="TerrainChunk3D"/> children (one per LOD level) and one
/// <see cref="StaticBody3D"/> per chunk with a <see cref="ConcavePolygonShape3D"/> whose faces are exactly the chunk's
/// LOD 0 triangles. The Realistic profile geomipmaps: up to four levels per chunk (every 1st, 2nd, 4th, 8th vertex), each
/// with a skirt hanging below its edges by the coarsest level's error plus one quad, so neighbours at any levels show
/// no cracks; <c>OnProcess</c> shows, per chunk, the coarsest level whose height error projects to at most one pixel
/// (a 1080p view at 60°, scaled by <see cref="LodBias"/>). Collision and queries stay at full resolution.</para>
/// <para>Every edit (through these methods or the <see cref="TerrainData"/>) rebuilds only the touched chunks' meshes
/// and collision and raises <see cref="Changed"/>. Queries are main-thread, allocation-free and clamp to the map.</para>
/// </remarks>
[Tool]
[EditorIcon("mountain")]
public sealed class Terrain3D : Node3D, ISceneSaveHook, IWaterBody3D, IRenderResourceOwner
{
    /// <summary>What <see cref="SurfaceAt"/> returns where there is water.</summary>
    public const int WaterSurface = 255;

    /// <summary>Most LOD levels per chunk (Realistic).</summary>
    public const int MaxLodLevels = 4;

    /// <summary>Pixels per radian of the reference view the LOD error is projected with (1080 px over 60°).</summary>
    public const float LodPixelsPerRadian = 935f;

    /// <summary>The material chunks draw with when <see cref="Material"/> is null: matte grey.</summary>
    public static StandardMaterial3D DefaultMaterial { get; } = new()
    {
        ResourceName = "Terrain default",
        AlbedoColor = System.Drawing.Color.FromArgb(255, 150, 150, 145),
        Specular = 0.05f,
        Shininess = 8f,
    };

    private readonly Action<TerrainLayers, Rect2I, bool> _onEdited;
    private TerrainData? _data;
    private TerrainData? _subscribed;
    private TerrainEdit? _edit;
    private bool _built;
    private int _builtVersion;
    private int _levels;
    private int _chunks;
    private int _chunkQuads;
    private TerrainChunk3D[] _lodNodes = [];      // chunk · levels + level
    private float[] _lodErrors = [];              // chunk · levels + level (metres)
    private float[] _skirtDepth = [];             // per chunk
    private byte[] _currentLod = [];              // per chunk
    private int[][] _levelIndices = [];           // shared by every chunk
    private StaticBody3D?[] _bodies = [];
    private ConcavePolygonShape3D?[] _shapes = [];
    private bool[] _pendingCollision = [];
    private MeshInstance3D?[] _waterNodes = [];   // per chunk, null when dry
    private Aabb _wetBounds = Aabb.Empty;         // terrain-local, wet vertices
    private World3D? _world;
    private TerrainFoliage3D? _foliage;
    private TerrainMacroTexture? _macro;
    private Task<TerrainMacroTexture>? _macroBake; // the build's bake, on a worker thread
    private bool _macroDirty;
    private float _macroQuiet;
    private RenderServer? _server;

    /// <summary>The macro texture on the GPU (ADR 0175; the render server uploads it the first frame it binds it).</summary>
    internal Texture2DArrayGpu? MacroGpu;

    public Terrain3D()
    {
        _onEdited = OnDataEdited;
    }

    /// <summary>The terrain's settings and layers. Replacing it tears the chunks down and rebuilds them.</summary>
    [Export]
    public TerrainData? Data
    {
        get => _data;
        set
        {
            if (ReferenceEquals(_data, value))
                return;
            Unsubscribe();
            FreeChunks();
            _data = value;
            if (value is not null && IsInsideTree && IsNodeReady)
                Build();
        }
    }

    /// <summary>
    /// What the chunks draw with (any material; null: <see cref="DefaultMaterial"/>). A <see cref="TerrainSplatMaterial3D"/>
    /// is linked to this terrain (<see cref="TerrainSplatMaterial3D.Terrain"/>) so it reads the splat maps.
    /// </summary>
    [Export]
    public Material? Material
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
                return;
            if (field is TerrainSplatMaterial3D old && ReferenceEquals(old.Terrain, this))
                old.Terrain = null;
            field = value;
            if (value is TerrainSplatMaterial3D splat)
                splat.Terrain = this;
            foreach (var node in _lodNodes)
                node.RenderStamp++;
            if (MacroTextureEnabled && _built)
                _macroDirty = true;
        }
    }

    /// <summary>
    /// What the pond and lake surfaces (from the water layer) draw with: null is <see cref="DefaultWaterMaterial"/>.
    /// </summary>
    [Export]
    public Material? WaterMaterial
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
                return;
            field = value;
            foreach (var node in _waterNodes)
                if (node is not null)
                    node.MaterialOverride = value ?? DefaultWaterMaterial;
        }
    }

    /// <summary>The pond material when <see cref="WaterMaterial"/> is null: a default <see cref="WaterMaterial3D"/>.</summary>
    public static WaterMaterial3D DefaultWaterMaterial { get; } = new() { ResourceName = "Terrain water (default)" };

    /// <summary>
    /// Bake the terrain's <see cref="MacroTexture"/> (ADR 0175, RVT-lite): its surface from above in a two-layer array the
    /// world's surfaces with <see cref="StandardMaterial3D.TerrainBlend"/> blend towards near the ground. Baked on a worker
    /// thread when the terrain is built (available a few frames later, about a second at 2048² over eight layers) and again
    /// half a second after the last edit; off by default.
    /// </summary>
    [ExportGroup("Macro texture")]
    [Export]
    public bool MacroTextureEnabled
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            if (!value)
                ReleaseMacro();
            else if (_built)
                RebakeMacroTexture();
        }
    }

    /// <summary>Texels per side of <see cref="MacroTexture"/> (2048: 12.5 cm over 256 m).</summary>
    [Export(Range = "64,4096,1")]
    public int MacroTextureResolution
    {
        get;
        set
        {
            value = Math.Clamp(value, 64, 4096);
            if (field == value)
                return;
            field = value;
            if (MacroTextureEnabled && _built)
                _macroDirty = true;
        }
    } = TerrainMacroTexture.DefaultResolution;

    /// <summary>The baked macro texture (ADR 0175), or null (<see cref="MacroTextureEnabled"/> off, or not built yet).</summary>
    public TerrainMacroTexture? MacroTexture => _macro;

    /// <summary>Bakes <see cref="MacroTexture"/> now from the data and material (needs a built terrain); returns it.</summary>
    public TerrainMacroTexture? RebakeMacroTexture()
    {
        _macroDirty = false;
        _macroBake = null; // a running build-time bake is superseded
        if (_data is null || !_data.IsLoaded)
            return _macro;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _macro = TerrainMacroTexture.Bake(_data, Material, MacroTextureResolution);
        Log.Info($"[Terrain] '{Name}': macro texture {_macro.Resolution}² baked in {watch.Elapsed.TotalMilliseconds:0} ms.");
        return _macro;
    }

    /// <summary>True while the build's macro bake runs on its worker thread.</summary>
    public bool IsBakingMacroTexture => _macroBake is not null;

    private void StartMacroBake()
    {
        var data = _data!;
        var material = Material;
        var resolution = MacroTextureResolution;
        var name = Name;
        _macroBake = Task.Run(() =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var macro = TerrainMacroTexture.Bake(data, material, resolution);
            Log.Info($"[Terrain] '{name}': macro texture {macro.Resolution}² baked in {watch.Elapsed.TotalMilliseconds:0} ms (worker thread).");
            return macro;
        });
    }

    private void ReleaseMacro()
    {
        _macroBake = null;
        _macro = null;
        _macroDirty = false;
        ReleaseMacroGpu();
    }

    private void ReleaseMacroGpu()
    {
        MacroGpu?.Dispose(); // deletion-queued: frames in flight keep it
        MacroGpu = null;
        _server?.Untrack(this);
        _server = null;
    }

    /// <summary>Called by the render server when it uploads the macro texture, so a shutdown with the node alive frees it.</summary>
    internal void TrackRenderResources(RenderServer server)
    {
        if (ReferenceEquals(_server, server))
            return;
        _server?.Untrack(this);
        _server = server;
        server.Track(this);
    }

    void IRenderResourceOwner.ReleaseRenderResourcesForShutdown()
    {
        MacroGpu?.Dispose();
        MacroGpu = null;
        _server = null;
    }

    /// <summary>Realistic chunk LOD: above 1 keeps finer levels further away, below 1 drops them sooner.</summary>
    [Export(Range = "0.25,4,0.05")]
    public float LodBias { get; set; } = 1f;

    /// <summary>
    /// A runtime quality knob (a settings menu's "ground cover density"): the share of each foliage tile's instances that
    /// draw, 0–1 (default 1). The instances are sorted by a uniform hash, so a lower value thins every tile evenly; nothing
    /// is rebuilt or uploaded. Not saved.
    /// </summary>
    public float FoliageDensityScale
    {
        get;
        set => field = Math.Clamp(value, 0f, 1f);
    } = 1f;

    /// <summary>
    /// A runtime quality knob (a settings menu's "ground cover distance"): scales every foliage type's
    /// <see cref="FoliageType.CullDistance"/> and <see cref="FoliageType.ThinBand"/>, 0.1–1 (default 1). Nothing is rebuilt. Not
    /// saved.
    /// </summary>
    public float FoliageDistanceScale
    {
        get;
        set => field = Math.Clamp(value, 0.1f, 1f);
    } = 1f;

    /// <summary>Records edits for undo (<see cref="TerrainEdit.Begin"/> / <see cref="TerrainEdit.End"/>).</summary>
    public TerrainEdit Edit => _edit ??= new TerrainEdit(this);

    /// <summary>Raised synchronously after every edit with what changed.</summary>
    public event Action<TerrainChange>? Changed;

    /// <summary>LOD levels per chunk (1 for Faceted; 0 before the chunks are built).</summary>
    public int LodLevels => _levels;

    /// <summary>Chunks per side (0 before they are built).</summary>
    public int ChunksPerSide => _chunks;

    /// <summary>The LOD level chunk (<paramref name="cx"/>, <paramref name="cz"/>) shows.</summary>
    public int GetChunkLod(int cx, int cz) => _currentLod[cz * _chunks + cx];

    /// <summary>The node drawing level <paramref name="lod"/> of chunk (<paramref name="cx"/>, <paramref name="cz"/>).</summary>
    public TerrainChunk3D GetChunkNode(int cx, int cz, int lod) => _lodNodes[(cz * _chunks + cx) * _levels + lod];

    /// <summary>The largest height difference (metres) between level <paramref name="lod"/> of a chunk and its LOD 0.</summary>
    public float GetChunkLodError(int cx, int cz, int lod) => _lodErrors[(cz * _chunks + cx) * _levels + lod];

    /// <summary>The collision shape of chunk (<paramref name="cx"/>, <paramref name="cz"/>), or null without collision.</summary>
    public ConcavePolygonShape3D? GetChunkShape(int cx, int cz) => _shapes.Length == 0 ? null : _shapes[cz * _chunks + cx];

    /// <summary>The node drawing <see cref="TerrainData.FoliageTypes"/> (null before the chunks are built).</summary>
    public TerrainFoliage3D? Foliage => _foliage;

    /// <summary>The collision body of chunk (<paramref name="cx"/>, <paramref name="cz"/>), or null without collision.</summary>
    public StaticBody3D? GetChunkBody(int cx, int cz) => _bodies.Length == 0 ? null : _bodies[cz * _chunks + cx];

    /// <summary>The water surface of chunk (<paramref name="cx"/>, <paramref name="cz"/>) (ponds, lakes), or null where it is dry.</summary>
    public MeshInstance3D? GetChunkWater(int cx, int cz) => _waterNodes.Length == 0 ? null : _waterNodes[cz * _chunks + cx];

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _world = GetWorld3D();
        _world?.Water.Register(this);
        _world?.AddTerrain(this);
        if (_built && _data is { } data)
        {
            Subscribe();
            if (_builtVersion != data.Version)
                RefreshAll();
        }
    }

    protected override void OnReady()
    {
        base.OnReady();
        if (!_built && _data is not null)
            Build();
    }

    protected override void OnExitTree()
    {
        Unsubscribe();
        _world?.Water.Unregister(this);
        _world?.RemoveTerrain(this);
        _world = null;
        ReleaseMacroGpu();
        base.OnExitTree();
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (_macroBake is { IsCompleted: true } bake)
        {
            _macroBake = null;
            if (bake.IsCompletedSuccessfully && MacroTextureEnabled)
                _macro = bake.Result;
            else if (bake.Exception is { } e)
                Log.Error($"[Terrain] '{Name}': the macro texture bake failed: {e.InnerException?.Message ?? e.Message}");
        }

        // The macro texture follows edits once they pause (a stroke edits every frame).
        if (_macroDirty && (_macroQuiet += gameTime.DeltaTime) >= 0.5f)
            RebakeMacroTexture();
        if (!_built || _levels <= 1 || GetViewport() is not { } viewport)
            return;
        Vector3 camera;
        if (viewport.CameraOverride is { } overrideCamera)
            camera = overrideCamera.Position;
        else if (viewport.ActiveCamera3D is { } active)
            camera = active.GlobalPosition;
        else
            return;
        UpdateLods(camera - GlobalPosition);
    }

    /// <summary>
    /// Shows, per chunk, the coarsest level whose error seen from <paramref name="localCamera"/> (terrain-local) is at
    /// most one pixel. Allocation-free.
    /// </summary>
    internal void UpdateLods(Vector3 localCamera)
    {
        var data = _data!;
        var chunkSize = _chunkQuads * data.VertexSpacing;
        var scale = LodPixelsPerRadian * MathF.Max(LodBias, 0.01f);
        var min = data.ChunkMin;
        var max = data.ChunkMax;
        for (var cz = 0; cz < _chunks; cz++)
            for (var cx = 0; cx < _chunks; cx++)
            {
                var c = cz * _chunks + cx;
                var dx = Gap(localCamera.X, cx * chunkSize, (cx + 1) * chunkSize);
                var dy = Gap(localCamera.Y, min[c], max[c]);
                var dz = Gap(localCamera.Z, cz * chunkSize, (cz + 1) * chunkSize);
                var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                var level = 0;
                for (var l = _levels - 1; l > 0; l--)
                {
                    if (_lodErrors[c * _levels + l] * scale <= distance)
                    {
                        level = l;
                        break;
                    }
                }

                ShowLod(c, level);
            }

        static float Gap(float v, float lo, float hi) => v < lo ? lo - v : v > hi ? v - hi : 0f;
    }

    private void ShowLod(int chunk, int level)
    {
        var current = _currentLod[chunk];
        if (current == level)
            return;
        _lodNodes[chunk * _levels + current].Visible = false;
        _lodNodes[chunk * _levels + level].Visible = true;
        _currentLod[chunk] = (byte)level;
    }

    // ── Building ───────────────────────────────────────────────────────────────

    private void Build()
    {
        var data = _data!;
        data.EnsureLoaded();
        _chunkQuads = data.ChunkQuads;
        _chunks = data.ChunksPerSide;
        _levels = data.Profile == TerrainProfile.Realistic ? LevelCount(_chunkQuads) : 1;
        var count = _chunks * _chunks;
        _levelIndices = new int[_levels][];
        for (var level = 0; level < _levels; level++)
            _levelIndices[level] = BuildIndices(_chunkQuads >> level, data.Diagonal, skirts: _levels > 1);
        _lodNodes = new TerrainChunk3D[count * _levels];
        _lodErrors = new float[count * _levels];
        _skirtDepth = new float[count];
        _currentLod = new byte[count];
        _pendingCollision = new bool[count];
        var collide = data.CollisionMode != TerrainCollisionMode.None;
        _bodies = collide ? new StaticBody3D?[count] : [];
        _shapes = collide ? new ConcavePolygonShape3D?[count] : [];
        var chunkSize = _chunkQuads * data.VertexSpacing;

        for (var cz = 0; cz < _chunks; cz++)
            for (var cx = 0; cx < _chunks; cx++)
            {
                var c = cz * _chunks + cx;
                ComputeErrors(cx, cz);
                var origin = new Vector3(cx * chunkSize, 0f, cz * chunkSize);
                for (var level = 0; level < _levels; level++)
                {
                    var n = (_chunkQuads >> level) + 1;
                    var vertices = n * n + (_levels > 1 ? 4 * n : 0);
                    var positions = new Vector3[vertices];
                    var normals = new Vector3[vertices];
                    var uvs = new Vector2[vertices];
                    FillLevel(cx, cz, level, positions, normals, uvs);
                    var mesh = new ArrayMesh();
                    mesh.AddSurface(positions, normals, uvs, _levelIndices[level]);
                    var node = new TerrainChunk3D
                    {
                        Name = string.Create(CultureInfo.InvariantCulture, $"Chunk{cx}_{cz}_Lod{level}"),
                        Terrain = this,
                        ChunkX = cx,
                        ChunkZ = cz,
                        Lod = level,
                        Mesh = mesh,
                        Position = origin,
                        CastShadows = data.CastShadows,
                        Visible = level == 0,
                    };
                    _lodNodes[c * _levels + level] = node;
                    AddChild(node);
                }

                if (!collide)
                    continue;
                var shape = new ConcavePolygonShape3D { Faces = BuildFaces(c, null) };
                var body = new StaticBody3D
                {
                    Name = string.Create(CultureInfo.InvariantCulture, $"Collision{cx}_{cz}"),
                    Position = origin,
                    CollisionLayer = data.CollisionLayer,
                    CollisionMask = data.CollisionMask,
                };
                body.AddChild(new CollisionShape3D { Name = "Shape", Shape = shape });
                _shapes[c] = shape;
                _bodies[c] = body;
                AddChild(body);
            }

        _waterNodes = new MeshInstance3D?[count];
        for (var cz = 0; cz < _chunks; cz++)
            for (var cx = 0; cx < _chunks; cx++)
                RebuildWater(cx, cz);
        UpdateWetBounds();

        _built = true;
        _builtVersion = data.Version;
        Subscribe();
        _foliage = new TerrainFoliage3D { Name = "Foliage", Terrain = this };
        AddChild(_foliage);
        if (MacroTextureEnabled && _macro is null) // not already baked by a RebakeMacroTexture before the build
            StartMacroBake();
    }

    private static int LevelCount(int chunkQuads)
    {
        var levels = 1;
        while (levels < MaxLodLevels && (chunkQuads >> levels) >= 2 && ((chunkQuads >> levels) & 1) == 0)
            levels++;
        return levels;
    }

    private void FreeChunks()
    {
        if (!_built)
            return;
        foreach (var node in _lodNodes)
            node.Free();
        foreach (var body in _bodies)
            body?.Free();
        foreach (var water in _waterNodes)
            water?.Free();
        _waterNodes = [];
        _wetBounds = Aabb.Empty;
        _foliage?.Free();
        _foliage = null;
        _lodNodes = [];
        _bodies = [];
        _shapes = [];
        _lodErrors = [];
        _skirtDepth = [];
        _currentLod = [];
        _pendingCollision = [];
        _levels = 0;
        _chunks = 0;
        _built = false;
    }

    /// <summary>
    /// The index list of a level with <paramref name="n"/> quads per side: the grid's triangles (two per quad, by the
    /// diagonal rule on the level's own grid, whose parity matches the global one because chunk origins are even), then
    /// the skirt (each edge segment twice, once per winding, so it hides cracks from either side).
    /// </summary>
    internal static int[] BuildIndices(int n, TerrainDiagonal diagonal, bool skirts)
    {
        var stride = n + 1;
        var indices = new int[n * n * 6 + (skirts ? 4 * n * 12 : 0)];
        Span<int> corners = stackalloc int[6];
        Span<int> quad = stackalloc int[4];
        var k = 0;
        for (var b = 0; b < n; b++)
            for (var a = 0; a < n; a++)
            {
                quad[0] = b * stride + a;
                quad[1] = quad[0] + 1;
                quad[2] = quad[0] + stride;
                quad[3] = quad[2] + 1;
                TerrainGrid.QuadCorners(TerrainGrid.IsDiagonalB(diagonal, a, b), corners);
                for (var t = 0; t < 6; t++)
                    indices[k++] = quad[corners[t]];
            }

        if (!skirts)
            return indices;
        var skirtBase = stride * stride;
        for (var edge = 0; edge < 4; edge++)
            for (var e = 0; e < n; e++)
            {
                var t0 = EdgeVertex(edge, e, n);
                var t1 = EdgeVertex(edge, e + 1, n);
                var s0 = skirtBase + edge * stride + e;
                var s1 = s0 + 1;
                indices[k++] = t0; indices[k++] = s0; indices[k++] = t1;
                indices[k++] = t1; indices[k++] = s0; indices[k++] = s1;
                indices[k++] = t0; indices[k++] = t1; indices[k++] = s0;
                indices[k++] = t1; indices[k++] = s1; indices[k++] = s0;
            }

        return indices;
    }

    // The grid vertex at step e along edge 0 (z = 0), 1 (z = max), 2 (x = 0) or 3 (x = max).
    private static int EdgeVertex(int edge, int e, int n) => edge switch
    {
        0 => e,
        1 => n * (n + 1) + e,
        2 => e * (n + 1),
        _ => e * (n + 1) + n,
    };

    /// <summary>Writes level <paramref name="level"/> of a chunk: grid vertices, then the skirt ring (4 edges × (n + 1)).</summary>
    private void FillLevel(int cx, int cz, int level, Span<Vector3> positions, Span<Vector3> normals, Span<Vector2> uvs)
    {
        var data = _data!;
        var bed = data.BedHeights;
        var grid = data.Grid;
        var stride = data.VerticesPerSide;
        var spacing = data.VertexSpacing;
        var inverseSize = 1f / grid.Size;
        var step = 1 << level;
        var n = _chunkQuads >> level;
        var row = n + 1;
        for (var b = 0; b <= n; b++)
            for (var a = 0; a <= n; a++)
            {
                var gi = cx * _chunkQuads + a * step;
                var gj = cz * _chunkQuads + b * step;
                var v = b * row + a;
                positions[v] = new Vector3(a * step * spacing, bed[gj * stride + gi], b * step * spacing);
                normals[v] = grid.VertexNormal(bed, gi, gj);
                uvs[v] = new Vector2(gi * spacing * inverseSize, gj * spacing * inverseSize);
            }

        if (_levels <= 1)
            return;
        var depth = new Vector3(0f, _skirtDepth[cz * _chunks + cx], 0f);
        var last = _chunks - 1;
        var skirtBase = row * row;
        for (var edge = 0; edge < 4; edge++)
        {
            // Edges on the map border have no neighbour to hide: their skirt collapses (degenerate, invisible).
            var border = edge switch { 0 => cz == 0, 1 => cz == last, 2 => cx == 0, _ => cx == last };
            var drop = border ? Vector3.Zero : depth;
            for (var e = 0; e <= n; e++)
            {
                var top = EdgeVertex(edge, e, n);
                var s = skirtBase + edge * row + e;
                positions[s] = positions[top] - drop;
                normals[s] = normals[top];
                uvs[s] = uvs[top];
            }
        }
    }

    /// <summary>Each level's largest deviation from LOD 0 over the chunk, and the skirt depth (coarsest error + one quad).</summary>
    private void ComputeErrors(int cx, int cz)
    {
        var data = _data!;
        var bed = data.BedHeights;
        var stride = data.VerticesPerSide;
        var c = cz * _chunks + cx;
        var deepest = 0f;
        for (var level = 1; level < _levels; level++)
        {
            var step = 1 << level;
            var n = _chunkQuads >> level;
            var error = 0f;
            for (var b = 0; b < n; b++)
                for (var a = 0; a < n; a++)
                {
                    var i0 = cx * _chunkQuads + a * step;
                    var j0 = cz * _chunkQuads + b * step;
                    var h00 = bed[j0 * stride + i0];
                    var h10 = bed[j0 * stride + i0 + step];
                    var h01 = bed[(j0 + step) * stride + i0];
                    var h11 = bed[(j0 + step) * stride + i0 + step];
                    var diagonalB = TerrainGrid.IsDiagonalB(data.Diagonal, a, b);
                    for (var fz = 0; fz <= step; fz++)
                        for (var fx = 0; fx <= step; fx++)
                        {
                            var coarse = TerrainGrid.Interpolate(diagonalB, h00, h10, h01, h11, (float)fx / step, (float)fz / step);
                            error = MathF.Max(error, MathF.Abs(bed[(j0 + fz) * stride + i0 + fx] - coarse));
                        }
                }

            _lodErrors[c * _levels + level] = error;
            deepest = MathF.Max(deepest, error);
        }

        _skirtDepth[c] = _levels > 1 ? deepest + data.VertexSpacing : 0f;
    }

    /// <summary>The chunk's LOD 0 grid triangles as a face list (copied from the level-0 mesh, so they match bit for bit).</summary>
    private Vector3[] BuildFaces(int chunk, Vector3[]? reuse)
    {
        var positions = _lodNodes[chunk * _levels].Mesh!.GetSurface(0).Positions;
        var indices = _levelIndices[0];
        var count = _chunkQuads * _chunkQuads * 6;
        var faces = reuse is { Length: var length } && length == count ? reuse : new Vector3[count];
        for (var k = 0; k < count; k++)
            faces[k] = positions[indices[k]];
        return faces;
    }

    private void RebuildChunk(int cx, int cz, bool collision)
    {
        var c = cz * _chunks + cx;
        RebuildWater(cx, cz);
        ComputeErrors(cx, cz);
        for (var level = 0; level < _levels; level++)
        {
            var surface = _lodNodes[c * _levels + level].Mesh!.GetSurface(0);
            FillLevel(cx, cz, level, surface.Positions, surface.Normals, surface.UVs);
            surface.NotifyChanged();
        }

        if (_shapes.Length == 0)
            return;
        if (collision)
            RebuildCollision(c);
        else
            _pendingCollision[c] = true;
    }

    private void RebuildCollision(int chunk)
    {
        _pendingCollision[chunk] = false;
        if (_shapes[chunk] is { } shape)
            shape.Faces = BuildFaces(chunk, shape.Faces);
    }

    /// <summary>Rebuilds the collision of chunks whose meshes changed while an edit recorded.</summary>
    internal void FlushPendingCollision()
    {
        for (var c = 0; c < _pendingCollision.Length; c++)
            if (_pendingCollision[c])
                RebuildCollision(c);
    }

    private void RefreshAll()
    {
        for (var cz = 0; cz < _chunks; cz++)
            for (var cx = 0; cx < _chunks; cx++)
                RebuildChunk(cx, cz, collision: true);
        UpdateWetBounds();
        _builtVersion = _data!.Version;
    }

    private void Subscribe()
    {
        if (_data is null || ReferenceEquals(_subscribed, _data) || !IsInsideTree)
            return;
        Unsubscribe();
        _data.Edited += _onEdited;
        _subscribed = _data;
    }

    private void Unsubscribe()
    {
        if (_subscribed is null)
            return;
        _subscribed.Edited -= _onEdited;
        _subscribed = null;
    }

    private void OnDataEdited(TerrainLayers layers, Rect2I rect, bool fromUndo)
    {
        var data = _data!;
        Rect2I cells, chunks;
        var perQuad = data.CellsPerQuad;
        if (layers is TerrainLayers.Height or TerrainLayers.Water)
        {
            // Quads around the touched vertices, in cells.
            var q0x = Math.Max(rect.Position.X - 1, 0);
            var q0z = Math.Max(rect.Position.Y - 1, 0);
            var q1x = Math.Min(rect.End.X, data.Quads);
            var q1z = Math.Min(rect.End.Y, data.Quads);
            cells = new Rect2I(q0x * perQuad, q0z * perQuad, Math.Max(q1x - q0x, 0) * perQuad, Math.Max(q1z - q0z, 0) * perQuad);

            // Vertex normals use the neighbours: chunks holding a vertex within one of the rectangle rebuild.
            var q = data.ChunkQuads;
            var n = data.ChunksPerSide;
            var x0 = Math.Max(rect.Position.X - 1, 0);
            var z0 = Math.Max(rect.Position.Y - 1, 0);
            var x1 = Math.Min(rect.End.X, data.VerticesPerSide - 1);   // inclusive
            var z1 = Math.Min(rect.End.Y, data.VerticesPerSide - 1);
            var cx0 = Math.Max(0, (x0 - 1) / q);
            var cz0 = Math.Max(0, (z0 - 1) / q);
            var cx1 = Math.Min(n - 1, x1 / q);
            var cz1 = Math.Min(n - 1, z1 / q);
            chunks = new Rect2I(cx0, cz0, cx1 - cx0 + 1, cz1 - cz0 + 1);
            if (_built)
            {
                var collision = _edit is not { IsActive: true };
                for (var cz = cz0; cz <= cz1; cz++)
                    for (var cx = cx0; cx <= cx1; cx++)
                        RebuildChunk(cx, cz, collision);
                if (layers == TerrainLayers.Water || _wetBounds is { IsEmpty: false })
                    UpdateWetBounds();
                _builtVersion = data.Version;
            }
        }
        else
        {
            cells = rect;
            var c = data.ChunkQuads * perQuad;
            var n = data.ChunksPerSide;
            var cx0 = Math.Min(rect.Position.X / c, n - 1);
            var cz0 = Math.Min(rect.Position.Y / c, n - 1);
            var cx1 = Math.Min(Math.Max(rect.End.X - 1, 0) / c, n - 1);
            var cz1 = Math.Min(Math.Max(rect.End.Y - 1, 0) / c, n - 1);
            chunks = new Rect2I(cx0, cz0, cx1 - cx0 + 1, cz1 - cz0 + 1);
            if (_built)
                _builtVersion = data.Version;
        }

        if (MacroTextureEnabled && _built)
        {
            _macroDirty = true;
            _macroQuiet = 0f;
        }

        Changed?.Invoke(new TerrainChange(layers, cells, chunks, fromUndo));
    }

    // ── Water (ponds and lakes from the water layer) ───────────────────────────

    /// <summary>Creates, updates or frees chunk (<paramref name="cx"/>, <paramref name="cz"/>)'s water surface.</summary>
    private void RebuildWater(int cx, int cz)
    {
        var c = cz * _chunks + cx;
        var surface = TerrainWaterMesh.Build(_data!, cx, cz);
        var node = _waterNodes[c];
        if (surface is null)
        {
            node?.Free();
            _waterNodes[c] = null;
            return;
        }

        var mesh = new ArrayMesh();
        mesh.AddSurface(surface);
        if (node is null)
        {
            var chunkSize = _chunkQuads * _data!.VertexSpacing;
            node = new MeshInstance3D
            {
                Name = string.Create(CultureInfo.InvariantCulture, $"Water{cx}_{cz}"),
                Position = new Vector3(cx * chunkSize, 0f, cz * chunkSize),
                MaterialOverride = WaterMaterial ?? DefaultWaterMaterial,
                CastShadows = false,
            };
            _waterNodes[c] = node;
            AddChild(node); // unowned: generated, never saved
        }

        node.Mesh = mesh;
    }

    /// <summary>The terrain-local box of every wet vertex, bed to surface (empty when the map is dry).</summary>
    private void UpdateWetBounds()
    {
        var data = _data!;
        var water = data.WaterPixels;
        var heights = data.Heights;
        var bed = data.BedHeights;
        var stride = data.VerticesPerSide;
        var spacing = data.VertexSpacing;
        var bounds = Aabb.Empty;
        for (var j = 0; j < stride; j++)
            for (var i = 0; i < stride; i++)
            {
                var k = j * stride + i;
                if (water[k * 4] == 0)
                    continue;
                bounds = bounds.Encapsulate(new Vector3(i * spacing, heights[k], j * spacing));
                bounds = bounds.Encapsulate(new Vector3(i * spacing, bed[k], j * spacing));
            }

        // Wet triangles reach one quad beyond their wet vertices.
        _wetBounds = bounds.IsEmpty ? bounds : new Aabb(bounds.Min - new Vector3(spacing, 0f, spacing), bounds.Max + new Vector3(spacing, 0f, spacing));
    }

    /// <inheritdoc />
    public Aabb WaterBounds
    {
        get
        {
            if (_wetBounds.IsEmpty)
                return _wetBounds;
            var o = GlobalPosition;
            return new Aabb(_wetBounds.Min + o, _wetBounds.Max + o);
        }
    }

    /// <inheritdoc />
    /// <remarks>Over a wet point of the map: the surface is <see cref="HeightAt"/> + <see cref="WaterDepthAt"/>; still water.</remarks>
    public bool TrySample(Vector3 position, out WaterSample sample)
    {
        sample = default;
        if (_data is null || !Contains(position.X, position.Z))
            return false;
        var depth = WaterDepthAt(position.X, position.Z);
        if (!(depth > 0f))
            return false;
        sample = new WaterSample(HeightAt(position.X, position.Z) + depth, depth, Vector3.Zero);
        return true;
    }

    void ISceneSaveHook.OnSceneSaving(string scenePath)
    {
        if (_data is not { } data)
            return;
        if (!data.IsExternal)
        {
            var folder = TerrainData.DefaultFolderFor(scenePath);
            data.SaveLayers(AssetDatabase.Current.ToAbsolutePath(folder));
            ResourceSaver.Save(data, folder + "/" + TerrainData.ResourceFileName);
            data.LayerFolder = null; // follow the resource path from now on
            return;
        }

        data.SaveLayers();
        ResourceSaver.Save(data, data.ResourcePath!);
    }

    // ── Queries ────────────────────────────────────────────────────────────────

    private TerrainData RequireData() => _data ?? throw new InvalidOperationException("The terrain has no data.");

    /// <summary>True when world point (<paramref name="x"/>, <paramref name="z"/>) is over the map.</summary>
    public bool Contains(float x, float z)
    {
        if (_data is not { } data)
            return false;
        var o = GlobalPosition;
        var size = data.Grid.Size;
        x -= o.X;
        z -= o.Z;
        return x >= 0f && z >= 0f && x <= size && z <= size;
    }

    /// <summary>The ground height (world Y) at world (<paramref name="x"/>, <paramref name="z"/>): exact on the drawn LOD 0 triangle.</summary>
    public float HeightAt(float x, float z)
    {
        var data = RequireData();
        var o = GlobalPosition;
        return o.Y + data.Grid.HeightAt(data.BedHeights, x - o.X, z - o.Z);
    }

    /// <summary>The face normal of the LOD 0 triangle under world (<paramref name="x"/>, <paramref name="z"/>): what physics sees.</summary>
    public Vector3 NormalAt(float x, float z)
    {
        var data = RequireData();
        var o = GlobalPosition;
        return data.Grid.FaceNormalAt(data.BedHeights, x - o.X, z - o.Z);
    }

    /// <summary>The interpolated vertex normal at world (<paramref name="x"/>, <paramref name="z"/>): what the mesh shades with.</summary>
    public Vector3 SmoothNormalAt(float x, float z)
    {
        var data = RequireData();
        var o = GlobalPosition;
        return data.Grid.SmoothNormalAt(data.BedHeights, x - o.X, z - o.Z);
    }

    /// <summary>
    /// The strongest splat layer (Realistic) or surface id (Faceted) of the cell under world (<paramref name="x"/>,
    /// <paramref name="z"/>); <see cref="WaterSurface"/> where there is water unless <paramref name="ignoreWater"/>.
    /// </summary>
    public int SurfaceAt(float x, float z, bool ignoreWater = false)
    {
        var data = RequireData();
        if (!ignoreWater && WaterDepthAt(x, z) > 0f)
            return WaterSurface;
        Cell(data, x, z, out var u, out var v);
        return data.GetSurface(u, v);
    }

    /// <summary>
    /// The <see cref="TerrainLayer.Tag"/> of the strongest layer under world (<paramref name="x"/>, <paramref name="z"/>)
    /// when <see cref="Material"/> is a <see cref="TerrainSplatMaterial3D"/> (footsteps, friction); null where there is
    /// water (unless <paramref name="ignoreWater"/>), without a splat material, or past its layers.
    /// </summary>
    public string? SurfaceTagAt(float x, float z, bool ignoreWater = false)
    {
        if (Material is not TerrainSplatMaterial3D splat || RequireData().Profile != TerrainProfile.Realistic)
            return null;
        var surface = SurfaceAt(x, z, ignoreWater);
        return surface < splat.LayerCount ? splat.Layers[surface]?.Tag : null;
    }

    /// <summary>The weight (0..1) of splat layer <paramref name="layer"/> in the cell under world (<paramref name="x"/>, <paramref name="z"/>).</summary>
    public float LayerWeightAt(int layer, float x, float z)
    {
        var data = RequireData();
        Cell(data, x, z, out var u, out var v);
        return data.GetLayerWeight(layer, u, v);
    }

    /// <summary>User channel <paramref name="channel"/> (0..3) of the cell under world (<paramref name="x"/>, <paramref name="z"/>).</summary>
    public byte UserChannelAt(int channel, float x, float z)
    {
        var data = RequireData();
        Cell(data, x, z, out var u, out var v);
        return data.GetUserChannel(channel, u, v);
    }

    /// <summary>
    /// Water depth in metres above the bed at world (<paramref name="x"/>, <paramref name="z"/>), interpolated on the
    /// same triangle as <see cref="HeightAt"/> (so HeightAt + WaterDepthAt is the water surface); 0 when dry.
    /// </summary>
    public float WaterDepthAt(float x, float z)
    {
        var data = RequireData();
        var o = GlobalPosition;
        var grid = data.Grid;
        grid.Locate(x - o.X, z - o.Z, out var i, out var j, out var fx, out var fz);
        var water = data.WaterPixels;
        var stride = grid.Stride;
        var k = j * stride + i;
        var f = TerrainGrid.Interpolate(grid.IsDiagonalB(i, j), water[k * 4], water[(k + 1) * 4], water[(k + stride) * 4],
            water[(k + stride + 1) * 4], fx, fz);
        return f <= 0f ? 0f : f / 255f * data.MaxWaterDepth;
    }

    /// <summary>
    /// Casts a ray (world space) against the ground, analytically on the full-resolution triangles, from either side.
    /// </summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out TerrainHit hit)
    {
        hit = default;
        if (_data is not { } data)
            return false;
        var o = GlobalPosition;
        var local = origin - o;
        if (!data.Grid.Raycast(data.BedHeights, data.ChunkMin, data.ChunkMax, data.ChunkQuads,
                data.HeightMin - data.MaxWaterDepth, data.HeightMax, local, direction, maxDistance, out var distance, out var normal))
            return false;
        var dir = Vector3.Normalize(direction);
        hit = new TerrainHit(origin + dir * distance, normal, distance);
        return true;
    }

    private void Cell(TerrainData data, float x, float z, out int u, out int v)
    {
        var o = GlobalPosition;
        var cell = data.CellSize;
        var size = data.CellsPerSide;
        var gx = (x - o.X) / cell;
        var gz = (z - o.Z) / cell;
        u = float.IsNaN(gx) ? 0 : (int)Math.Clamp(gx, 0f, size - 1);
        v = float.IsNaN(gz) ? 0 : (int)Math.Clamp(gz, 0f, size - 1);
    }

    // ── Edits (forwarded to the data: every edit rebuilds the touched chunks and raises Changed) ──

    /// <inheritdoc cref="TerrainData.GetHeights"/>
    public void GetHeights(Rect2I vertices, Span<float> destination) => RequireData().GetHeights(vertices, destination);

    /// <inheritdoc cref="TerrainData.SetHeights"/>
    public void SetHeights(Rect2I vertices, ReadOnlySpan<float> heights) => RequireData().SetHeights(vertices, heights);

    /// <inheritdoc cref="TerrainData.SetHeightsFrom"/>
    public void SetHeightsFrom(Func<float, float, float> height) => RequireData().SetHeightsFrom(height);

    /// <inheritdoc cref="TerrainData.SetWeights"/>
    public void SetWeights(Rect2I cells, ReadOnlySpan<float> weights) => RequireData().SetWeights(cells, weights);

    /// <inheritdoc cref="TerrainData.SetWeightsFrom"/>
    public void SetWeightsFrom(TerrainWeightGenerator weights) => RequireData().SetWeightsFrom(weights);

    /// <inheritdoc cref="TerrainData.SetWaterDepth"/>
    public void SetWaterDepth(Rect2I vertices, ReadOnlySpan<float> depthMeters) => RequireData().SetWaterDepth(vertices, depthMeters);

    /// <inheritdoc cref="TerrainData.SetWaterDepthFrom"/>
    public void SetWaterDepthFrom(Func<float, float, float> depth) => RequireData().SetWaterDepthFrom(depth);

    /// <inheritdoc cref="TerrainData.GetCells"/>
    public void GetCells(TerrainLayers layer, int image, Rect2I rect, Span<uint> destination) =>
        RequireData().GetCells(layer, image, rect, destination);

    /// <inheritdoc cref="TerrainData.SetCells"/>
    public void SetCells(TerrainLayers layer, int image, Rect2I rect, ReadOnlySpan<uint> source) =>
        RequireData().SetCells(layer, image, rect, source);

    /// <summary>
    /// Paints splat layer (Realistic) or surface id (Faceted) <paramref name="surface"/> over the cells whose centre is
    /// within <paramref name="radius"/> of world <paramref name="centerXZ"/> (see <see cref="TerrainData.PaintSurface"/>).
    /// </summary>
    public void PaintSurface(Vector2 centerXZ, float radius, int surface, float strength = 1f)
    {
        var o = GlobalPosition;
        RequireData().PaintSurface(centerXZ - new Vector2(o.X, o.Z), radius, surface, strength);
    }
}
