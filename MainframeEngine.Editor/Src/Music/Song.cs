using System.Text.Json;
using System.Text.Json.Serialization;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// An editor song (<c>.msong</c>): one tempo and time signature, a loop region, render settings, the master and its
/// tracks. Everything on the timeline is in ticks (<see cref="Ppq"/> per quarter note). Editor-only: games play the
/// rendered file. Edit it through a <see cref="SongDocument"/> (undoable); read and write it with <see cref="SongFormat"/>.
/// See docs/design/future/music-editor.md.
/// </summary>
public sealed class Song
{
    /// <summary>Ticks per quarter note.</summary>
    public const int DefaultPpq = 960;

    public int Format { get; set; } = SongFormat.Current;

    public string Uid { get; set; } = "";

    /// <summary>Beats (quarter notes) per minute.</summary>
    public double Tempo { get; set; } = 120;

    /// <summary>[beats per bar, beat unit].</summary>
    public int[] TimeSignature { get; set; } = [4, 4];

    public int Ppq { get; set; } = DefaultPpq;

    public SongLoop Loop { get; set; } = new();

    public SongRenderSettings Render { get; set; } = new();

    public SongMaster Master { get; set; } = new();

    public List<SongTrack> Tracks { get; set; } = [];

    /// <summary>Unknown top-level fields (a newer editor's), written back unchanged.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>A new empty song with a fresh <c>sng_</c> UID.</summary>
    public static Song CreateNew() => new() { Uid = AssetUid.Generate(AssetUid.SongPrefix) };

    /// <summary>Ticks per bar for the time signature.</summary>
    [JsonIgnore]
    public long TicksPerBar => (long)Ppq * 4 * Math.Max(1, TimeSignature.ElementAtOrDefault(0)) / Math.Max(1, TimeSignature.ElementAtOrDefault(1));

    /// <summary>Seconds for <paramref name="ticks"/> at the song's tempo.</summary>
    public double TicksToSeconds(double ticks) => ticks * 60.0 / (Tempo * Ppq);

    /// <summary>Ticks for <paramref name="seconds"/> at the song's tempo.</summary>
    public double SecondsToTicks(double seconds) => seconds * Tempo * Ppq / 60.0;

    /// <summary>The track with <paramref name="id"/>, or null.</summary>
    public SongTrack? FindTrack(string id) => Tracks.Find(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    /// <summary>End of the last clip in ticks (0 for an empty song).</summary>
    [JsonIgnore]
    public long EndTick
    {
        get
        {
            long end = 0;
            foreach (var track in Tracks)
                foreach (var clip in track.Clips)
                    end = Math.Max(end, clip.Start + clip.Length);
            return end;
        }
    }

    /// <summary>An id not used by any track (<c>t1</c>, <c>t2</c>, …).</summary>
    public string NextTrackId()
    {
        for (var i = 1; ; i++)
        {
            var id = "t" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (FindTrack(id) is null)
                return id;
        }
    }

    /// <summary>A deep copy (through the JSON form, so opaque plugin data is copied exactly).</summary>
    public Song Clone() => SongFormat.Parse(SongFormat.Write(this), "clone").Song;
}

/// <summary>The loop region in ticks (<see cref="End"/> &gt; <see cref="Start"/> when valid).</summary>
public sealed class SongLoop
{
    public bool Enabled { get; set; }

    public long Start { get; set; }

    public long End { get; set; } = (long)Song.DefaultPpq * 16;

    public SongLoop Clone() => (SongLoop)MemberwiseClone();
}

/// <summary>Where and how <see cref="SongRenderer"/> writes the song.</summary>
public sealed class SongRenderSettings
{
    /// <summary>Project path of the output (null: <c>Content/Music/&lt;SongName&gt;.wav</c>).</summary>
    public string? Output { get; set; }

    public int SampleRate { get; set; } = 48000;

    /// <summary>Vorbis quality (0–10) for the later <c>.ogg</c> encoder; unused while renders are WAV.</summary>
    public int Quality { get; set; } = 6;

    /// <summary>Seconds rendered past the last clip (without a loop) so tails ring out.</summary>
    public double TailSeconds { get; set; } = 2;

    /// <summary>With a loop: render it twice and keep the second pass (tails folded into the start).</summary>
    public bool SeamlessLoop { get; set; } = true;

    public SongRenderSettings Clone() => (SongRenderSettings)MemberwiseClone();
}

/// <summary>The master: volume and (opaque, plugin-phase) inserts.</summary>
public sealed class SongMaster
{
    public float VolumeDb { get; set; }

