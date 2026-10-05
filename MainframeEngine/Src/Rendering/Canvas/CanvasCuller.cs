using System.Numerics;

namespace MainframeEngine;

/// <summary>One canvas item ready to draw: its final transform (canvas pixels), modulate, z and draw state.</summary>
public readonly record struct CulledCanvasItem(
    CanvasItem Item,
    Transform2D Transform,
    Vector4 Modulate,
    int Z,
    Material? Material,
    CanvasTextureFilter Filter,
    CanvasTextureRepeat Repeat);

/// <summary>
/// Orders and culls canvas items exactly like Godot 4.7's <c>RendererCanvasCull</c> (<c>_cull_canvas_item</c>,
/// <c>_collect_ysort_children</c>, <c>_attach_canvas_item_for_draw</c>): items are attached to one list per z index
/// (−4096 … 4096) in tree order — children after their parent unless <see cref="CanvasItem.ShowBehindParent"/>,
/// y-sorted subtrees flattened and ordered by their origin's Y (ties by collection order) — and the lists are
/// concatenated from the lowest z up. Items whose drawn rect misses the clip rect are skipped (their children are not).
/// Engine-free: the canvas server feeds it, tests call it directly.
/// </summary>
public sealed class CanvasCuller
{
    public const int ZMin = -4096;
    public const int ZMax = 4096;
    private const int ZRange = ZMax - ZMin + 1;

    private readonly List<CulledCanvasItem>?[] _zLists = new List<CulledCanvasItem>?[ZRange];
    private readonly List<int> _usedZ = [];
    private readonly Stack<List<CulledCanvasItem>> _listPool = new();
    private readonly List<YSortEntry> _ysortScratch = [];
    private Rect2 _clip;
    private uint _cullMask;

    private struct YSortEntry
    {
        public CanvasItem Item;
        public Transform2D Xform;       // relative to the y-sort root
        public Vector4 Modulate;        // modulate of the y-sort ancestors between the root and the item's parent
        public int Index;               // collection order (tie-break)
        public int ParentAbsZ;          // absolute z of the item's parent
        public CanvasItem? MaterialOwner;
        public CanvasTextureFilter Filter;   // the parent's resolved filter / repeat
        public CanvasTextureRepeat Repeat;
    }

    /// <summary>
    /// Culls every root of <paramref name="canvas"/> under <paramref name="canvasTransform"/> against
    /// <paramref name="clipRect"/> (viewport pixels) and appends the draw order to <paramref name="output"/>.
    /// </summary>
    public void Cull(Canvas canvas, in Transform2D canvasTransform, Rect2 clipRect, List<CulledCanvasItem> output, uint cullMask = uint.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(output);
        var roots = canvas.SortedRoots();
        Cull(roots, canvasTransform, clipRect, output, cullMask);
    }

    /// <summary>Culls the given root items (in order) as one canvas.</summary>
    public void Cull(IReadOnlyList<CanvasItem> roots, in Transform2D canvasTransform, Rect2 clipRect, List<CulledCanvasItem> output, uint cullMask = uint.MaxValue)
    {
        _clip = clipRect;
        _cullMask = cullMask;
        for (var i = 0; i < roots.Count; i++)
        {
            var root = roots[i];
            // A top-level item is a canvas root but still hides with its node ancestors (Godot propagates visibility to it).
            if (root.TopLevel && !AncestorsVisible(root))
                continue;
            CullItem(root, canvasTransform, Vector4.One, 0, null, alreadyYSorted: false,
                CanvasTextureFilter.Linear, CanvasTextureRepeat.Disabled);
        }

        _usedZ.Sort();
        for (var i = 0; i < _usedZ.Count; i++)
        {
            var z = _usedZ[i];
            var list = _zLists[z - ZMin]!;
            output.AddRange(list);
            list.Clear();
            _listPool.Push(list);
            _zLists[z - ZMin] = null;
        }

        _usedZ.Clear();
    }

