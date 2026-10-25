using System.Numerics;

namespace MainframeEngine.Tests.Nodes;

/// <summary>
/// <see cref="SpineNode.ShadowAlphaThreshold"/> and <see cref="SpineNode.Billboard"/> (ADR 0204) without a GPU: the
/// threshold and tint alpha the shadow casters read from the vertices, the billboarded pose per view and for the
/// shadow passes, and serialization.
/// </summary>
public sealed class SpineNodeShadowBillboardTests
{
    private static readonly string SpineBoy = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "Spine", "SpineBoy");

    private static SpineNode CreateNode() => new(new SpineNodeTests.HeadlessRenderer(), new SpineFolder(SpineBoy));

    private static GameTime Frame(float dt) => new() { DeltaTime = dt, FrameCount = 1 };

    private static Vector3 Row(in Matrix4x4 m, int row) => row switch
    {
        0 => new Vector3(m.M11, m.M12, m.M13),
        1 => new Vector3(m.M21, m.M22, m.M23),
        2 => new Vector3(m.M31, m.M32, m.M33),
        _ => new Vector3(m.M41, m.M42, m.M43),
    };

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    private sealed class FixedCamera(Vector3 position, Vector3 target) : ICamera
    {
        public Vector3 Position { get; set; } = position;
        public Vector3 Forward { get; set; } = Vector3.Normalize(target - position);
        public Vector3 Up { get; set; } = Vector3.UnitY;
        public Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Position, Position + Forward, Up);
        public Matrix4x4 ProjectionMatrix => Matrix4x4.Identity;
    }

    [Fact]
    public void ShadowAlphaThresholdDefaultsToOneHalfAndBillboardToDisabled()
    {
        var node = new SpineNode();
        Assert.Equal(0.5f, node.ShadowAlphaThreshold);
        Assert.Equal(SpineNode.DefaultShadowAlphaThreshold, node.ShadowAlphaThreshold);
        Assert.Equal(BillboardMode.Disabled, node.Billboard);
        node.Free();
    }

    [Theory]
    [InlineData(0.5f, 0.5f)]
    [InlineData(0f, 0f)] // today's quad shadows: nothing is discarded
    [InlineData(0.8f, 0.8f)]
    [InlineData(-1f, 0f)]
    [InlineData(float.NaN, 0f)]
    public void TheThresholdTravelsWithEveryVertex(float threshold, float expected)
    {
        using var node = CreateNode();
        node.ShadowAlphaThreshold = threshold;
        node.Advance(Frame(1f / 60f));

        var renderer = node.SpineRenderer!;
        Assert.True(renderer.PreparedVertexCount > 0);
        for (var i = 0; i < renderer.PreparedVertexCount; i++)
            Assert.Equal(expected, renderer.PreparedShadowAlphaThreshold(i));
    }

    [Fact]
    public void TheTintAlphaFadesTheShadowTest()
    {
        // The casters discard where atlas alpha × tint alpha < threshold: a fading skeleton (or slot) fades its shadow.
        using var node = CreateNode();
        node.Advance(Frame(1f / 60f));
        Assert.Equal(1f, node.SpineRenderer!.PreparedAlpha(0));

        node.Skeleton.SetColor(1f, 1f, 1f, 0.25f);
        node.Advance(Frame(1f / 60f));
        var renderer = node.SpineRenderer!;
        for (var i = 0; i < renderer.PreparedVertexCount; i += 7)
            Assert.True(renderer.PreparedAlpha(i) <= 0.25f + 1e-6f, $"vertex {i} alpha {renderer.PreparedAlpha(i)}");
    }

    [Fact]
    public void DisabledDrawsAndCastsTheNodesOwnPose()
    {
        using var node = CreateNode();
        node.Position = new Vector3(1, 2, 3);
        node.RotationDegrees = new Vector3(0, 40, 0);
        node.Advance(Frame(1f / 60f));
        var camera = new FixedCamera(new Vector3(5, 4, 8), Vector3.Zero);

        var built = Matrix4x4.CreateScale(SpineNode.DefaultSpineScale, SpineNode.DefaultSpineScale, 1f) * node.ModelMatrix;
        Assert.Equal(built, node.ViewModel(camera.ViewMatrix));
        node.PrepareShadowCasting(camera);
        Assert.Equal(built, node.ShadowModel);
    }

    [Fact]
    public void FixedYStaysUprightAndTurnsToTheCamera()
    {
        using var node = CreateNode();
        node.Position = new Vector3(2, 0, -1);
        node.RotationDegrees = new Vector3(0, 90, 0); // edge-on to the camera unless billboarded
        node.Scale = new Vector3(0.5f);
        node.Billboard = BillboardMode.FixedY;
        node.Advance(Frame(1f / 60f));

        // Pitched down 45° (the Crash Site camera), from two yaws.
        foreach (var cameraPosition in new[] { new Vector3(2, 6, 5), new Vector3(8, 6, -1) })
        {
            var camera = new FixedCamera(cameraPosition, node.Position);
            var model = node.ViewModel(camera.ViewMatrix);
            var spineScale = SpineNode.DefaultSpineScale * 0.5f;

            AssertNear(Vector3.UnitY * spineScale, Row(model, 1)); // upright: +Y is world up
            var toCamera = cameraPosition - node.Position;
            var flat = Vector3.Normalize(new Vector3(toCamera.X, 0, toCamera.Z));
            AssertNear(flat * 0.5f, Row(model, 2)); // +Z (the skeleton's front) turned to the camera, level
            Assert.Equal(spineScale, Row(model, 0).Length(), 5);
            AssertNear(node.Position, Row(model, 3));
        }
    }

    [Fact]
    public void EnabledAlsoTiltsToTheView()
    {
        using var node = CreateNode();
        node.Billboard = BillboardMode.Enabled;
        node.Advance(Frame(1f / 60f));
        var cameraPosition = new Vector3(0, 6, 6);
        var camera = new FixedCamera(cameraPosition, Vector3.Zero);

        var model = node.ViewModel(camera.ViewMatrix);
        AssertNear(Vector3.Normalize(cameraPosition) * 1f, Row(model, 2)); // faces the camera, leaning back 45°
        var up = Vector3.Normalize(Row(model, 1));
        Assert.True(up.Y < 0.75f && up.Z < -0.6f, $"the skeleton's up {up} should lean back toward the view");
    }

    [Fact]
    public void ShadowsCastFromTheMainViewsBillboardedPose()
    {
        using var node = CreateNode();
        node.RotationDegrees = new Vector3(0, 90, 0);
        node.Billboard = BillboardMode.FixedY;
        node.Advance(Frame(1f / 60f));
        var camera = new FixedCamera(new Vector3(0, 5, 5), Vector3.Zero);

        node.PrepareShadowCasting(camera);
        Assert.Equal(node.ViewModel(camera.ViewMatrix), node.ShadowModel);

        node.PrepareShadowCasting(null); // no camera: the node's own pose
        Assert.Equal(Matrix4x4.CreateScale(SpineNode.DefaultSpineScale, SpineNode.DefaultSpineScale, 1f) * node.ModelMatrix, node.ShadowModel);
    }

    [Fact]
    public void FlipXStillMirrorsABillboardedSkeleton()
    {
        using var node = CreateNode();
        node.Billboard = BillboardMode.FixedY;
        node.Advance(Frame(1f / 60f));
        var renderer = node.SpineRenderer!;
        var count = renderer.PreparedVertexCount;
        var unflipped = new Vector3[count];
        for (var i = 0; i < count; i++)
            unflipped[i] = renderer.PreparedWorldPosition(i);
        var camera = new FixedCamera(new Vector3(3, 2, 6), Vector3.Zero);
        var before = node.ViewModel(camera.ViewMatrix);

        node.FlipX(true);
        node.Advance(Frame(0f));
        Assert.Equal(before, node.ViewModel(camera.ViewMatrix)); // the billboard basis does not change
        Assert.Equal(count, renderer.PreparedVertexCount);
        for (var i = 0; i < count; i += 11)
        {
            var flipped = renderer.PreparedWorldPosition(i); // the skeleton is mirrored about its root (x = 0)
            Assert.Equal(-unflipped[i].X, flipped.X, 3);
            Assert.Equal(unflipped[i].Y, flipped.Y, 3);
        }
    }

    [Fact]
    public void PropertiesRoundTripThroughScenes()
    {
        var node = new SpineNode
        {
            Name = "Crew",
            Folder = "Assets/Models/Spine/SpineBoy",
            ShadowAlphaThreshold = 0.25f,
            Billboard = BillboardMode.FixedY,
        };
        var json = SceneSaver.ToJson(node);
        var copy = (SpineNode)PackedScene.Parse(json).Instantiate();
        Assert.Equal(0.25f, copy.ShadowAlphaThreshold);
        Assert.Equal(BillboardMode.FixedY, copy.Billboard);
        node.Free();
        copy.Free();
    }

    [Fact]
    public void DefaultsWriteNothingSoOlderScenesLoadUnchanged()
    {
        var node = new SpineNode { Name = "SpineBoy", Folder = "Assets/Models/Spine/SpineBoy", Animation = "walk" };
        var json = SceneSaver.ToJson(node);
        var text = System.Text.Encoding.UTF8.GetString(json);
        Assert.DoesNotContain("ShadowAlphaThreshold", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Billboard", text, StringComparison.Ordinal);

        // A scene saved before ADR 0204 (no new members) loads with the defaults.
        var copy = (SpineNode)PackedScene.Parse(json).Instantiate();
        Assert.Equal(SpineNode.DefaultShadowAlphaThreshold, copy.ShadowAlphaThreshold);
        Assert.Equal(BillboardMode.Disabled, copy.Billboard);
        Assert.Equal("walk", copy.Animation);
        node.Free();
        copy.Free();
    }
}
