using MainframeEngine.UI.Rml;

namespace MainframeEngine;

/// <summary>One collapsible section of the <see cref="DevOverlay"/>: RML body bound to its own data model.</summary>
public sealed class DevOverlayPanel
{
    internal DevOverlayPanel(string id, string title, string rml, Action<DevOverlayPanel>? bind)
    {
        Id = id;
        Title = title;
        Rml = rml;
        Bind = bind;
    }

    public string Id { get; }

    public string Title { get; }

    /// <summary>The RML fragment shown as the section body.</summary>
    public string Rml { get; }

    /// <summary>Whether the body is shown; bound as <c>open</c> in the panel's data model (the title toggles it).</summary>
    public bool Expanded { get; set; } = true;

    /// <summary>The panel's data model (<c>dev_{id}</c>); null until the overlay document bound it (inside the bind callback).</summary>
    public RmlDataModel? Model { get; internal set; }

    internal Action<DevOverlayPanel>? Bind { get; }

    internal string ModelName => "dev_" + Id;

    /// <summary>Marks a bound value as changed so the view refreshes; does nothing until the panel is bound.</summary>
    public void Dirty(string name) => Model?.Dirty(name);
}
