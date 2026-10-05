namespace MainframeEngine;

/// <summary>
/// A canvas item shader (Godot's <c>Shader</c> with <c>shader_type canvas_item</c>): GLSL with Godot's canvas built-ins,
/// compiled to SPIR-V at build time (docs/design/canvas.md). Completed by the shader pipeline (E5 of the Crash Site Defense
/// port); for now it carries what the canvas batcher needs.
/// </summary>
[EditorIcon("code")]
public sealed class Shader : Resource
{
    /// <summary>True when the shader defines <c>vertex()</c>: its items keep local vertices and get MODEL_MATRIX.</summary>
    public bool HasVertexFunction { get; set; }

    /// <summary>The shader's <c>render_mode blend_*</c>.</summary>
    public CanvasBlendMode BlendMode { get; set; }
}

/// <summary>A material running a <see cref="MainframeEngine.Shader"/> with per-material uniform values (Godot's <c>ShaderMaterial</c>).</summary>
[EditorIcon("palette")]
public sealed class ShaderMaterial : Material
{
    private readonly Dictionary<string, object> _parameters = new(StringComparer.Ordinal);

    [Export]
    public Shader? Shader
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field = value;
            Touch();
        }
    }

    /// <summary>Sets a uniform by name (float, int, bool, vectors, colours, arrays of them, <see cref="Texture2D"/>).</summary>
    public void SetShaderParameter(string name, object value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        _parameters[name] = value;
        Touch();
    }

    /// <summary>The value set for a uniform, or null when it was never set (the shader's default applies).</summary>
    public object? GetShaderParameter(string name) => _parameters.GetValueOrDefault(name);

    /// <summary>Every parameter set on this material.</summary>
    public IReadOnlyDictionary<string, object> Parameters => _parameters;

    public override MaterialRenderState RenderState => default;
}
