using System.Globalization;
using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// PCSS (ADR 0167): a top-down camera over a white floor. A plank 6.5 m up (above the camera, out of view) and a thin wall
/// 1 m tall standing on the floor cast shadows side by side; with the sun's <see cref="DirectionalLight3D.LightAngularDistance"/>
/// at 1.5° the plank's penumbra is several times the wall's. <c>--count 1</c>: angle 0 (the fixed Poisson filter).
/// </summary>
public sealed class ShadowPcssScene(HostOptions host) : ShadowSceneBase(host)
{
    /// <summary>Where, as a fraction of the image width, the plank's shadow (left) ends and the wall's (right) begins.</summary>
    public const float Split = 0.39f;

    protected override bool HasSky => false;

    protected override void Build(Node3D scene)
    {
        // Right of the wall, so the wall's top projects to its left and its shadow (on its right) stays in view.
        Camera.Position = new Vector3(1.6f, 3f, 0f);
        Camera.RotationDegrees = new Vector3(-90f, 0f, 0f); // looks down -Y; screen right = +X
        Camera.Fov = 75f;
        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(30, 30) }, MaterialOverride = Plain(225, 225, 225, 0f) });
        scene.AddChild(new MeshInstance3D
        {
            Name = "Plank",
            Position = new Vector3(-2.6f, 6.5f, 0f),
            Mesh = new BoxMesh { Size = new Vector3(1f, 0.1f, 8f) },
            MaterialOverride = Plain(120, 120, 120),
        });
        scene.AddChild(new MeshInstance3D
        {
            Name = "Wall",
            Position = new Vector3(1f, 0.5f, 0f),
            Mesh = new BoxMesh { Size = new Vector3(0.08f, 1f, 8f) },
            // Unshaded: every side reads blue, so the measurement can skip the wall.
            MaterialOverride = new StandardMaterial3D { AlbedoColor = System.Drawing.Color.FromArgb(255, 60, 90, 220), ShadingMode = ShadingMode.Unshaded },
        });

        var sun = Sun(new Vector3(0.35f, -1f, 0f));
        sun.ShadowCascades = 1;
        sun.ShadowMaxDistance = 8f;
        sun.ShadowResolution = 2048;
        sun.LightAngularDistance = Host.Count == 1 ? 0f : 1.5f;
        scene.AddChild(sun);
    }
}

/// <summary>
/// Contact shadows (ADR 0167): pebbles that cast no shadow-map shadow (<see cref="VisualInstance3D.CastShadows"/> off) on a
/// floor under a low sun, seen close up; the sun's contact shadows darken the floor beside them. <c>--count 1</c> turns the
/// light's contact shadows off.
/// </summary>
public sealed class ContactShadowsScene(HostOptions host) : ShadowSceneBase(host)
{
    protected override bool HasSky => false;

    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(0f, 0.55f, 0.75f);
        Camera.LookAt(new Vector3(0f, 0f, 0f));
        Environment.AmbientColor = new Vector3(0.25f, 0.25f, 0.28f);
        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(8, 8) }, MaterialOverride = Plain(215, 210, 200, 0f) });

        var pebble = new SphereMesh { Radius = 0.035f, Height = 0.05f };
        var stone = Plain(150, 140, 130);
        var positions = new[] { new Vector3(-0.25f, 0.02f, 0.05f), new Vector3(-0.05f, 0.02f, -0.1f), new Vector3(0.15f, 0.02f, 0.1f), new Vector3(0.3f, 0.02f, -0.05f) };
        for (var i = 0; i < positions.Length; i++)
        {
            scene.AddChild(new MeshInstance3D
            {
                Name = string.Create(CultureInfo.InvariantCulture, $"Pebble{i}"),
                Position = positions[i],
                Mesh = pebble,
                MaterialOverride = stone,
                CastShadows = false, // only the contact shadows can shadow the floor next to them
            });
        }

        scene.AddChild(new MeshInstance3D
        {
            Name = "Stick",
            Position = new Vector3(0.05f, 0.012f, 0.25f),
            RotationDegrees = new Vector3(0f, 20f, 0f),
            Mesh = new BoxMesh { Size = new Vector3(0.4f, 0.024f, 0.024f) },
            MaterialOverride = Plain(140, 100, 60),
            CastShadows = false,
        });

        var sun = Sun(new Vector3(-0.7f, -0.45f, -0.55f));
        sun.ContactShadows = Host.Count != 1;
        sun.ContactShadowLength = 0.3f;
        scene.AddChild(sun);
    }
}

