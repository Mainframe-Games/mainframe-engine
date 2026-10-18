using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MainframeEngine.Audio.Music;

// The adaptive-music data shared by the editor's song (.msong "adaptive" block) and the baked manifest (.mmusic): one
// set of types, so both sides read a value the same way. See docs/design/future/adaptive-music.md.

/// <summary>What values a <see cref="MusicParameterInfo"/> takes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MusicParameterKind>))]
public enum MusicParameterKind
{
    /// <summary>A float in [min, max], smoothed by the seek speed.</summary>
    [JsonStringEnumMemberName("continuous")]
    Continuous,

    /// <summary>Integers in [min, max].</summary>
    [JsonStringEnumMemberName("discrete")]
    Discrete,

    /// <summary>An index into the labels.</summary>
    [JsonStringEnumMemberName("labeled")]
    Labeled,
}

/// <summary>What a section is for.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MusicSectionKind>))]
public enum MusicSectionKind
{
    /// <summary>Playable and a transition destination.</summary>
    [JsonStringEnumMemberName("section")]
    Section,

    /// <summary>Played once between two sections by the rules that name it as their segment.</summary>
    [JsonStringEnumMemberName("transition")]
    Transition,

    /// <summary>A one-shot overlay.</summary>
    [JsonStringEnumMemberName("stinger")]
    Stinger,
}

/// <summary>Where a transition, stinger or stop may happen.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MusicQuantize>))]
public enum MusicQuantize
{
    /// <summary>As soon as the scheduling lookahead allows.</summary>
    [JsonStringEnumMemberName("immediate")]
    Immediate,

    [JsonStringEnumMemberName("beat")]
    Beat,

    [JsonStringEnumMemberName("bar")]
    Bar,

    /// <summary>Every <see cref="MusicTransitionInfo.Every"/> bars from the section's start.</summary>
    [JsonStringEnumMemberName("bars")]
    Bars,

    [JsonStringEnumMemberName("sectionEnd")]
    SectionEnd,

    /// <summary>The next marker with <c>exit</c> in the section, else its end.</summary>
    [JsonStringEnumMemberName("exitMarker")]
    ExitMarker,
}

/// <summary>Where a transition enters its destination.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MusicEntry>))]
public enum MusicEntry
{
    /// <summary>At its start.</summary>
    [JsonStringEnumMemberName("start")]
    Start,

    /// <summary>At the same offset as into the source section, wrapped to the destination's length.</summary>
    [JsonStringEnumMemberName("sync")]
    Sync,
}

/// <summary>What a curve drives on its layer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MusicCurveTarget>))]
public enum MusicCurveTarget
{
    /// <summary>Layer gain in dB (−80 dB or less is silence).</summary>
    [JsonStringEnumMemberName("volume")]
    Volume,

    /// <summary>The layer filter's cutoff in Hz (the layer needs a <see cref="MusicLayerFilter"/>).</summary>
    [JsonStringEnumMemberName("cutoff")]
    Cutoff,
}

/// <summary>How a curve moves between two points.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MusicCurveShape>))]
public enum MusicCurveShape
{
    [JsonStringEnumMemberName("linear")]
    Linear,

    /// <summary>Cosine ease.</summary>
    [JsonStringEnumMemberName("smooth")]
    Smooth,
}

/// <summary>The filter a layer runs live (after its baked stem).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MusicLayerFilter>))]
public enum MusicLayerFilter
{
    [JsonStringEnumMemberName("none")]
    None,

    [JsonStringEnumMemberName("lowpass")]
    LowPass,

    [JsonStringEnumMemberName("highpass")]
    HighPass,
}

/// <summary>A comparison in a transition condition.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MusicCompare>))]
public enum MusicCompare
{
    [JsonStringEnumMemberName("==")]
    Equal,

    [JsonStringEnumMemberName("!=")]
    NotEqual,

    [JsonStringEnumMemberName("<")]
    Less,

    [JsonStringEnumMemberName("<=")]
    LessOrEqual,

    [JsonStringEnumMemberName(">")]
    Greater,

    [JsonStringEnumMemberName(">=")]
    GreaterOrEqual,
}

/// <summary>A parameter value in a condition: a number, or a label of a labeled parameter.</summary>
[JsonConverter(typeof(MusicValueConverter))]
public readonly record struct MusicValue
{
    public MusicValue(float number) => Number = number;

    public MusicValue(string label) => Label = label ?? throw new ArgumentNullException(nameof(label));

    public float Number { get; }

    /// <summary>The label, or null for a number.</summary>
    public string? Label { get; }

    public bool IsLabel => Label is not null;

    public override string ToString() => Label ?? Number.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Reads and writes a <see cref="MusicValue"/> as a JSON number or string.</summary>
public sealed class MusicValueConverter : JsonConverter<MusicValue>
{
    public override MusicValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Number => new MusicValue(reader.GetSingle()),
        JsonTokenType.String => new MusicValue(reader.GetString()!),
        _ => throw new JsonException("A condition value is a number or a label."),
    };

    public override void Write(Utf8JsonWriter writer, MusicValue value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value.Label is { } label)
            writer.WriteStringValue(label);
        else
            writer.WriteNumberValue(value.Number);
    }
}

/// <summary>A curve point: <see cref="X"/> is the parameter value, <see cref="Y"/> the target's (dB or Hz). JSON <c>[x, y]</c>.</summary>
[JsonConverter(typeof(MusicCurvePointConverter))]
public readonly record struct MusicCurvePoint(float X, float Y);

