using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>
/// One curve-tool gesture (G13): the curve's whole point array before and after. Each apply hands the curve its own
/// copy, so later in-place edits of the curve never reach the history.
/// </summary>
public sealed class SetCurvePointsAction : IEditorAction
{
    private readonly float[] _before;
    private readonly float[] _after;

    public SetCurvePointsAction(string name, Resource curve, ExportPropertyInfo points, float[] before, float[] after)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        Curve = curve ?? throw new ArgumentNullException(nameof(curve));
        Points = points ?? throw new ArgumentNullException(nameof(points));
        _before = (float[])(before ?? throw new ArgumentNullException(nameof(before))).Clone();
        _after = (float[])(after ?? throw new ArgumentNullException(nameof(after))).Clone();
    }

    public string Name { get; }

    public Resource Curve { get; }

    public ExportPropertyInfo Points { get; }

    public void Do() => Points.SetValue(Curve, _after.Clone());

    public void Undo() => Points.SetValue(Curve, _before.Clone());

    public bool TryMerge(IEditorAction next) => false;
}