    private void CullItem(CanvasItem ci, in Transform2D parentXform, Vector4 parentModulate, int z, CanvasItem? materialOwner, bool alreadyYSorted,
        CanvasTextureFilter parentFilter, CanvasTextureRepeat parentRepeat)
    {
        if (!ci.Visible || (ci.VisibilityLayer & _cullMask) == 0)
            return;

        CanvasItem? ownMaterialOwner;
        if (ci.UseParentMaterial && materialOwner is not null)
        {
            ownMaterialOwner = materialOwner;
        }
        else
        {
            materialOwner = ci;
            ownMaterialOwner = null;
        }

        var modulate = ci.Modulate * parentModulate;
        if (modulate.W < 0.007f)
            return;

        var filter = ci.TextureFilter == CanvasTextureFilter.ParentNode ? parentFilter : ci.TextureFilter;
        var repeat = ci.TextureRepeat == CanvasTextureRepeat.ParentNode ? parentRepeat : ci.TextureRepeat;

        // Y-sorted items arrive with their final transform already computed.
        var finalXform = alreadyYSorted ? parentXform : parentXform * ci.GetTransform();

        var parentZ = z;
        z = ci.ZAsRelative ? Math.Clamp(z + ci.ZIndex, ZMin, ZMax) : ci.ZIndex;

        if (ci.YSortEnabled)
        {
            if (!alreadyYSorted)
            {
                // Flatten the y-sorted subtree (the item itself first), sort by Y, then cull each as already sorted.
                var start = _ysortScratch.Count;
                _ysortScratch.Add(new YSortEntry
                {
                    Item = ci,
                    Xform = Transform2D.Identity,
                    Modulate = Reciprocal(ci.Modulate),
                    Index = 0,
                    ParentAbsZ = parentZ,
                    MaterialOwner = materialOwner == ci ? null : materialOwner,
                    Filter = parentFilter,
                    Repeat = parentRepeat,
                });
                var index = 1;
                CollectYSortChildren(ci, Transform2D.Identity, materialOwner, Vector4.One, ref index, z, filter, repeat);

                var count = _ysortScratch.Count - start;
                _ysortScratch.Sort(start, count, YSortComparer.Instance);
                for (var i = 0; i < count; i++)
                {
                    var e = _ysortScratch[start + i];
                    CullItem(e.Item, finalXform * e.Xform, modulate * e.Modulate, e.ParentAbsZ, e.MaterialOwner, alreadyYSorted: true, e.Filter, e.Repeat);
                }

                _ysortScratch.RemoveRange(start, count);
            }
            else
            {
                Attach(ci, finalXform, modulate, z, ownMaterialOwner, filter, repeat);
            }

            return;
        }

        var children = ci.ChildList;
        var childCount = children?.Count ?? 0;
        for (var i = 0; i < childCount; i++)
            if (children![i] is CanvasItem { TopLevel: false, ShowBehindParent: true } child)
                CullItem(child, finalXform, modulate, z, materialOwner, false, filter, repeat);
        Attach(ci, finalXform, modulate, z, ownMaterialOwner, filter, repeat);
        for (var i = 0; i < childCount; i++)
            if (children![i] is CanvasItem { TopLevel: false, ShowBehindParent: false } child)
                CullItem(child, finalXform, modulate, z, materialOwner, false, filter, repeat);
    }

    private void CollectYSortChildren(CanvasItem parent, in Transform2D parentYSortXform, CanvasItem? materialOwner, Vector4 modulate, ref int index, int z,
        CanvasTextureFilter parentFilter, CanvasTextureRepeat parentRepeat)
    {
        var children = parent.ChildList;
        if (children is null)
            return;
        for (var i = 0; i < children.Count; i++)
        {
            if (children[i] is not CanvasItem { TopLevel: false } child || !child.Visible)
                continue;
            if ((child.VisibilityLayer & _cullMask) == 0)
                continue;

            var xform = parentYSortXform * child.GetTransform();
            var absZ = child.ZAsRelative ? Math.Clamp(z + child.ZIndex, ZMin, ZMax) : child.ZIndex;
            var filter = child.TextureFilter == CanvasTextureFilter.ParentNode ? parentFilter : child.TextureFilter;
            var repeat = child.TextureRepeat == CanvasTextureRepeat.ParentNode ? parentRepeat : child.TextureRepeat;
            _ysortScratch.Add(new YSortEntry
            {
                Item = child,
                Xform = xform,
                Modulate = modulate,
                Index = index++,
                ParentAbsZ = z,
                MaterialOwner = child.UseParentMaterial ? materialOwner : null,
                Filter = parentFilter,
                Repeat = parentRepeat,
            });
            if (child.YSortEnabled)
                CollectYSortChildren(child, xform, child.UseParentMaterial ? materialOwner : child, modulate * child.Modulate, ref index, absZ, filter, repeat);
        }
    }

    private void Attach(CanvasItem ci, in Transform2D xform, Vector4 modulate, int z, CanvasItem? materialOwner, CanvasTextureFilter filter, CanvasTextureRepeat repeat)
    {
        var drawList = ci.DrawList;
        if (drawList.IsEmpty)
            return;
        var globalRect = drawList.Bounds.Transformed(xform);
        if (!_clip.Intersects(globalRect, includeBorders: true))
            return;

        var zi = z - ZMin;
        var list = _zLists[zi];
        if (list is null)
        {
            list = _listPool.Count > 0 ? _listPool.Pop() : new List<CulledCanvasItem>(64);
            _zLists[zi] = list;
            _usedZ.Add(z);
        }

        var material = (materialOwner ?? ci).Material;
        list.Add(new CulledCanvasItem(ci, xform, modulate * ci.SelfModulate, z, material, filter, repeat));
    }

    private static bool AncestorsVisible(CanvasItem item)
    {
        for (var n = item.Parent; n is CanvasItem parent; n = parent.Parent)
        {
            if (!parent.Visible)
                return false;
            if (!parent.TopLevel && parent.Parent is not CanvasItem)
                break;
        }

        return true;
    }

    private static Vector4 Reciprocal(Vector4 m) =>
        new(m.X != 0 ? 1 / m.X : 0, m.Y != 0 ? 1 / m.Y : 0, m.Z != 0 ? 1 / m.Z : 0, m.W != 0 ? 1 / m.W : 0);

    // Godot's ItemYSort: by the origin's Y, ties (is_equal_approx) by collection order.
    private sealed class YSortComparer : IComparer<YSortEntry>
    {
        public static readonly YSortComparer Instance = new();

        public int Compare(YSortEntry a, YSortEntry b)
        {
            var ay = a.Xform.Origin.Y;
            var by = b.Xform.Origin.Y;
            if (IsEqualApprox(ay, by))
                return a.Index.CompareTo(b.Index);
            return ay < by ? -1 : 1;
        }

        private static bool IsEqualApprox(float a, float b)
        {
            if (a == b)
                return true;
            var tolerance = 0.00001f * MathF.Abs(a);
            if (tolerance < 0.00001f)
                tolerance = 0.00001f;
            return MathF.Abs(a - b) < tolerance;
        }
    }
}
