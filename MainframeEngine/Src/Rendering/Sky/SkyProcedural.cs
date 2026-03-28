namespace MainframeEngine;

/// <summary>
/// Creates a procedural gradient sky with a sun disk.
/// </summary>
/// <param name="renderer"></param>
public class SkyProcedural(IRenderer renderer) : SkyEnvironment(renderer, SkyEnvironmentType.Procedural);