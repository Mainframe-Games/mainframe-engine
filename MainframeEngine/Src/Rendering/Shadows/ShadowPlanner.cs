using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The CPU half of the shadow system (no GPU state, so it is unit-tested and benchmarked on its own). Once per frame
/// <see cref="Plan"/> decides which lights cast shadows into which maps, fits the cascades of the primary
/// directional light to the camera, packs the atlas tiles of spot and secondary directional lights, and produces the
/// frame's <see cref="ShadowPass"/>es and the shader-side <see cref="ShadowUniforms"/>. Allocation-free.
/// </summary>
internal sealed class ShadowPlanner
{
    /// <summary>Shadow sub-passes per frame: 4 cascades + the far shadow + 11 atlas tiles + 4 cubes × 6 faces (40).</summary>
    public const int MaxPasses = ShaderLimits.MaxShadowCascades + 1 + ShaderLimits.MaxShadowAtlasMaps + ShaderLimits.MaxShadowPoint * 6;

    /// <summary>The far shadow's layer of the cascade array: after the cascades (ADR 0167).</summary>
    public const int FarShadowLayer = ShaderLimits.MaxShadowCascades;

    /// <summary>Largest PCSS penumbra radius in texels (<see cref="ShadowFilter.Pcss"/>; also the blocker search radius).</summary>
    public const float MaxPenumbraTexels = 8f;

    /// <summary>Filter radius of the far shadow, in its texels: wide, it is only seen far away.</summary>
    public const float FarShadowFilterRadius = 2.5f;

    /// <summary>The far shadow re-renders when the light turns by more than this (degrees).</summary>
    public const float FarShadowTurnDegrees = 0.1f;

    public const int MinCascadeResolution = 128;
    public const int MaxCascadeResolution = 4096;
    public const int MinCubeResolution = 64;
    public const int MaxCubeResolution = 2048;
    public const int MinAtlasSize = 512;
    public const int DefaultMaxAtlasSize = 4096;
    public const int MinAtlasTile = 64;

    /// <summary>Sphere used for directional lights when there is no camera (tree-less code): the old ±20 box.</summary>
    private const float NoCameraRadius = 20f;

    private readonly ShadowPass[] _passes = new ShadowPass[MaxPasses];
    private readonly bool[] _passHasCasters = new bool[MaxPasses];

    // Atlas requests of this frame (map k = request k) and the set the current packing was made for.
    private readonly Light?[] _atlasLights = new Light?[ShaderLimits.MaxShadowAtlasMaps];
    private readonly int[] _atlasLightIndex = new int[ShaderLimits.MaxShadowAtlasMaps];
    private readonly bool[] _atlasIsDirectional = new bool[ShaderLimits.MaxShadowAtlasMaps];
    private readonly int[] _atlasRequests = new int[ShaderLimits.MaxShadowAtlasMaps];
    private int _atlasCount;
    private readonly Light?[] _packedLights = new Light?[ShaderLimits.MaxShadowAtlasMaps];
    private readonly int[] _packedRequests = new int[ShaderLimits.MaxShadowAtlasMaps];
    private int _packedCount = -1;
    private int _packedMaxAtlasSize;
    private readonly ShadowAtlasTile[] _tiles = new ShadowAtlasTile[ShaderLimits.MaxShadowAtlasMaps];
    private ShadowAtlasAllocator? _allocator;

    private readonly int[] _cubeLightIndex = new int[ShaderLimits.MaxShadowPoint];
    private int _primaryIndex = -1;

    // Staggered cascades (ADR 0167): what each cascade layer holds, and the settings it was rendered with.
    private struct CachedCascade
    {
        public bool Valid;
        public bool Enabled;
        public ShadowMapData Data;
        public Vector3 Center;
        public float Radius;
        public float DepthRange;
    }

    private readonly CachedCascade[] _cache = new CachedCascade[ShaderLimits.MaxShadowCascades];
    private readonly float[] _cacheSplits = new float[ShaderLimits.MaxShadowCascades];
    private DirectionalLight? _cacheLight;
    private Vector3 _cacheDirection;
    private int _cacheResolution, _cacheCount, _cacheLayers;
    private bool _cacheStable;
    private ulong _frame;

