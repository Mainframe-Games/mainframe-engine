namespace MainframeEngine;

/// <summary>
/// A preset of the music editor's wavetable synth ("Mainframe Wavetable", ADR 0195): a project <c>.mres</c> (default
/// folder <c>Content/Presets/Wavetable/</c>) with its name (<see cref="Resource.ResourceName"/>), category, author, tags
/// and the patch, stored as the patch JSON a song's <c>instrument.patch</c> holds. The type lives in the engine core so
/// the generator registers it and a runtime synth could load it later; today only the editor reads it. A wavetable the
/// patch references by UID is a reference like any other resource's.
/// </summary>
[EditorIcon("wave-square")]
public sealed class WavetablePreset : Resource
{
    /// <summary>The preset menu's group: Bass, Lead, Pad, Pluck, Keys, FX or the author's own.</summary>
    [Export]
    public string Category { get; set; } = "";

    [Export]
    public string Author { get; set; } = "";

    /// <summary>Comma-separated words for searching.</summary>
    [Export]
    public string Tags { get; set; } = "";

    /// <summary>The patch JSON (the editor's <c>SynthPatch</c>, as written in a song).</summary>
    [Export]
    public string Patch { get; set; } = "";
}
