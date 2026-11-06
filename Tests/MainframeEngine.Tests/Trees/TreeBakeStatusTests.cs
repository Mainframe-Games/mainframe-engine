using System.Numerics;
using MainframeEngine.Tests.Physics;
using MainframeEngine.Trees;

namespace MainframeEngine.Tests.Trees;

/// <summary>
/// What the tree inspector (G8b.3, ADR 0271) needs from the engine: <see cref="TreeMesh.BakeHash"/> and
/// <see cref="Tree3D.GetBakeStatus"/>, a stale bake yielding to the generator in edit mode, <see cref="Tree3D.ForceLod"/>,
/// <see cref="TreeLevel.LevelIndex"/> and <see cref="World3D.PreviewWind"/>.
/// </summary>
[Collection(nameof(Scene.SerialResources))] // presets go through the process-wide loader
public sealed class TreeBakeStatusTests : IDisposable
{
    private readonly PhysicsHarness3D _h = new();

    public void Dispose() => _h.Dispose();

    private Tree3D AddTree(TreeOptions? options = null)
    {
        options ??= TreePresets.Load("Aspen Small");
        var tree = _h.Add(new Tree3D { Name = "Tree", Options = options, Preset = "Aspen Small", Seed = 12 });
        tree.Owner = _h.Scene;
        return tree;
    }

    [Fact]
    public void TheBakeHashFollowsEveryInputButTheName()
    {
        var options = TreePresets.Load("Aspen Small");
        var hash = TreeMesh.ComputeBakeHash(options, 12, TreeStyle.Realistic);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, TreeMesh.ComputeBakeHash(options.Clone(), 12, TreeStyle.Realistic)); // equal copies
        Assert.Equal(hash, TreeMesh.ComputeBakeHash(TreePresets.Load("Aspen Small"), 12, TreeStyle.Realistic));
        Assert.NotEqual(hash, TreeMesh.ComputeBakeHash(options, 13, TreeStyle.Realistic));
        Assert.NotEqual(hash, TreeMesh.ComputeBakeHash(options, 12, TreeStyle.LowPoly));

        var renamed = options.Clone();
        renamed.ResourceName = "Mine";
        Assert.Equal(hash, TreeMesh.ComputeBakeHash(renamed, 12, TreeStyle.Realistic));

        var level = options.Clone();
        level.Level[2].Gnarliness += 0.01; // a level's value
        Assert.NotEqual(hash, TreeMesh.ComputeBakeHash(level, 12, TreeStyle.Realistic));
        var tint = options.Clone();
        tint.LeafTint = System.Drawing.Color.Red; // a material input, not a generator one
        Assert.NotEqual(hash, TreeMesh.ComputeBakeHash(tint, 12, TreeStyle.Realistic));