/// <summary>
/// Staggered cascades (ADR 0167): the <c>csm</c> floor of receding posts under a low sun, four cascades. <c>--count</c>:
/// 0 staggered, still camera; 1 every frame (<see cref="ShadowCacheMode.Off"/>), still; 2 staggered, the camera walking
/// along the posts at 8 m/s; 3 every frame, walking; 4 staggered and walking with every G8e.2 feature on (PCSS, contact
/// shadows, coarse cascades, the far shadow) for the allocation gate. Self-checks the schedule every frame: with a
/// staggered cache at most two cascades render and the others are reused; without, all four render.
/// </summary>
public sealed class ShadowStaggeredScene(HostOptions host) : ShadowSceneBase(host)
{
    public const float WalkSpeed = 8f;
    private DirectionalLight3D _sun = null!;
    private int _framesChecked, _framesWithCache;

    private bool Staggered => Host.Count is 0 or 2 or 4;

    private bool Walking => Host.Count >= 2;

    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(0, 2.2f, 4f);
        Camera.LookAt(new Vector3(0, 0.6f, -14f));
        scene.AddChild(new MeshInstance3D
        {
            Name = "Floor",
            Position = new Vector3(0, 0, -100),
            Mesh = new PlaneMesh { Size = new Vector2(16, 220) },
            MaterialOverride = Plain(205, 200, 190),
        });

        var post = new BoxMesh { Size = new Vector3(0.25f, 2.4f, 0.25f) };
        var wood = Plain(200, 120, 70);
        for (var i = 0; i < 34; i++)
        {
            var z = 2f - i * 6f;
            foreach (var x in new[] { -3.2f, 3.2f })
            {
                scene.AddChild(new MeshInstance3D
                {
                    Name = string.Create(CultureInfo.InvariantCulture, $"Post{i}{(x < 0 ? "L" : "R")}"),
                    Position = new Vector3(x, 1.2f, z),
                    Mesh = post,
                    MaterialOverride = wood,
                    // --count 4: the near posts are fine casters, a coarse copy casts into the far cascades and the far shadow.
                    ShadowCasterLod = Host.Count == 4 ? ShadowCasterLod.Fine : ShadowCasterLod.All,
                });
                if (Host.Count == 4)
                {
                    scene.AddChild(new MeshInstance3D
                    {
                        Name = string.Create(CultureInfo.InvariantCulture, $"Coarse{i}{(x < 0 ? "L" : "R")}"),
                        Position = new Vector3(x, 1.2f, z),
                        Mesh = post,
                        MaterialOverride = wood,
                        ShadowCasterLod = ShadowCasterLod.Coarse,
                        VisibilityRangeEnd = 0.001f, // never drawn in the view: a shadow stand-in only
                    });
                }
            }
        }

        _sun = Sun(new Vector3(0.85f, -0.75f, 0.2f));
        _sun.ShadowCacheMode = Staggered ? ShadowCacheMode.Staggered : ShadowCacheMode.Off;
        if (Host.Count == 4)
        {
            _sun.LightAngularDistance = 0.5f;
            _sun.ContactShadows = true;
            _sun.ShadowCoarseCascades = 2;
            _sun.FarShadowEnabled = true;
        }

