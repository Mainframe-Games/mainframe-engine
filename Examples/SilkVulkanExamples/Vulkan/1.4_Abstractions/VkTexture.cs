using Silk.NET.Vulkan;

namespace SilkVulkanExamples.Vulkan;

/// <summary>
/// Wraps a Vulkan texture (image + view + sampler).
/// </summary>
public unsafe class VkTexture : IDisposable
{
    private readonly ExampleBase _owner;
    private readonly Image _image;
    private readonly DeviceMemory _memory;

    public ImageView View { get; }
    public Sampler Sampler { get; }

    public VkTexture(ExampleBase owner, string path)
    {
        _owner = owner;
        var (image, memory, view, sampler) = owner.CreateTextureFromFile(path);
        _image = image;
        _memory = memory;
        View = view;
        Sampler = sampler;
    }

    public void Dispose()
    {
        _owner.Vk.DestroySampler(_owner.Device, Sampler, null);
        _owner.Vk.DestroyImageView(_owner.Device, View, null);
        _owner.Vk.DestroyImage(_owner.Device, _image, null);
        _owner.Vk.FreeMemory(_owner.Device, _memory, null);
    }
}
