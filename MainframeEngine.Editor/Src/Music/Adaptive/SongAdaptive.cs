using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MainframeEngine.Audio.Music;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// A song's adaptive-music data (<c>"adaptive"</c> in <c>.msong</c>): parameters, sections and markers on the timeline
/// (in ticks), layers, curves and transitions. Additive: a song without it plays and renders as before, and an older
/// editor keeps it unchanged in <see cref="Song.Extra"/>. Edit it through <see cref="SongDocument"/>; the engine's
/// <see cref="MusicPlan"/> compiles it (<see cref="SongAdaptiveCompiler"/>). See docs/design/future/adaptive-music.md.
/// </summary>
public sealed class SongAdaptive
{
    /// <summary>The section the music starts at.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Start { get; set; }

    /// <summary>Project path of the baked manifest (null: <c>Content/Music/&lt;SongName&gt;.mmusic</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Output { get; set; }

    /// <summary>Seconds rendered past each section's end for its tail.</summary>
    public double TailSeconds { get; set; } = 4;

    public List<MusicParameterInfo> Parameters { get; set; } = [];

    public List<SongSection> Sections { get; set; } = [];

    public List<SongMarker> Markers { get; set; } = [];

    public List<SongLayer> Layers { get; set; } = [];

    public List<MusicCurveInfo> Curves { get; set; } = [];

    public List<MusicTransitionInfo> Transitions { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public SongSection? FindSection(string? id) => id is null ? null : Sections.Find(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    public SongLayer? FindLayer(string? id) => id is null ? null : Layers.Find(l => string.Equals(l.Id, id, StringComparison.Ordinal));

    public MusicParameterInfo? FindParameter(string? name) =>
        name is null ? null : Parameters.Find(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>The section whose range holds <paramref name="tick"/>, or null.</summary>
    public SongSection? SectionAt(long tick) => Sections.Find(s => tick >= s.Start && tick < s.End);

    /// <summary>An id with <paramref name="prefix"/> not used by <paramref name="used"/> (<c>s1</c>, <c>s2</c>, …).</summary>
    internal static string NextId(string prefix, Func<string, bool> used)
    {
        for (var i = 1; ; i++)
        {
            var id = prefix + i.ToString(CultureInfo.InvariantCulture);
            if (!used(id))
                return id;
        }
    }
}

/// <summary>A section: a whole-bar range of the timeline (ticks), looping or continuing to another.</summary>
public sealed class SongSection
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public MusicSectionKind Kind { get; set; }

    /// <summary>First tick (on a bar line).</summary>
    public long Start { get; set; }

    /// <summary>Tick after the last (on a bar line).</summary>
    public long End { get; set; }

    public bool Loop { get; set; }

    /// <summary>The section that follows when it does not loop (null: the music ends).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Then { get; set; }

    /// <summary>CSS colour or null for the default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; set; }

    /// <summary>Stingers: where they land (beat when unset).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MusicQuantize? Quantize { get; set; }

    /// <summary>Stingers: beats that play before the quantum.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float PickupBeats { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public long Length => End - Start;

    public SongSection Clone() => (SongSection)MemberwiseClone();

    /// <summary>Copies every value of <paramref name="other"/> into this object (undo keeps object identity).</summary>
    public void CopyFrom(SongSection other)
    {
        ArgumentNullException.ThrowIfNull(other);
        (Id, Name, Kind, Start, End, Loop) = (other.Id, other.Name, other.Kind, other.Start, other.End, other.Loop);
        (Then, Color, Quantize, PickupBeats, Extra) = (other.Then, other.Color, other.Quantize, other.PickupBeats, other.Extra);
    }
}

/// <summary>A named tick; markers emit a signal in games, and <see cref="Exit"/> markers are transition points.</summary>
public sealed class SongMarker
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public long Tick { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Exit { get; set; }

    /// <summary>
    /// An exit marker that is also a tail point: the bake renders "cut here + tail" per layer, so an exit at it rings
    /// out like a section end instead of fading.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Tail { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A layer: the tracks naming it (<see cref="SongTrack.Layer"/>) bake to one stem per section.</summary>
public sealed class SongLayer
{
    /// <summary>The implicit layer of tracks without one: always at full volume, no curves.</summary>
    public const string MainId = "main";

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public MusicLayerFilter Filter { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
