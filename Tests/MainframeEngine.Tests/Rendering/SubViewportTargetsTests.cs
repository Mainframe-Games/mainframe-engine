using System.Runtime.CompilerServices;

namespace MainframeEngine.Tests.Rendering;

/// <summary><see cref="SubViewportTargets"/> lifetime (no GPU: the targets were never created).</summary>
public sealed class SubViewportTargetsTests
{
    [Fact]
    public void DisposedTargetsDropTheirDrawItemsNodes()
    {
        // A retired SubViewportPost keeps its targets until the deletion queue disposes it, frames later: the targets' last
        // draws must not keep the view's nodes (game code's, in the editor) alive past a code reload.
        var compositor = (SubViewportCompositor)RuntimeHelpers.GetUninitializedObject(typeof(SubViewportCompositor));
        var targets = new SubViewportTargets(null!, compositor, "view");
        var node = new MeshInstance3D();
        targets.Draws.Opaque.Add(0, new MeshDrawItem { Node = node });
        targets.Draws.Transparent.Add(0, new MeshDrawItem { Node = node });
        targets.Draws.Casters.Add(0, new ShadowCasterItem { Node = node });

        targets.Dispose();

        Assert.Equal(0, targets.Draws.Opaque.Count);
        Assert.Equal(0, targets.Draws.Transparent.Count);
        Assert.Equal(0, targets.Draws.Casters.Count);
        node.Free();
    }
}