    // The far shadow: the box and light direction it was rendered for.
    private bool _farValid, _farHasCasters;
    private ShadowMapData _farData;
    private Aabb _farBox;
    private Vector3 _farDirection;
    private int _farResolution;
    private DirectionalLight? _farLight;

    public ShadowFilter Filter { get; set; } = ShadowFilter.Pcss;

    /// <summary>Filter kernel radius in texels (Poisson disc and cube disc; the 3×3 grid uses texel spacing × radius / 1.5).</summary>
    public float FilterRadius { get; set; } = 1.5f;

    /// <summary>Tints the main view by cascade (debug).</summary>
    public bool DebugCascades { get; set; }

    /// <summary>Snap cascade and secondary-directional matrices to the texel grid (default true; false only to compare).</summary>
    public bool StableCascades { get; set; } = true;

    /// <summary>Largest atlas the planner may use (power of two).</summary>
    public int MaxAtlasSize { get; set; } = DefaultMaxAtlasSize;

    /// <summary>Most cascades the primary directional light gets (1–4; its <see cref="DirectionalLight.CascadeCount"/> is capped to this).</summary>
    public int CascadeLimit
    {
        get;
        set => field = Math.Clamp(value, 1, ShaderLimits.MaxShadowCascades);
    } = ShaderLimits.MaxShadowCascades;

