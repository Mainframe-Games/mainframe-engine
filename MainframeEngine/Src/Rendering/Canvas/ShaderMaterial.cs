namespace MainframeEngine;

/// <summary>
/// A material running a canvas <see cref="MainframeEngine.Shader"/> with per-material uniform values (Godot's
/// <c>ShaderMaterial</c>). Values are set by name; unset uniforms use the shader's defaults. Samplers take a
/// <see cref="Texture2D"/>.
/// </summary>
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

    /// <summary>
    /// Sets a uniform by name: float, int, bool, <see cref="System.Numerics.Vector2"/>/<c>3</c>/<c>4</c> (colours are Vector4),
    /// arrays of those for array uniforms, or a <see cref="Texture2D"/> for a sampler.
    /// </summary>
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
