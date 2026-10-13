namespace MainframeEngine.Video.Models;

/// <summary>Parameters for cutting a Mainframe Engine recording from its sidecar. Edited per user.</summary>
/// <param name="MinIdleMs">Stretches with no player input shorter than this are left alone.</param>
/// <param name="PaddingMs">Kept on each side of an idle cut, as a lead-in and lead-out. Loading, pause and Skip edges are exact and not padded.</param>
/// <param name="MinKeepMs">Kept fragments shorter than this between two cuts are removed with them.</param>
/// <param name="MarkerWindowMs">Footage this close to a game marker is never cut for idleness.</param>
public sealed record GameplaySettings(int MinIdleMs, int PaddingMs, int MinKeepMs, int MarkerWindowMs)
{
    public static GameplaySettings Default { get; } = new(MinIdleMs: 3000, PaddingMs: 500, MinKeepMs: 1000, MarkerWindowMs: 1500);
}
