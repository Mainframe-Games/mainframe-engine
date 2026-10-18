namespace MainframeEngine.Audio.Music;

/// <summary>
/// The live values of a piece of music's parameters. Code sets a target; continuous parameters with a seek speed move
/// towards it in <see cref="Advance"/>, the others jump. Nothing allocates after construction: names and labels are
/// looked up in tables built here.
/// </summary>
public sealed class MusicParameterSet
{
    private readonly MusicParameterKind[] _kinds;
    private readonly float[] _min;
    private readonly float[] _max;
    private readonly float[] _defaults;
    private readonly float[] _seek;
    private readonly float[] _values;
    private readonly float[] _targets;
    private readonly string[][] _labels;
    private readonly string[] _names;
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    /// <summary>Compiles <paramref name="parameters"/>; a repeated name keeps the first (the plan reports it).</summary>
    public MusicParameterSet(IReadOnlyList<MusicParameterInfo> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var list = new List<MusicParameterInfo>(parameters.Count);
        foreach (var p in parameters)
        {
            if (p is not null && !string.IsNullOrEmpty(p.Name) && _index.TryAdd(p.Name, list.Count))
                list.Add(p);
        }

        var n = list.Count;
        (_kinds, _min, _max, _defaults, _seek) = (new MusicParameterKind[n], new float[n], new float[n], new float[n], new float[n]);
        (_values, _targets, _labels, _names) = (new float[n], new float[n], new string[n][], new string[n]);
        for (var i = 0; i < n; i++)
        {
            var p = list[i];
            _names[i] = p.Name;
            _kinds[i] = p.Kind;
            _labels[i] = p.Kind == MusicParameterKind.Labeled ? [.. p.Labels ?? []] : [];
            switch (p.Kind)
            {
                case MusicParameterKind.Labeled:
                    (_min[i], _max[i]) = (0, Math.Max(0, _labels[i].Length - 1));
                    break;
                case MusicParameterKind.Discrete:
                    (_min[i], _max[i]) = (MathF.Round(Finite(p.Min)), MathF.Round(Finite(p.Max)));
                    break;
                default:
                    (_min[i], _max[i]) = (Finite(p.Min), Finite(p.Max));
                    break;
            }

            if (_max[i] < _min[i])
                (_min[i], _max[i]) = (_max[i], _min[i]);
            _seek[i] = p.Kind == MusicParameterKind.Continuous && p.SeekSpeed > 0 && float.IsFinite(p.SeekSpeed) ? p.SeekSpeed : 0;
            _defaults[i] = Sanitize(i, p.Default);
            _values[i] = _targets[i] = _defaults[i];
        }
    }

    public int Count => _names.Length;

    /// <summary>Bumped whenever a value (not just a target) changes.</summary>
    public int Version { get; private set; }

    public string NameOf(int index) => _names[index];

    public MusicParameterKind KindOf(int index) => _kinds[index];

    public float MinOf(int index) => _min[index];

    public float MaxOf(int index) => _max[index];

    public float DefaultOf(int index) => _defaults[index];

    public float SeekSpeedOf(int index) => _seek[index];

    /// <summary>A labeled parameter's labels (empty for the other kinds).</summary>
    public IReadOnlyList<string> LabelsOf(int index) => _labels[index];

    /// <summary>The parameter's index, or −1.</summary>
    public int IndexOf(string name) => name is not null && _index.TryGetValue(name, out var i) ? i : -1;

    /// <summary>A labeled parameter's label index, or −1.</summary>
    public int LabelIndex(int parameter, string label)
    {
        var labels = _labels[parameter];
        for (var i = 0; i < labels.Length; i++)
        {
            if (string.Equals(labels[i], label, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    /// <summary>The current (smoothed) value.</summary>
    public float GetValue(int index) => _values[index];

    /// <summary>The value being sought.</summary>
    public float GetTarget(int index) => _targets[index];

    /// <summary>The label of a labeled parameter's current value ("" for the other kinds).</summary>
    public string GetLabel(int index)
    {
        var labels = _labels[index];
        var i = (int)_values[index];
        return (uint)i < (uint)labels.Length ? labels[i] : "";
    }

    /// <summary>Sets the target (clamped; discrete and labeled values round and jump); <paramref name="immediate"/> skips the seek.</summary>
    public void SetTarget(int index, float value, bool immediate = false)
    {
        if (float.IsNaN(value))
            return;
        var target = Sanitize(index, value);
        _targets[index] = target;
        if ((immediate || _seek[index] == 0) && _values[index] != target)
        {
            _values[index] = target;
            Version++;
        }
    }

    /// <summary>Sets a parameter by name; false for an unknown name.</summary>
    public bool SetTarget(string name, float value, bool immediate = false)
    {
        var i = IndexOf(name);
        if (i < 0)
            return false;
        SetTarget(i, value, immediate);
        return true;
    }

    /// <summary>Sets a labeled parameter to a label; false for an unknown name or label.</summary>
    public bool SetLabel(string name, string label, bool immediate = false)
    {
        var i = IndexOf(name);
        var l = i < 0 ? -1 : LabelIndex(i, label);
        if (l < 0)
            return false;
        SetTarget(i, l, immediate);
        return true;
    }

    /// <summary>Every parameter back to its default, immediately.</summary>
    public void Reset()
    {
        for (var i = 0; i < _values.Length; i++)
            SetTarget(i, _defaults[i], immediate: true);
    }

    /// <summary>Moves seeking values towards their targets by <paramref name="seconds"/>; true when a value changed.</summary>
    public bool Advance(double seconds)
    {
        if (!(seconds > 0))
            return false;
        var changed = false;
        for (var i = 0; i < _values.Length; i++)
        {
            var value = _values[i];
            var target = _targets[i];
            if (value == target)
                continue;
            var step = (float)(_seek[i] * seconds);
            _values[i] = MathF.Abs(target - value) <= step ? target : value + MathF.CopySign(step, target - value);
            changed = true;
        }

        if (changed)
            Version++;
        return changed;
    }

    private float Sanitize(int index, float value)
    {
        value = Math.Clamp(Finite(value), _min[index], _max[index]);
        return _kinds[index] == MusicParameterKind.Continuous ? value : MathF.Round(value);
    }

    private static float Finite(float value) => float.IsFinite(value) ? value : 0;
}
