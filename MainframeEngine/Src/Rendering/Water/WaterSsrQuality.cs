namespace MainframeEngine;

/// <summary>
/// Screen-space reflections of refracting water (ADR 0173; <c>rendering.waterSsr</c>, <see cref="RenderServer.WaterSsr"/>):
/// <see cref="Off"/> reflects only the sky, <see cref="Low"/> marches 16 steps (+ 4 refinements) up to 40 m,
/// <see cref="High"/> 32 (+ 6) up to 60 m.
/// </summary>
public enum WaterSsrQuality
{
    Off,
    Low,
    High,
}
