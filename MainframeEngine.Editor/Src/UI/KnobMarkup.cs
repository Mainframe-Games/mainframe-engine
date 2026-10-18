using System.Globalization;
using System.Text;
using MainframeEngine.Editor.Music;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The knob, toggle and choice markup of the music windows (the plugin window, the synth window and its FX panel), built
/// from <see cref="BuiltinParam"/>s, and the per-change update of what a control shows. A control's ids are
/// <c>{prefix}kt-i</c> (toggle), <c>{prefix}kc-i</c> (choice), <c>{prefix}kp-i</c> (knob pointer) and <c>{prefix}kv-i</c>
/// (knob value); every control carries <c>data-{prefix}p="i"</c>, the dial of a knob the class <c>knob-dial</c>.
/// </summary>
internal static class KnobMarkup
{
    /// <summary>The pointer rotation for each whole degree of a knob's 270° travel (index 0 = −135°).</summary>
    public static readonly string[] Degrees = [.. Enumerable.Range(0, 271).Select(i => $"rotate({(i - 135).ToString(CultureInfo.InvariantCulture)}deg)")];

    /// <summary>The attribute every control of <paramref name="prefix"/> carries with its index.</summary>
    public static string Attribute(string prefix) => "data-" + prefix + "p";

    /// <summary>
    /// Appends parameter <paramref name="p"/>'s control (index <paramref name="i"/>). A knob with <paramref name="drop"/> accepts
    /// modulation drops (<c>data-drop</c>) and holds a <c>{prefix}km-i</c> element for its modulation marks.
    /// </summary>
    public static void Append(StringBuilder sb, BuiltinParam p, int i, string prefix = "", string? tooltipExtra = null, string? drop = null)
    {
        var n = i.ToString(CultureInfo.InvariantCulture);
        var name = RmlText.Escape(p.Name);
        var attribute = Attribute(prefix);
        switch (p.Curve)
        {
            case ParamCurve.Toggle:
                sb.Append("<div class=\"pw-choice-wrap\"><div class=\"pw-toggle\" id=\"").Append(prefix).Append("kt-").Append(n).Append("\" ")
                    .Append(attribute).Append("=\"").Append(n).Append("\" data-tooltip=\"").Append(name).Append(" — click to switch\">").Append(name)
                    .Append("</div></div>");
                break;
            case ParamCurve.Choice:
                sb.Append("<div class=\"pw-choice-wrap\"><div class=\"pw-choice\" id=\"").Append(prefix).Append("kc-").Append(n).Append("\" ")
                    .Append(attribute).Append("=\"").Append(n).Append("\" data-tooltip=\"").Append(name)
                    .Append(" — click for the choices\"></div><span class=\"pw-param-name\">").Append(name).Append("</span></div>");
                break;
            default:
                sb.Append("<div class=\"knob\" data-tooltip=\"").Append(name).Append(" — drag up or down (Shift: fine) · wheel steps · double-click resets")
                    .Append(tooltipExtra).Append("\"><div class=\"knob-dial\" ").Append(attribute).Append("=\"").Append(n).Append('"');
                if (drop is not null)
                    sb.Append(" data-drop=\"").Append(drop).Append('"');
                sb.Append("><div class=\"knob-pointer\" id=\"").Append(prefix).Append("kp-").Append(n).Append("\"></div>");
                if (drop is not null)
                    sb.Append("<div id=\"").Append(prefix).Append("km-").Append(n).Append("\"></div>");
                sb.Append("</div><div class=\"knob-name\">").Append(name).Append("</div><div class=\"knob-value mono\" id=\"").Append(prefix).Append("kv-")
                    .Append(n).Append("\"></div></div>");
                break;
        }
    }

    /// <summary>Shows <paramref name="value"/> on control <paramref name="index"/>; <paramref name="shownText"/> caches the knob readouts.</summary>
    public static void Show(RmlDocument document, string prefix, int index, BuiltinParam p, float value, string?[] shownText)
    {
        Span<char> id = stackalloc char[24];
        switch (p.Curve)
        {
            case ParamCurve.Toggle:
                if (document.GetElementById(Id(id, prefix, "kt-", index)) is { IsNull: false } toggle)
                    toggle.SetClass("active", value >= 0.5f);
                return;
            case ParamCurve.Choice:
                if (document.GetElementById(Id(id, prefix, "kc-", index)) is { IsNull: false } choice)
                    choice.SetInnerRml(RmlText.Escape(p.Format(value)));
                return;
        }

        if (document.GetElementById(Id(id, prefix, "kp-", index)) is not { IsNull: false } pointer)
            return;
        pointer.SetProperty("transform", Degrees[Math.Clamp((int)MathF.Round(p.ToNormalized(value) * 270), 0, 270)]);
        var text = p.Format(value);
        if (text == shownText[index])
            return;
        shownText[index] = text;
        document.GetElementById(Id(id, prefix, "kv-", index)).SetInnerRml(RmlText.Escape(text));
    }

    /// <summary><c>{prefix}{kind}{index}</c> in <paramref name="buffer"/> (no allocation).</summary>
    public static ReadOnlySpan<char> Id(Span<char> buffer, string prefix, string kind, int index)
    {
        prefix.CopyTo(buffer);
        kind.CopyTo(buffer[prefix.Length..]);
        var at = prefix.Length + kind.Length;
        index.TryFormat(buffer[at..], out var written, provider: CultureInfo.InvariantCulture);
        return buffer[..(at + written)];
    }

    /// <summary>The index in <paramref name="prefix"/>'s attribute on <paramref name="element"/> or an ancestor, or −1.</summary>
    public static int IndexOf(RmlElement element, string prefix, bool dialOnly = false)
    {
        var attribute = Attribute(prefix);
        for (var el = element; !el.IsNull && el.TagName != "body"; el = el.Parent)
        {
            if (el.GetAttribute(attribute) is not { Length: > 0 } value)
                continue;
            if (dialOnly && !el.IsClassSet("knob-dial"))
                return -1;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
        }

        return -1;
    }
}