/// <summary>Reads and writes a <see cref="MusicCurvePoint"/> as <c>[x, y]</c>.</summary>
public sealed class MusicCurvePointConverter : JsonConverter<MusicCurvePoint>
{
    public override MusicCurvePoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("A curve point is [x, y].");
        reader.Read();
        var x = reader.GetSingle();
        reader.Read();
        var y = reader.GetSingle();
        reader.Read();
        if (reader.TokenType != JsonTokenType.EndArray)
            throw new JsonException("A curve point is [x, y].");
        return new MusicCurvePoint(x, y);
    }

    public override void Write(Utf8JsonWriter writer, MusicCurvePoint value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteEndArray();
    }
}

/// <summary>A song parameter: <c>intensity</c> 0–1, <c>wave</c> 1–5, <c>area</c> = Calm/Combat/Boss.</summary>
public sealed class MusicParameterInfo
{
    public string Name { get; set; } = "";

    public MusicParameterKind Kind { get; set; }

    /// <summary>Continuous and discrete parameters: the lowest value.</summary>
    public float Min { get; set; }

    /// <summary>Continuous and discrete parameters: the highest value.</summary>
    public float Max { get; set; } = 1;

    /// <summary>The value at start (labeled: the label's index).</summary>
    public float Default { get; set; }

    /// <summary>Continuous parameters: units per second towards a new value (0 = instant).</summary>
    public float SeekSpeed { get; set; }

    /// <summary>Labeled parameters: the labels, in index order.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Labels { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public MusicParameterInfo Clone()
    {
        var copy = (MusicParameterInfo)MemberwiseClone();
        copy.Labels = Labels is null ? null : [.. Labels];
        return copy;
    }

    /// <summary>Copies every value of <paramref name="other"/> into this object (undo keeps object identity).</summary>
    public void CopyFrom(MusicParameterInfo other)
    {
        ArgumentNullException.ThrowIfNull(other);
        (Name, Kind, Min, Max, Default, SeekSpeed) = (other.Name, other.Kind, other.Min, other.Max, other.Default, other.SeekSpeed);
        Labels = other.Labels is null ? null : [.. other.Labels];
        Extra = other.Extra;
    }
}

/// <summary>A layer's volume or filter cutoff as a function of one parameter.</summary>
public sealed class MusicCurveInfo
{
    public string Layer { get; set; } = "";

    public MusicCurveTarget Target { get; set; }

    public string Parameter { get; set; } = "";

    public MusicCurveShape Shape { get; set; }

    /// <summary>Sorted by <see cref="MusicCurvePoint.X"/> when compiled; values outside the points hold the end values.</summary>
    public List<MusicCurvePoint> Points { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public MusicCurveInfo Clone()
    {
        var copy = (MusicCurveInfo)MemberwiseClone();
        copy.Points = [.. Points];
        return copy;
    }
}

/// <summary>One comparison: <c>{ "parameter": "area", "op": "==", "value": "Combat" }</c>.</summary>
public sealed class MusicComparison
{
    public string Parameter { get; set; } = "";

    public MusicCompare Op { get; set; }

    public MusicValue Value { get; set; }

    public MusicComparison Clone() => (MusicComparison)MemberwiseClone();
}

/// <summary>A transition's condition: every comparison in <see cref="All"/> holds and, when <see cref="Any"/> has any, one of those.</summary>
public sealed class MusicConditionGroup
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MusicComparison>? All { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MusicComparison>? Any { get; set; }

    public MusicConditionGroup Clone() => new()
    {
        All = All?.ConvertAll(c => c.Clone()),
        Any = Any?.ConvertAll(c => c.Clone()),
    };
}

/// <summary>A transition rule: from these sections to <see cref="To"/>, when the condition holds, at the quantum.</summary>
public sealed class MusicTransitionInfo
{
    /// <summary>Source section ids; <c>"*"</c> is any section.</summary>
    public List<string> From { get; set; } = [];

    public string To { get; set; } = "";

    /// <summary>The condition (null: never fires by itself, only through <c>GoToSection</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MusicConditionGroup? When { get; set; }

    public MusicQuantize Quantize { get; set; } = MusicQuantize.Bar;

    /// <summary><see cref="MusicQuantize.Bars"/>: the bar interval (1 when unset).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Every { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public MusicEntry Entry { get; set; }

    /// <summary>Beats over which the outgoing section fades after the boundary (null: 0 at a section end or exit marker, else ½).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? FadeOutBeats { get; set; }

    /// <summary>Beats over which the destination fades in (null: 0).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? FadeInBeats { get; set; }

    /// <summary>A transition section played once in between.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Segment { get; set; }

    /// <summary>A stinger fired at the boundary.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stinger { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public MusicTransitionInfo Clone()
    {
        var copy = (MusicTransitionInfo)MemberwiseClone();
        copy.From = [.. From];
        copy.When = When?.Clone();
        return copy;
    }

    /// <summary>Copies every value of <paramref name="other"/> into this object (undo keeps object identity).</summary>
    public void CopyFrom(MusicTransitionInfo other)
    {
        ArgumentNullException.ThrowIfNull(other);
        From = [.. other.From];
        (To, Quantize, Every, Entry) = (other.To, other.Quantize, other.Every, other.Entry);
        (FadeOutBeats, FadeInBeats, Segment, Stinger) = (other.FadeOutBeats, other.FadeInBeats, other.Segment, other.Stinger);
        When = other.When?.Clone();
        Extra = other.Extra;
    }
}