    public List<PluginInsert> Inserts { get; set; } = [];
}

/// <summary>What a track holds.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SongTrackKind>))]
public enum SongTrackKind
{
    /// <summary>MIDI clips played by an instrument.</summary>
    [JsonStringEnumMemberName("instrument")]
    Instrument,

    /// <summary>Audio files placed as clips.</summary>
    [JsonStringEnumMemberName("audio")]
    Audio,
}

public sealed class SongTrack
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public SongTrackKind Kind { get; set; }

    /// <summary>CSS colour (<c>#f59e0b</c>) or null for the default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; set; }

    public float VolumeDb { get; set; }

    /// <summary>−1 (left) … 1 (right).</summary>
    public float Pan { get; set; }

    public bool Mute { get; set; }

    public bool Solo { get; set; }

    /// <summary>Instrument tracks: the built-in ZzFX line or a plugin (null: silent).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InstrumentDescriptor? Instrument { get; set; }

    public List<PluginInsert> Inserts { get; set; } = [];

    /// <summary><see cref="MidiClip"/>s on instrument tracks, <see cref="AudioClip"/>s on audio tracks.</summary>
    [JsonConverter(typeof(SongClipListConverter))]
    public List<SongClip> Clips { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// A track's instrument: <c>{"builtin": "zzfx", "params": "zzfx(...[…])"}</c>, or a plugin descriptor with its saved state
/// (kept byte for byte even when the plugin is missing or the plugin host does not exist yet).
/// </summary>
public sealed class InstrumentDescriptor
{
    public const string ZzfxBuiltin = "zzfx";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Builtin { get; set; }

    /// <summary>Built-in ZzFX: the sound line (<see cref="ZzfxParameters.TryParse"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Params { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PluginDescriptor? Plugin { get; set; }

    /// <summary>Plugin state: base64 of the VST3 component + controller state (opaque here).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? State { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public bool IsZzfx => string.Equals(Builtin, ZzfxBuiltin, StringComparison.Ordinal);

    public static InstrumentDescriptor Zzfx(string line) => new() { Builtin = ZzfxBuiltin, Params = line };

    /// <summary>"name (vendor)" for messages such as "missing plugin: …".</summary>
    [JsonIgnore]
    public string DisplayName => Plugin is { } p
        ? string.IsNullOrEmpty(p.Vendor) ? p.Name ?? p.ClassId ?? "plugin" : $"{p.Name} ({p.Vendor})"
        : IsZzfx ? "ZzFX" : Builtin ?? "none";
}

/// <summary>Identity of a plugin (VST3 class ID); unknown fields are preserved.</summary>
public sealed class PluginDescriptor
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClassId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Vendor { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>An effect insert (plugin phase): kept and written back unchanged, bypassed while no plugin host exists.</summary>
public sealed class PluginInsert
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PluginDescriptor? Plugin { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? State { get; set; }

    public bool Bypass { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A region on a track's timeline, in ticks.</summary>
public abstract class SongClip
{
    public long Start { get; set; }

    public long Length { get; set; }

    [JsonIgnore]
    public long End => Start + Length;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public abstract SongClip Clone();
}

/// <summary>Notes on an instrument track; note times are relative to the clip's start.</summary>
public sealed class MidiClip : SongClip
{
    public List<MidiNote> Notes { get; set; } = [];

    public override MidiClip Clone()
    {
        var copy = (MidiClip)MemberwiseClone();
        copy.Notes = Notes.ConvertAll(n => n.Clone());
        return copy;
    }
}

public sealed class MidiNote
{
    /// <summary>MIDI note number (60 = C4).</summary>
    public int Pitch { get; set; } = 60;

    /// <summary>Ticks from the clip's start.</summary>
    public long Start { get; set; }

    public long Length { get; set; } = Song.DefaultPpq;

    /// <summary>1–127.</summary>
    public int Velocity { get; set; } = 100;

    public MidiNote Clone() => (MidiNote)MemberwiseClone();
}

/// <summary>A sound file on an audio track: <see cref="File"/> is an asset UID (<c>aud_…</c>) or a project path.</summary>
public sealed class AudioClip : SongClip
{
    public string File { get; set; } = "";

    /// <summary>Ticks into the file where the clip starts playing.</summary>
    public long Offset { get; set; }

    public float GainDb { get; set; }

    public override AudioClip Clone() => (AudioClip)MemberwiseClone();
}
