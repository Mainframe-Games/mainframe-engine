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
    /// <summary>Shadow sub-passes per frame: 4 cascades + 11 atlas tiles + 4 cubes × 6 faces (39).</summary>
    public const int MaxPasses = ShaderLimits.MaxShadowCascades + ShaderLimits.MaxShadowAtlasMaps + ShaderLimits.MaxShadowPoint * 6;

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

    public ShadowFilter Filter { get; set; } = ShadowFilter.Poisson16;

    /// <summary>Filter kernel radius in texels (Poisson disc and cube disc; the 3×3 grid uses texel spacing × radius / 1.5).</summary>
    public float FilterRadius { get; set; } = 1.5f;

    /// <summary>Tints the main view by cascade (debug).</summary>
    public bool DebugCascades { get; set; }

    /// <summary>Snap cascade and secondary-directional matrices to the texel grid (default true; false only to compare).</summary>
    public bool StableCascades { get; set; } = true;

    /// <summary>Largest atlas the planner may use (power of two).</summary>
    public int MaxAtlasSize { get; set; } = DefaultMaxAtlasSize;

    /// <summary>Least pull-back of the cascade near plane towards the light (covers casters without bounds, such as Spine).</summary>
    public float MinCasterPullback { get; set; } = 10f;

    /// <summary>Most pull-back of the cascade near plane (depth precision).</summary>
    public float MaxCasterPullback { get; set; } = 1000f;

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
            var resolution = Math.Clamp(light.ShadowResolutionPow2, MinCubeResolution, MaxCubeResolution);
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
        var resolution = directional ? light.ShadowResolutionPow2 / 2 : light.ShadowResolutionPow2;
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
        var resolution = Math.Clamp(light.ShadowResolutionPow2, MinCascadeResolution, MaxCascadeResolution);
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

            count = light.CascadeCount;
            splits = splits[..count];
            ShadowMath.ComputeSplits(near, distance, light.CascadeSplitLambda, splits);
        }
        else
        {
            count = 1;
            distance = float.MaxValue;
            splits[0] = float.MaxValue;
        }

        CascadeCount = count;
        CascadeResolution = resolution;
        Uniforms.DirCodes[_primaryIndex] = 1;
        Uniforms.Csm = new Vector4(count, light.CascadeBlend, distance, DebugCascades ? 1f : 0f);

        for (var c = 0; c < count; c++)
        {
            Vector3 center;
            float radius;
            if (haveCamera)
            {
                var (viewCenter, r) = ShadowMath.SliceSphere(projection, c == 0 ? near : splits[c - 1], splits[c]);
                center = Vector3.Transform(viewCenter, inverseView);
                radius = r;
            }
            else
            {
                center = Vector3.Zero;
                radius = NoCameraRadius;
            }

            var pullback = ShadowMath.CasterPullback(rotation, casterBounds, center, radius, MinCasterPullback, MaxCasterPullback);
            var viewProjection = ShadowMath.SphereLightMatrix(rotation, center, radius, resolution, pullback, StableCascades, out var texel);
            Uniforms.Cascades[c] = new ShadowMapData
            {
                ViewProjection = viewProjection,
                Rect = new Vector4(0f, 0f, 1f, 1f),
                Params = new Vector4(texel, 0f, light.ShadowBias, light.ShadowNormalBias),
            };
            Uniforms.CascadeSplits[c] = splits[c];
            Uniforms.CascadeEnabled[c] = 1f;
            AddPass(new ShadowPass
            {
                Kind = ShadowPassKind.Cascade,
                LightIndex = _primaryIndex,
                Slot = c,
                ViewProjection = viewProjection,
                Size = resolution,
            });
        }
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

        var anyCascade = false;
        for (var c = 0; c < CascadeCount; c++)
            anyCascade |= Uniforms.CascadeEnabled[c] != 0f;
        if (!anyCascade && _primaryIndex >= 0)
            Uniforms.DirCodes[_primaryIndex] = 0;
    }

    /// <summary>Whether cube slot <paramref name="cube"/> renders this frame (after <see cref="ApplyCulling"/>).</summary>
    public bool CubeRenders(int cube) => CubeResolutions[cube] > 0 && Uniforms.PointCodes[_cubeLightIndex[cube]] != 0;
}
