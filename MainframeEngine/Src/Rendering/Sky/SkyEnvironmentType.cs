namespace MainframeEngine;

public enum SkyEnvironmentType
{
    /// <summary>Procedurally generated gradient sky with a sun disk.</summary>
    Procedural,

    /// <summary>Equirectangular (360°) panoramic image mapped to the sky dome.</summary>
    Panoramic,

    /// <summary>Six-faced cubemap texture (faces: +X, -X, +Y, -Y, +Z, -Z).</summary>
    Cubemap,

    /// <summary>A physically based atmosphere lit by the sun (Hillaire 2020; ADR 0154).</summary>
    Physical,
}
