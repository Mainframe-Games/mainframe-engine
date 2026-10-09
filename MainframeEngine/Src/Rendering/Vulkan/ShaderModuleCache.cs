using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// One <c>VkShaderModule</c> per SPIR-V file, shared by every pipeline that uses it (each sky, Spine skeleton
/// or shape type used to load and destroy its own copy). Paths go through <see cref="ContentPaths.Resolve(string)"/>,
/// so <c>"Shaders/Sky/Sky.vk.vert.spv"</c> and <c>"Content/Shaders/Sky/Sky.vk.vert.spv"</c> are the same module.
/// Modules live until the renderer is disposed. Load-time API (it allocates); render thread only.
/// </summary>
public sealed unsafe class ShaderModuleCache : IDisposable
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Dictionary<string, ShaderModule> _modules = new(StringComparer.Ordinal);
    private bool _disposed;

    internal ShaderModuleCache(Vk vk, Device device)
    {
        _vk = vk;
        _device = device;
    }

    /// <summary>Modules loaded so far.</summary>
    public int Count => _modules.Count;

    /// <summary>The module for a <c>.spv</c> content path, loading it on first use.</summary>
    public ShaderModule Get(string spvPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = ContentPaths.Resolve(spvPath);
        if (_modules.TryGetValue(path, out var module))
            return module;

        byte[] code;
        try
        {
            code = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new FileNotFoundException($"Shader '{spvPath}' not found at {path} (is the engine's Content/Shaders copied to the output?).", path, e);
        }

        if (code.Length == 0 || code.Length % 4 != 0)
            throw new InvalidDataException($"Shader '{path}' is not SPIR-V ({code.Length} bytes).");
        if (SpirvInvariance.Applies(path))
            code = SpirvInvariance.MakePositionInvariant(code); // ADR 0172: the prepass and colour positions match exactly

        fixed (byte* p = code)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)p,
            };
            _vk.CreateShaderModule(_device, in info, null, out module).Check($"vkCreateShaderModule ({spvPath})");
        }

        _modules.Add(path, module);
        return module;
    }

    /// <summary>Destroys every module (the renderer calls this with the device idle).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var module in _modules.Values)
            _vk.DestroyShaderModule(_device, module, null);
        _modules.Clear();
    }
}
