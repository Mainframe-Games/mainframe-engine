using System.Globalization;
using System.Text;
using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>
/// A <see cref="CustomInspectorAttribute"/> example (E5): the audio bus layout as a mixer — one strip per bus with its
/// send, volume (−/+ 1 dB), mute and solo toggles and remove, an Add Bus button and the layout's validation message —
/// instead of the generic (read-only) list of buses. Every change goes through the inspector context's history, so it
/// can be undone and is saved with the resource. Open it from Project Settings › Audio › Bus Layout › Edit, or by
/// double-clicking the <c>.mres</c> in the FileSystem panel.
/// </summary>
[CustomInspector(typeof(AudioBusLayout))]
public sealed class AudioBusLayoutInspector : ICustomInspector
{
    public string? GetHeaderRml(object target)
    {
        var layout = (AudioBusLayout)target;
        var rml = new StringBuilder(1024);
        rml.Append("<div class=\"mixer\"><div class=\"mixer-title\"><span class=\"icon icon-sm icon-adjustments-horizontal icon-audio\"></span><span>Mixer · ")
            .Append(layout.Buses.Count).Append(" bus").Append(layout.Buses.Count == 1 ? "" : "es").Append("</span>")
            .Append("<button class=\"tool-button small\" data-action=\"bus-add\" data-tooltip=\"Add Bus — a new bus sending to Master\">")
            .Append("<span class=\"icon icon-sm icon-plus\"></span></button></div>");
        for (var i = 0; i < layout.Buses.Count; i++)
        {
            var bus = layout.Buses[i];
            var master = string.Equals(bus.Name, AudioBusLayout.MasterBus, StringComparison.Ordinal);
            rml.Append("<div class=\"mixer-bus\"><span class=\"mixer-name\">").Append(RmlText.Escape(bus.Name)).Append("</span>")
                .Append("<span class=\"mixer-send\">").Append(master ? "out" : "→ " + RmlText.Escape(bus.Send)).Append("</span>")
                .Append("<button class=\"tool-button small\" data-action=\"bus-down:").Append(i).Append("\" data-tooltip=\"Volume −1 dB\"><span class=\"icon icon-sm icon-minus\"></span></button>")
                .Append("<span class=\"mixer-db mono\">").Append(bus.VolumeDb.ToString("0.0", CultureInfo.InvariantCulture)).Append(" dB</span>")
                .Append("<button class=\"tool-button small\" data-action=\"bus-up:").Append(i).Append("\" data-tooltip=\"Volume +1 dB\"><span class=\"icon icon-sm icon-plus\"></span></button>")
                .Append("<button class=\"tool-button small").Append(bus.Mute ? " active" : "").Append("\" data-action=\"bus-mute:").Append(i)
                .Append("\" data-tooltip=\"Mute\"><span class=\"icon icon-sm icon-volume-off\"></span></button>")
                .Append("<button class=\"tool-button small").Append(bus.Solo ? " active" : "").Append("\" data-action=\"bus-solo:").Append(i)
                .Append("\" data-tooltip=\"Solo\"><span class=\"icon icon-sm icon-headphones\"></span></button>");
            if (!master)
                rml.Append("<button class=\"tool-button small\" data-action=\"bus-remove:").Append(i)
                    .Append("\" data-tooltip=\"Remove Bus\"><span class=\"icon icon-sm icon-trash\"></span></button>");
            rml.Append("</div>");
        }

        if (layout.Validate() is { } problem)
            rml.Append("<div class=\"notice\">").Append(RmlText.Escape(problem)).Append("</div>");
        return rml.Append("</div>").ToString();
    }

    // The bus list is the mixer above (the generic inspector cannot edit lists of resources).
    public bool ShowProperty(object target, ExportPropertyInfo property) => property.Name != nameof(AudioBusLayout.Buses);

    public void OnAction(object target, string action, IInspectorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var layout = (AudioBusLayout)target;
        if (action == "bus-add")
        {
            var buses = new List<AudioBusInfo>(layout.Buses) { new() { Name = UniqueName(layout, "Bus"), Send = AudioBusLayout.MasterBus } };
            context.SetProperty(layout, Property(typeof(AudioBusLayout), nameof(AudioBusLayout.Buses)), buses);
            return;
        }

        var colon = action.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0 || !int.TryParse(action.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ||
            (uint)index >= (uint)layout.Buses.Count)
            return;
        var bus = layout.Buses[index];
        switch (action[..colon])
        {
            case "bus-up":
                context.SetProperty(bus, Property(typeof(AudioBusInfo), nameof(AudioBusInfo.VolumeDb)), MathF.Min(24f, bus.VolumeDb + 1f));
                break;
            case "bus-down":
                context.SetProperty(bus, Property(typeof(AudioBusInfo), nameof(AudioBusInfo.VolumeDb)), MathF.Max(-80f, bus.VolumeDb - 1f));
                break;
            case "bus-mute":
                context.SetProperty(bus, Property(typeof(AudioBusInfo), nameof(AudioBusInfo.Mute)), !bus.Mute);
                break;
            case "bus-solo":
                context.SetProperty(bus, Property(typeof(AudioBusInfo), nameof(AudioBusInfo.Solo)), !bus.Solo);
                break;
            case "bus-remove":
                var buses = new List<AudioBusInfo>(layout.Buses);
                buses.RemoveAt(index);
                context.SetProperty(layout, Property(typeof(AudioBusLayout), nameof(AudioBusLayout.Buses)), buses);
                break;
        }
    }

    private static ExportPropertyInfo Property(Type type, string name) =>
        TypeRegistry.GetRequired(type).FindProperty(name) ?? throw new InvalidOperationException($"{type.Name}.{name} is not exported.");

    private static string UniqueName(AudioBusLayout layout, string name)
    {
        for (var i = 1; ; i++)
        {
            var candidate = i == 1 ? name : name + i.ToString(CultureInfo.InvariantCulture);
            if (!layout.Buses.Any(b => string.Equals(b.Name, candidate, StringComparison.Ordinal)))
                return candidate;
        }
    }
}
