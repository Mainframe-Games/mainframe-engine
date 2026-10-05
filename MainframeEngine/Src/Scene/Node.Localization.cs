using MainframeEngine.Localization;

namespace MainframeEngine;

/// <summary>Whether a node translates its text automatically (<see cref="Node.Atr(string, string?)"/>), Godot's <c>auto_translate_mode</c>.</summary>
public enum AutoTranslateMode
{
    /// <summary>Same as the parent; a root without a parent translates.</summary>
    Inherit,

    /// <summary>Translate this node's text (and inheriting descendants').</summary>
    Always,

    /// <summary>Show text as written (player names, debug text); inheriting descendants too.</summary>
    Disabled,
}

public partial class Node
{
    // Stored as a byte (with the process modes): keeps Node at 232 bytes, see Node.Processing.cs.
    private byte _autoTranslateMode;

    /// <summary>
    /// Whether <see cref="Atr(string, string?)"/> translates for this node (default: inherit; translated at the root).
    /// Changing it re-runs <see cref="OnLocaleChanged"/> on the subtree so displayed text updates.
    /// </summary>
    [Export]
    public AutoTranslateMode AutoTranslateMode
    {
        get => (AutoTranslateMode)_autoTranslateMode;
        set
        {
            if (_autoTranslateMode == (byte)value)
                return;
            _autoTranslateMode = (byte)value;
            if (_tree is not null)
                PropagateLocaleChanged();
        }
    }

    /// <summary>True when this node's <see cref="AutoTranslateMode"/> (resolved through its parents) translates.</summary>
    public bool CanAutoTranslate()
    {
        for (var node = this; node is not null; node = node._parent)
        {
            switch ((AutoTranslateMode)node._autoTranslateMode)
            {
                case AutoTranslateMode.Always:
                    return true;
                case AutoTranslateMode.Disabled:
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Translates <paramref name="message"/> if this node auto-translates (Godot's <c>atr</c>), otherwise returns it
    /// unchanged. Use it for <c>[Export(Translatable = true)]</c> strings when the node displays them (in
    /// <see cref="OnReady"/> and <see cref="OnLocaleChanged"/>).
    /// </summary>
    public string Atr(string message, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!CanAutoTranslate())
            return message;
        return context is null ? Tr._(message) : Tr.P(context, message);
    }

    /// <summary>Plural form of <see cref="Atr"/> (<c>n</c> is <c>{0}</c>); untranslated when auto-translation is off.</summary>
    public string AtrN(string singular, string plural, long n, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(singular);
        ArgumentNullException.ThrowIfNull(plural);
        if (CanAutoTranslate())
            return context is null ? Tr.N(singular, plural, n) : Tr.NP(context, singular, plural, n);
        var text = n == 1 ? singular : plural;
        return Tr.FormatUntranslated(text, n);
    }

    /// <summary>
    /// Called on nodes inside a tree after <see cref="Tr.SetLocale"/> (parents before children; deferred to the end of
    /// the frame when the tree is processing) and when <see cref="AutoTranslateMode"/> changes: re-translate displayed
    /// text here. Not called on entering the tree — translate in <see cref="OnReady"/> too.
    /// </summary>
    protected virtual void OnLocaleChanged()
    {
    }

    internal void PropagateLocaleChanged()
    {
        if (_freed || _tree is null)
            return;
        OnLocaleChanged();

        var count = _children?.Count ?? 0;
        if (count == 0)
            return;
        var snapshot = System.Buffers.ArrayPool<Node>.Shared.Rent(count);
        _children!.CopyTo(snapshot);
        try
        {
            for (var i = 0; i < count; i++)
            {
                var child = snapshot[i];
                if (ReferenceEquals(child._parent, this))
                    child.PropagateLocaleChanged();
            }
        }
        finally
        {
            Array.Clear(snapshot, 0, count);
            System.Buffers.ArrayPool<Node>.Shared.Return(snapshot);
        }
    }
}