        scene.AddChild(_sun);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (Walking)
            Camera.Position += new Vector3(0f, 0f, -WalkSpeed / 60f); // fixed 60 Hz steps: the same path every run
    }

    protected override void OnRenderMainPass(in GameTime gameTime)
    {
        base.OnRenderMainPass(gameTime);
        if (gameTime.FrameCount < 3 || Shadows is not { } shadows || Host.AllocationMeasuredFrames > 0 && gameTime.FrameCount > 40)
            return;
        _framesChecked++;
        var rendered = shadows.RenderedCascades;
        var cached = shadows.CachedCascades;
        if (rendered + cached != 4)
            Fail(string.Create(CultureInfo.InvariantCulture, $"frame {gameTime.FrameCount}: {rendered} rendered + {cached} cached cascades, expected 4"));
        if (Staggered && rendered > 2)
            Fail(string.Create(CultureInfo.InvariantCulture, $"frame {gameTime.FrameCount}: {rendered} cascades rendered with a staggered cache (at most 2)"));
        if (!Staggered && rendered != 4)
            Fail(string.Create(CultureInfo.InvariantCulture, $"frame {gameTime.FrameCount}: {rendered} cascades rendered without a cache (expected 4)"));
        if (cached > 0)
            _framesWithCache++;
    }

    protected override void DisposeScene()
    {
        if (_framesChecked == 0)
            Fail("the cascade schedule was never checked");
        else if (Staggered && _framesWithCache < _framesChecked / 2)
            Fail(string.Create(CultureInfo.InvariantCulture, $"only {_framesWithCache} of {_framesChecked} frames reused a cascade"));
        base.DisposeScene();
    }
}

/// <summary>
/// The far shadow (ADR 0167): a ridge 200 m away, behind which a low sun sets, shadows a wide plain well past the last
/// cascade (40 m). The visible ridge is a fine caster; an invisible coarse copy (outside its visibility range) is what
/// casts into the far shadow, which renders once and is then reused. <c>--count 1</c>: no far shadow.
/// </summary>
public sealed class FarShadowScene(HostOptions host) : ShadowSceneBase(host)
{
    private int _farRenders;

    protected override void Build(Node3D scene)
    {
        // Looking down the plain from 30 m: the ground 100–200 m away fills the upper half of the frame.
        Camera.Position = new Vector3(0, 30f, 0);
        Camera.LookAt(new Vector3(0, 0f, -110f));
        Camera.Far = 1000f;
        scene.AddChild(new MeshInstance3D { Name = "Plain", Position = new Vector3(0, 0, -150), Mesh = new PlaneMesh { Size = new Vector2(400, 400) }, MaterialOverride = Plain(190, 200, 160, 0f) });
        scene.AddChild(new MeshInstance3D { Name = "Post", Position = new Vector3(1.5f, 1f, -20f), Mesh = new BoxMesh { Size = new Vector3(0.6f, 2f, 0.6f) }, MaterialOverride = Plain(200, 120, 70) });

        // The sun sets behind the ridge: a 15 m ridge throws a 100 m shadow towards the camera.
        var ridge = new BoxMesh { Size = new Vector3(260f, 15f, 12f) };
        scene.AddChild(new MeshInstance3D
        {
            Name = "Ridge",
            Position = new Vector3(0f, 7.5f, -225f),
            Mesh = ridge,
            MaterialOverride = Plain(120, 110, 100),
            ShadowCasterLod = ShadowCasterLod.Fine,
        });
        scene.AddChild(new MeshInstance3D
        {
            Name = "RidgeCaster",
            Position = new Vector3(0f, 7.5f, -225f),
            Mesh = ridge,
            MaterialOverride = Plain(120, 110, 100),
            ShadowCasterLod = ShadowCasterLod.Coarse,
            VisibilityRangeEnd = 0.001f, // never drawn in the view
        });

        var sun = Sun(new Vector3(0f, -0.15f, 1f));
        sun.ShadowMaxDistance = 40f;
        sun.ShadowCoarseCascades = 1;
        sun.FarShadowEnabled = Host.Count != 1;
        scene.AddChild(sun);
    }

    protected override void OnRenderMainPass(in GameTime gameTime)
    {
        base.OnRenderMainPass(gameTime);
        if (Shadows is { FarShadowRendered: true })
            _farRenders++;
    }

    protected override void DisposeScene()
    {
        // Static casters, a still camera and a still sun: the far shadow renders once and is reused.
        var expected = Host.Count == 1 ? 0 : 1;
        if (_farRenders != expected)
            Fail(string.Create(CultureInfo.InvariantCulture, $"the far shadow rendered {_farRenders} times (expected {expected})"));
        base.DisposeScene();
    }
}
