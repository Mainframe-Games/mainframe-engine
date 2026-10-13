using System.Text.Json.Serialization;

namespace MainframeEngine.Video.Models;

/// <summary>
/// Who decided a segment's boundaries and state. Re-running silence detection replaces
/// <see cref="Auto"/> segments; re-running filler detection replaces <see cref="Filler"/> segments;
/// re-running gameplay detection replaces <see cref="Gameplay"/> segments. None of them touches
/// <see cref="Manual"/> ones, and each preserves the others' cuts.
/// </summary>
public enum SegmentOrigin
{
    [JsonStringEnumMemberName("auto")]
    Auto,

    [JsonStringEnumMemberName("manual")]
    Manual,

    [JsonStringEnumMemberName("claude")]
    Claude,

    /// <summary>A cut produced by filler-word detection. Schema version 2.</summary>
    [JsonStringEnumMemberName("filler")]
    Filler,

    /// <summary>A cut produced from a Mainframe Engine recording's sidecar (idle play, loading, pause). Schema version 3.</summary>
    [JsonStringEnumMemberName("gameplay")]
    Gameplay,
}