        Assert.Equal(TreeMesh.Generate(options, 12, TreeStyle.Realistic).BakeHash, hash);
    }

    [Fact]
    public void BakeStatusGoesFromNotBakedToUpToDateToStale()
    {
        var tree = AddTree();
        Assert.Equal(TreeBakeStatus.NotBaked, tree.GetBakeStatus());
        var baked = tree.Bake();
        Assert.Equal(TreeBakeStatus.UpToDate, tree.GetBakeStatus());
        Assert.True(tree.DrawsBake);
        Assert.NotSame(tree.Options, baked.Options); // the bake keeps a copy: later edits leave it as it was

        tree.Options!.Level[1].Length *= 0.8;
        Assert.Equal(TreeBakeStatus.Stale, tree.GetBakeStatus());
        Assert.False(baked.IsCurrentFor(tree.Options, 12, TreeStyle.Realistic));
        Assert.True(baked.IsCurrentFor(baked.Options!, 12, TreeStyle.Realistic));

        tree.Bake();
        Assert.Equal(TreeBakeStatus.UpToDate, tree.GetBakeStatus());
        tree.Seed = 13;
        Assert.Equal(TreeBakeStatus.Stale, tree.GetBakeStatus());
        tree.Seed = 12;
        tree.Style = TreeStyle.LowPoly;
        Assert.Equal(TreeBakeStatus.Stale, tree.GetBakeStatus());

        // A bake without the hash (older files) is stale; a tree with only a bake has nothing to compare with.
        var old = new TreeMesh { Lods = baked.Lods, Options = baked.Options, Seed = 12 };
        tree.Style = TreeStyle.Realistic;
        tree.BakedMesh = old;
        Assert.Equal(TreeBakeStatus.Stale, tree.GetBakeStatus());
        Assert.Equal(TreeBakeStatus.UpToDate, _h.Add(new Tree3D { Name = "Only", BakedMesh = old }).GetBakeStatus());
    }

    [Fact]
    public void AStaleBakeYieldsToTheGeneratorOnlyInEditMode()
    {
        var tree = AddTree();
        var baked = tree.Bake();
        tree.Options!.Level[0].Length *= 0.5;
        _h.Run(1);
        Assert.Same(baked, tree.Mesh); // a game draws its bake as is
        Assert.True(tree.DrawsBake);

        _h.Tree.EditMode = true;
        tree.Seed = 12; // unchanged: nothing waits
        Assert.False(tree.NeedsRegenerate);
        tree.Options!.Level[0].Length *= 0.999;
        Assert.True(tree.NeedsRegenerate);
        tree.Regenerate();
        Assert.False(tree.NeedsRegenerate);
        Assert.NotSame(baked, tree.Mesh);
        Assert.False(tree.DrawsBake);
        Assert.True(tree.Mesh!.Lods[0].Bounds.Size.Y < baked.Lods[0].Bounds.Size.Y); // the edit shows
        Assert.True(tree.LastRegenerateMilliseconds > 0);

        var current = tree.Bake();
        Assert.Same(current, tree.Mesh); // an up-to-date bake draws in the editor too
    }

    [Fact]
    public void ForceLodDrawsOneLevelAtAnyDistance()
    {
        var tree = AddTree();
        Assert.Equal(-1, tree.ForceLod);
        tree.ForceLod = 2;
        for (var l = 0; l < 3; l++)
        {
            var node = tree.GetLodNode(l);
            foreach (var distance in (float[])[0f, 10f, 50f, 500f])
                Assert.Equal(l == 2, GeometryInstance3D.IsInVisibilityRange(distance, node.VisibilityRangeBegin, node.VisibilityRangeEnd));
        }

        tree.Regenerate(); // survives a rebuild
        Assert.False(GeometryInstance3D.IsInVisibilityRange(1f, tree.GetLodNode(0).VisibilityRangeBegin, tree.GetLodNode(0).VisibilityRangeEnd));

        tree.ForceLod = -1;
        var near = tree.GetLodNode(0);
        Assert.Equal((0f, tree.Lod1Distance), (near.VisibilityRangeBegin, near.VisibilityRangeEnd));
        tree.ForceLod = -7;
        Assert.Equal(-1, tree.ForceLod);
    }

    [Fact]
    public void LevelsKnowTheirIndex()
    {
        var options = TreePresets.Load("Oak Medium");
        for (var i = 0; i < options.Level.Length; i++)
            Assert.Equal(i, options.Level[i].LevelIndex);
        Assert.Equal(3, options.Clone().Level[3].LevelIndex);
        Assert.Equal(-1, new TreeLevel().LevelIndex);
        var swapped = new[] { options.Level[1], options.Level[0] };
        options.Level = swapped;
        Assert.Equal(0, swapped[0].LevelIndex);
    }

    [Fact]
    public void PreviewWindBlowsOnlyWhileTheWorldIsCalm()
    {
        var world = new World3D();
        Assert.Equal(default, world.GetFrameEnvironment());
        world.PreviewWind = true;
        var breeze = world.GetFrameEnvironment();
        Assert.Equal(1f, breeze.Wind.W);
        Assert.True(Vector3.Distance(Vector3.Normalize(new Vector3(1f, 0f, 0.3f)), new Vector3(breeze.Wind.X, breeze.Wind.Y, breeze.Wind.Z)) < 1e-5f);
        Assert.Equal(0.5f, breeze.WindParams.X);

        var root = new Node3D { Name = "Root" };
        _h.Add(root);
        var environment = new WorldEnvironment { Name = "Env", WindStrength = 2.5f };
        root.AddChild(environment);
        var windy = _h.Tree.Root.World3D;
        windy.PreviewWind = true;
        Assert.Equal(2.5f, windy.GetFrameEnvironment().Wind.W); // the world's own wind wins
        environment.WindStrength = 0f;
        Assert.Equal(1f, windy.GetFrameEnvironment().Wind.W);
        windy.PreviewWind = false;
        Assert.Equal(0f, windy.GetFrameEnvironment().Wind.W);
    }
}