    /// <summary>
    /// Largest side of any one map — cascade layer, atlas tile or cube face (a power of two). Caps each light's
    /// <see cref="Light.ShadowResolution"/>; default <see cref="Light.MaxShadowResolution"/> (no cap).
    /// </summary>
    public int ResolutionLimit
    {
        get;
        set => field = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(value, Light.MinShadowResolution, Light.MaxShadowResolution));
    } = Light.MaxShadowResolution;

    /// <summary>Least pull-back of the cascade near plane towards the light (covers casters without bounds, such as Spine).</summary>
    public float MinCasterPullback { get; set; } = 10f;

    /// <summary>Most pull-back of the cascade near plane (depth precision).</summary>
    public float MaxCasterPullback { get; set; } = 1000f;

    /// <summary>
    /// The camera speed (units per second) a staggered cascade's sphere allows for over its interval at
    /// <see cref="ShadowCacheSchedule.AssumedFrameRate"/> (default 10): a faster camera re-renders the cascade early.
    /// </summary>
    public float CacheMaxSpeed { get; set; } = 10f;

    /// <summary>
    /// The camera turn rate (degrees per second, default 60) a staggered cascade's sphere allows for: turning swings a
    /// slice's sphere by its distance ahead of the camera, so far cascades need more room than translation alone.
    /// </summary>
    public float CacheMaxTurnRate { get; set; } = 60f;

    /// <summary>Whether contact shadows may be used (<see cref="ShadowSystem.ContactShadows"/>).</summary>
    public bool ContactShadows { get; set; } = true;

    /// <summary>Cascades that re-render this frame (all of them unless the primary light's cache is staggered).</summary>
    public int RenderedCascadeCount { get; private set; }

    /// <summary>Cascades whose layer and matrix are reused from an earlier frame this frame.</summary>
    public int CachedCascadeCount { get; private set; }

    /// <summary>True when this frame plans the far shadow (it re-renders); false while its layer is reused or there is none.</summary>
    public bool FarShadowRenders { get; private set; }

    /// <summary>True while the primary light has a far shadow (rendered this frame or reused).</summary>
    public bool HasFarShadow => _farValid;

    /// <summary>Layers the cascade array needs this frame: the cascades, plus the far shadow's when there is one.</summary>
    public int CascadeLayers { get; private set; }

    /// <summary>Forgets every cached cascade and the far shadow: they all re-render next frame (camera cuts, edits).</summary>
    public void InvalidateCache()
    {
        Array.Clear(_cache);
        _cacheLight = null;
        _farValid = false;
        _farLight = null;
    }

    /// <summary>This frame's shader data.</summary>
    public ShadowUniforms Uniforms;

    /// <summary>This frame's passes: cascades, then atlas tiles, then cube faces.</summary>
    public ReadOnlySpan<ShadowPass> Passes => _passes.AsSpan(0, PassCount);

    public int PassCount { get; private set; }

    /// <summary>Cascades this frame (0 without a shadowed directional light).</summary>
    public int CascadeCount { get; private set; }

    /// <summary>Size of each cascade layer (0 without cascades).</summary>
    public int CascadeResolution { get; private set; }

    /// <summary>Size of the atlas this frame needs (0 without atlas tiles).</summary>
    public int AtlasSize { get; private set; }

    /// <summary>Face size of each cube slot (0 = slot unused this frame).</summary>
    public int[] CubeResolutions { get; } = new int[ShaderLimits.MaxShadowPoint];

    /// <summary>Times the atlas was (re)packed (it is only re-packed when its requests change).</summary>
    public int AtlasPackCount { get; private set; }

    /// <summary>Atlas tiles that had to shrink at the last packing (the atlas was full).</summary>
    public int AtlasDegradedTiles { get; private set; }

    /// <summary>The tile of atlas map <paramref name="map"/> this frame.</summary>
    public ShadowAtlasTile AtlasTile(int map) => _tiles[map];

    /// <summary>Atlas maps this frame (secondary directional lights first, then spot lights).</summary>
    public int AtlasMapCount => _atlasCount;

    /// <summary>
    /// Plans the frame's shadows for <paramref name="lights"/> seen by <paramref name="camera"/> (null: directional
    /// lights cover a fixed sphere around the origin). <paramref name="casterBounds"/> (world space, may be empty)
    /// lets cascades pull their near plane back just far enough to include every caster.
    /// </summary>
    public void Plan(LightEnvironment lights, ICamera? camera, in Aabb casterBounds)
    {
        ArgumentNullException.ThrowIfNull(lights);
        Uniforms = default;
        PassCount = 0;
        CascadeCount = 0;
        CascadeResolution = 0;
        CascadeLayers = 0;
        RenderedCascadeCount = 0;
        CachedCascadeCount = 0;
        FarShadowRenders = false;
        _frame++;
        _primaryIndex = -1;
        Array.Clear(CubeResolutions);
        Array.Clear(_passHasCasters);
        _atlasCount = 0;

        // Directional: the first shadowed one gets the cascades, the others an atlas tile each.
        var dirs = lights.DirectionalLights;
        var numDir = Math.Min(dirs.Count, ShaderLimits.MaxDirectionalLights);
        for (var i = 0; i < numDir; i++)
        {
            var light = dirs[i];
            if (!light.CastsShadows)
                continue;
            if (_primaryIndex < 0)
            {
                _primaryIndex = i;
                continue;
            }

            if (_atlasCount < ShaderLimits.MaxShadowAtlasMaps)
                AddAtlasRequest(light, i, directional: true);
        }

        var spots = lights.SpotLights;
        var numSpot = Math.Min(Math.Min(spots.Count, ShaderLimits.MaxSpotLights), ShaderLimits.MaxShadowSpot);
        for (var i = 0; i < numSpot; i++)
        {
            if (spots[i].CastsShadows && _atlasCount < ShaderLimits.MaxShadowAtlasMaps)
                AddAtlasRequest(spots[i], i, directional: false);
        }

        PackAtlas();

        Matrix4x4 projection = default, inverseView = default;
        float near = 0f, far = 0f;
        var haveCamera = camera is not null && TryGetView(camera, out projection, out inverseView, out near, out far);

        if (_primaryIndex >= 0)
            PlanCascades(dirs[_primaryIndex], haveCamera, projection, inverseView, near, far, casterBounds);
        if (CascadeCount == 0)
            InvalidateCache(); // no cascade layers this frame: the array may be released, nothing is kept

        for (var k = 0; k < _atlasCount; k++)
            PlanAtlasMap(k, lights, haveCamera, projection, inverseView, near, far, casterBounds);

        var points = lights.PointLights;
        var numPoint = Math.Min(points.Count, ShaderLimits.MaxPointLights);
        var cube = 0;
        for (var i = 0; i < numPoint && cube < ShaderLimits.MaxShadowPoint; i++)
        {
            var light = points[i];
            if (!light.CastsShadows || light.Range <= 0f)
                continue;
            var resolution = Math.Clamp(Math.Min(light.ShadowResolutionPow2, ResolutionLimit), MinCubeResolution, MaxCubeResolution);
            CubeResolutions[cube] = resolution;
            _cubeLightIndex[cube] = i;
            Uniforms.PointCodes[i] = cube + 1;
            Uniforms.PointParams[cube] = new Vector4(ShadowMath.CubeTexelPerDistance(resolution), light.ShadowBias, light.ShadowNormalBias, 0f);
            for (var face = 0; face < 6; face++)
            {
                AddPass(new ShadowPass
                {
                    Kind = ShadowPassKind.CubeFace,
                    LightIndex = i,
                    Slot = cube,
                    Face = face,
                    ViewProjection = ShadowMath.CubeFaceMatrix(light.Position, face, light.Range),
                    Size = resolution,
                    LightPosition = light.Position,
                    LightRange = light.Range,
                });
            }

            cube++;
        }

        Uniforms.Filter = new Vector4((float)Filter, FilterRadius,
            CascadeResolution > 0 ? 1f / CascadeResolution : 0f, AtlasSize > 0 ? 1f / AtlasSize : 0f);
    }

    private static bool TryGetView(ICamera camera, out Matrix4x4 projection, out Matrix4x4 inverseView, out float near, out float far)
    {
        projection = camera.ProjectionMatrix;
        (near, far) = FrameData.ClipPlanes(projection);
        if (projection.M34 == 0f) // orthographic: the near plane may be at (or behind) the camera
            near = Math.Max(near, 1e-3f);
        var ok = Matrix4x4.Invert(camera.ViewMatrix, out inverseView) && Matrix4x4.Invert(projection, out _) &&
                 float.IsFinite(near) && float.IsFinite(far) && near > 0f && far > near;
        return ok;
    }

    private void AddAtlasRequest(Light light, int index, bool directional)
    {
        var k = _atlasCount++;
        _atlasLights[k] = light;
        _atlasLightIndex[k] = index;
        _atlasIsDirectional[k] = directional;
        // A secondary directional light gets one tile of half its resolution (the primary's resolution is per cascade,
        // four of them): a 2048 sun as fill light costs a 1024 tile.
        var resolution = Math.Min(directional ? light.ShadowResolutionPow2 / 2 : light.ShadowResolutionPow2, ResolutionLimit);
        _atlasRequests[k] = Math.Clamp(resolution, MinAtlasTile, Math.Max(MinAtlasTile, MaxAtlasSize / 2));
        if (directional)
            Uniforms.DirCodes[index] = k + 2;
        else
            Uniforms.SpotCodes[index] = k + 1;
    }

    // Re-packs only when the requests (lights and sizes) or the atlas limit change; tiles stay put otherwise.
    private void PackAtlas()
    {
        if (_atlasCount == 0)
        {
            AtlasSize = 0;
            _packedCount = 0;
            Array.Clear(_packedLights); // do not keep removed lights alive
            return;
        }

        var changed = _packedCount != _atlasCount || _packedMaxAtlasSize != MaxAtlasSize || AtlasSize == 0;
        for (var k = 0; !changed && k < _atlasCount; k++)
            changed = !ReferenceEquals(_packedLights[k], _atlasLights[k]) || _packedRequests[k] != _atlasRequests[k];
        if (!changed)
            return;

        var maxSize = Math.Max(MinAtlasSize, (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, MaxAtlasSize)));
        var requests = _atlasRequests.AsSpan(0, _atlasCount);
        var required = ShadowAtlasAllocator.RequiredSize(requests, MinAtlasTile, MinAtlasSize, maxSize);
        // Grow when needed; never shrink while tiles are in use (avoids re-creating the atlas back and forth).
        var size = Math.Min(Math.Max(required, AtlasSize), maxSize);
        if (_allocator is null || _allocator.Size != size)
            _allocator = new ShadowAtlasAllocator(size, MinAtlasTile);
        AtlasSize = size;
        AtlasDegradedTiles = _allocator.Pack(requests, _tiles);
        AtlasPackCount++;

        _packedCount = _atlasCount;
        _packedMaxAtlasSize = MaxAtlasSize;
        for (var k = 0; k < _atlasCount; k++)
        {
            _packedLights[k] = _atlasLights[k];
            _packedRequests[k] = _atlasRequests[k];
        }

        for (var k = _atlasCount; k < _packedLights.Length; k++)
            _packedLights[k] = null;
    }

    private void PlanCascades(DirectionalLight light, bool haveCamera, in Matrix4x4 projection, in Matrix4x4 inverseView,
        float near, float far, in Aabb casterBounds)
    {
        var rotation = ShadowMath.LightRotation(light.Direction);
        var resolution = Math.Clamp(Math.Min(light.ShadowResolutionPow2, ResolutionLimit), MinCascadeResolution, MaxCascadeResolution);
        Span<float> splits = stackalloc float[ShaderLimits.MaxShadowCascades];
        int count;
        float distance;
        if (haveCamera)
        {
            distance = Math.Min(far, light.MaxShadowDistance);
            if (distance <= near * 1.001f)
            {
                Uniforms.DirCodes[_primaryIndex] = 0;
                return;
            }

            count = Math.Min(light.CascadeCount, CascadeLimit);
            splits = splits[..count];
            ShadowMath.ComputeSplits(near, distance, light.CascadeSplitLambda, splits);
        }
        else
        {
            count = 1;
            distance = float.MaxValue;
            splits[0] = float.MaxValue;
            splits = splits[..1];
        }

        CascadeCount = count;
        CascadeResolution = resolution;
        var farShadow = haveCamera && light.FarShadowEnabled;
        CascadeLayers = farShadow ? FarShadowLayer + 1 : ShaderLimits.MaxShadowCascades;
        Uniforms.DirCodes[_primaryIndex] = 1;
        Uniforms.Csm = new Vector4(count, light.CascadeBlend, distance, DebugCascades ? 1f : 0f);
        if (Filter == ShadowFilter.Pcss && light.AngularDistance > 0f)
            Uniforms.Pcss = new Vector4(MathF.Tan(float.DegreesToRadians(light.AngularDistance) * 0.5f), MaxPenumbraTexels, MaxPenumbraTexels, 0f);
        if (ContactShadows && light.ContactShadows)
            Uniforms.Contact = new Vector4(1f, 0f, 0f, 0f);

        // Staggered caching: reuse a cascade's layer while its settings hold, it is not due and the camera stays inside
        // the (grown) sphere it was rendered for.
        var staggered = haveCamera && light.CacheMode == ShadowCacheMode.Staggered;
        if (staggered && !CacheMatches(light, resolution, count, CascadeLayers, splits))
        {
            Array.Clear(_cache);
            _cacheLight = light;
            _cacheDirection = light.Direction;
            _cacheResolution = resolution;
            _cacheCount = count;
            _cacheLayers = CascadeLayers;
            _cacheStable = StableCascades;
            splits.CopyTo(_cacheSplits);
        }
        else if (!staggered && _cacheLight is not null)
        {
            Array.Clear(_cache);
            _cacheLight = null;
        }

        for (var c = 0; c < count; c++)
        {
            Vector3 center;
            float radius;
            var centerDistance = 0f;
            if (haveCamera)
            {
                var (viewCenter, r) = ShadowMath.SliceSphere(projection, c == 0 ? near : splits[c - 1], splits[c]);
                center = Vector3.Transform(viewCenter, inverseView);
                radius = r;
                centerDistance = viewCenter.Length();
            }
            else
            {
                center = Vector3.Zero;
                radius = NoCameraRadius;
            }

            Uniforms.CascadeSplits[c] = splits[c];
            ref var cached = ref _cache[c];
            if (staggered)
            {
                // Reused while the slice's sphere stays inside the one rendered (which was grown by the camera's travel
                // and turn over the cascade's interval: a constant, so the texel grid stays put between renders).
                var inside = Vector3.Distance(center, cached.Center) + radius <= cached.Radius;
                if (cached.Valid && inside && !ShadowCacheSchedule.IsDue(c, count, _frame))
                {
                    var data = cached.Data;
                    data.Params = new Vector4(data.Params.X, 0f, light.ShadowBias, light.ShadowNormalBias);
                    Uniforms.Cascades[c] = data;
                    Uniforms.CascadeEnabled[c] = cached.Enabled ? 1f : 0f;
                    Uniforms.CascadeDepthRange[c] = cached.DepthRange;
                    CachedCascadeCount++;
                    continue;
                }

                var margin = ShadowCacheSchedule.Margin(c, count, CacheMaxSpeed, CacheMaxTurnRate, centerDistance);
                radius = MathF.Ceiling((radius + margin) / ShadowMath.RadiusQuantum) * ShadowMath.RadiusQuantum;
            }

            var pullback = ShadowMath.CasterPullback(rotation, casterBounds, center, radius, MinCasterPullback, MaxCasterPullback);
            var viewProjection = ShadowMath.SphereLightMatrix(rotation, center, radius, resolution, pullback, StableCascades, out var texel);
            var mapData = new ShadowMapData
            {
                ViewProjection = viewProjection,
                Rect = new Vector4(0f, 0f, 1f, 1f),
                Params = new Vector4(texel, 0f, light.ShadowBias, light.ShadowNormalBias),
            };
            Uniforms.Cascades[c] = mapData;
            Uniforms.CascadeEnabled[c] = 1f;
            // An orthographic window of half-size h = texel · resolution / 2 spans depth from −(h + pullback) to h.
            var depthRange = texel * resolution + pullback;
            Uniforms.CascadeDepthRange[c] = depthRange;
            if (staggered)
                cached = new CachedCascade { Valid = true, Enabled = true, Data = mapData, Center = center, Radius = radius, DepthRange = depthRange };
            RenderedCascadeCount++;
            AddPass(new ShadowPass
            {
                Kind = ShadowPassKind.Cascade,
                LightIndex = _primaryIndex,
                Slot = c,
                ViewProjection = viewProjection,
                Size = resolution,
                Coarse = c >= count - light.CoarseCascades,
            });
        }

        if (farShadow)
        {
            PlanFarShadow(light, rotation, resolution, inverseView.Translation, casterBounds);
        }
        else
        {
            _farValid = false;
            _farLight = null;
        }
    }

    private bool CacheMatches(DirectionalLight light, int resolution, int count, int layers, ReadOnlySpan<float> splits)
    {
        if (!ReferenceEquals(_cacheLight, light) || _cacheDirection != light.Direction || _cacheResolution != resolution ||
            _cacheCount != count || _cacheLayers != layers || _cacheStable != StableCascades)
            return false;
        for (var c = 0; c < count; c++)
            if (_cacheSplits[c] != splits[c])
                return false;
        return true;
    }

    // The far shadow: a box (every caster's bounds, or FarShadowDistance around the camera on a coarse grid) rendered
    // once along the light into the layer after the cascades; again only when the light turns, the box outgrows the one
    // rendered, the resolution changes or the cache is invalidated.
    private void PlanFarShadow(DirectionalLight light, in Matrix4x4 rotation, int resolution, Vector3 cameraPosition, in Aabb casterBounds)
    {
        Aabb box;
        if (light.FarShadowDistance > 0f)
        {
            var d = light.FarShadowDistance;
            var step = d * 0.25f; // re-centred when the camera moves a quarter of the distance
            var c = new Vector3(MathF.Round(cameraPosition.X / step) * step, MathF.Round(cameraPosition.Y / step) * step, MathF.Round(cameraPosition.Z / step) * step);
            box = new Aabb(c - new Vector3(d), c + new Vector3(d));
            if (!casterBounds.IsEmpty)
                box = new Aabb(Vector3.Max(box.Min, casterBounds.Min), Vector3.Min(box.Max, casterBounds.Max));
        }
        else
        {
            box = casterBounds;
        }

        if (box.IsEmpty)
        {
            _farValid = false;
            return;
        }

        var cosTurn = MathF.Cos(float.DegreesToRadians(FarShadowTurnDegrees));
        var turned = !(Vector3.Dot(Vector3.Normalize(_farDirection), Vector3.Normalize(light.Direction)) >= cosTurn);
        var contained = _farBox.Contains(box.Min) && _farBox.Contains(box.Max);
        var renders = !_farValid || !ReferenceEquals(_farLight, light) || turned || !contained || _farResolution != resolution;
        if (renders)
        {
            // Grown a little so casters that move inside the box (or slowly growing bounds) do not re-render it.
            var margin = Vector3.Max(box.Size * 0.02f, Vector3.One);
            _farBox = new Aabb(box.Min - margin, box.Max + margin);
            _farDirection = light.Direction;
            _farResolution = resolution;
            _farLight = light;
            var pullback = casterBounds.IsEmpty
                ? MinCasterPullback
                : Math.Clamp(casterBounds.Transform(rotation).Max.Z - _farBox.Transform(rotation).Max.Z, MinCasterPullback, MaxCasterPullback);
            var viewProjection = ShadowMath.BoxLightMatrix(rotation, _farBox, resolution, pullback, out var texel, out _);
            _farData = new ShadowMapData
            {
                ViewProjection = viewProjection,
                Rect = new Vector4(0f, 0f, 1f, 1f),
                Params = new Vector4(texel, 0f, light.ShadowBias, light.ShadowNormalBias),
            };
            _farValid = true;
            _farHasCasters = true; // until culling says otherwise
            FarShadowRenders = true;
            AddPass(new ShadowPass
            {
                Kind = ShadowPassKind.FarShadow,
                LightIndex = _primaryIndex,
                Slot = FarShadowLayer,
                ViewProjection = viewProjection,
                Size = resolution,
                Coarse = true,
            });
        }

        var farData = _farData;
        farData.Params = new Vector4(_farData.Params.X, 0f, light.ShadowBias, light.ShadowNormalBias);
        Uniforms.FarMap = farData;
        Uniforms.FarParams = _farHasCasters ? new Vector4(1f, FarShadowFilterRadius, 1f / resolution, 0f) : Vector4.Zero;
    }

    private void PlanAtlasMap(int map, LightEnvironment lights, bool haveCamera, in Matrix4x4 projection, in Matrix4x4 inverseView,
        float near, float far, in Aabb casterBounds)
    {
        var tile = _tiles[map];
        var index = _atlasLightIndex[map];
        if (tile.IsEmpty)
        {
            // The atlas was full: this light lights without a shadow.
            if (_atlasIsDirectional[map])
                Uniforms.DirCodes[index] = 0;
            else
                Uniforms.SpotCodes[index] = 0;
            return;
        }

        Matrix4x4 viewProjection;
        Vector4 parameters;
        Vector3 position = default;
        float range = 0f;
        if (_atlasIsDirectional[map])
        {
            var light = lights.DirectionalLights[index];
            var rotation = ShadowMath.LightRotation(light.Direction);
            Vector3 center;
            float radius;
            var distance = haveCamera ? Math.Min(far, light.MaxShadowDistance) : 0f;
            if (haveCamera && distance > near * 1.001f)
            {
                var (viewCenter, r) = ShadowMath.SliceSphere(projection, near, distance);
                center = Vector3.Transform(viewCenter, inverseView);
                radius = r;
            }
            else
            {
                center = Vector3.Zero;
                radius = NoCameraRadius;
            }

            var pullback = ShadowMath.CasterPullback(rotation, casterBounds, center, radius, MinCasterPullback, MaxCasterPullback);
            viewProjection = ShadowMath.SphereLightMatrix(rotation, center, radius, tile.Size, pullback, StableCascades, out var texel);
            // y < 0: orthographic, shadowed up to the view distance −y (faded over its last tenth, like the cascades).
            parameters = new Vector4(texel, haveCamera ? -Math.Max(distance, 1e-3f) : 0f, light.ShadowBias, light.ShadowNormalBias);
        }
        else
        {
            var light = lights.SpotLights[index];
            viewProjection = ShadowMath.SpotLightMatrix(light.Position, light.Direction, light.OuterConeAngle, light.Range);
            parameters = new Vector4(ShadowMath.SpotTexelPerDistance(light.OuterConeAngle, tile.Size), 1f, light.ShadowBias, light.ShadowNormalBias);
            position = light.Position;
            range = light.Range;
        }

        var inverseSize = 1f / AtlasSize;
        Uniforms.AtlasMaps[map] = new ShadowMapData
        {
            ViewProjection = viewProjection,
            Rect = new Vector4(tile.X * inverseSize, tile.Y * inverseSize, tile.Size * inverseSize, tile.Size * inverseSize),
            Params = parameters,
        };
        AddPass(new ShadowPass
        {
            Kind = ShadowPassKind.AtlasTile,
            LightIndex = index,
            Slot = map,
            ViewProjection = viewProjection,
            X = tile.X,
            Y = tile.Y,
            Size = tile.Size,
            LightPosition = position,
            LightRange = range,
        });
    }

    private void AddPass(ShadowPass pass)
    {
        var index = PassCount++;
        _passes[index] = pass with { Index = index, Frustum = new Frustum(pass.ViewProjection) };
    }

    /// <summary>
    /// Records whether pass <paramref name="pass"/> has casters (call for every pass before <see cref="ApplyCulling"/>).
    /// </summary>
    public void SetHasCasters(int pass, bool hasCasters) => _passHasCasters[pass] = hasCasters;

    /// <summary>Whether pass <paramref name="pass"/> renders (set by <see cref="SetHasCasters"/>).</summary>
    public bool HasCasters(int pass) => _passHasCasters[pass];

    /// <summary>
    /// Turns off the shadows of maps nothing casts into, so the shaders treat them as lit and the passes are skipped:
    /// an empty cascade, an empty atlas tile, a cube whose six faces are all empty (a cube with any caster renders
    /// all six faces, since every face is sampled).
    /// </summary>
    public void ApplyCulling()
    {
        Span<bool> cubeUsed = stackalloc bool[ShaderLimits.MaxShadowPoint];
        foreach (ref readonly var pass in Passes)
        {
            var has = _passHasCasters[pass.Index];
            switch (pass.Kind)
            {
                case ShadowPassKind.Cascade:
                    Uniforms.CascadeEnabled[pass.Slot] = has ? 1f : 0f;
                    _cache[pass.Slot].Enabled = has;
                    break;
                case ShadowPassKind.FarShadow:
                    _farHasCasters = has;
                    if (!has)
                        Uniforms.FarParams = Vector4.Zero;
                    break;
                case ShadowPassKind.AtlasTile when !has:
                    if (_atlasIsDirectional[pass.Slot])
                        Uniforms.DirCodes[pass.LightIndex] = 0;
                    else
                        Uniforms.SpotCodes[pass.LightIndex] = 0;
                    break;
                case ShadowPassKind.CubeFace:
                    cubeUsed[pass.Slot] |= has;
                    break;
            }
        }

        for (var c = 0; c < ShaderLimits.MaxShadowPoint; c++)
        {
            if (CubeResolutions[c] == 0)
                continue;
            if (!cubeUsed[c])
                Uniforms.PointCodes[_cubeLightIndex[c]] = 0;
            else
                for (var p = 0; p < PassCount; p++)
                    if (_passes[p].Kind == ShadowPassKind.CubeFace && _passes[p].Slot == c)
                        _passHasCasters[p] = true; // the cube is sampled in every direction: render (clear) every face
        }

        var anyCascade = Uniforms.FarParams.X != 0f;
        for (var c = 0; c < CascadeCount; c++)
            anyCascade |= Uniforms.CascadeEnabled[c] != 0f;
        if (!anyCascade && _primaryIndex >= 0)
            Uniforms.DirCodes[_primaryIndex] = 0;
    }

    /// <summary>Whether cube slot <paramref name="cube"/> renders this frame (after <see cref="ApplyCulling"/>).</summary>
    public bool CubeRenders(int cube) => CubeResolutions[cube] > 0 && Uniforms.PointCodes[_cubeLightIndex[cube]] != 0;
}
