using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// The post tonemap's set 1 (G8e.4, ADR 0277): the glow map (Godot's <c>glow_map</c>, lens dirt) the glow is multiplied
/// by. One ring per tonemap pass (the main view's and each <see cref="SubViewportPost"/>'s), shaped like the colour
/// grade's LUT ring: <c>MaxFramesInFlight + 1</c> sets, each frame binds one, and a changed map is written into a slot no
/// frame in flight binds, so no set in use is ever rewritten. Without a map (or before it uploads) the set binds a 1 × 1
/// white texture and the strength is 0. Allocates nothing per frame.
/// </summary>
internal sealed unsafe class GlowMapSets : IDisposable
{
    private const int Slots = IVulkanContext.MaxFramesInFlight + 1;

    private readonly IVulkanContext _ctx;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet[] _sets = new DescriptorSet[Slots];
    private readonly ulong[] _usedFrame = new ulong[Slots];
    private readonly GpuTexture _white;
    private TextureGpu? _map;
    private int _mapGeneration = -1;
    private int _serial;  // bumped on every new map upload
    private int _key = -1; // what the current slot binds: 0 the white texture, else a _serial
    private int _slot = -1;

    public GlowMapSets(IVulkanContext ctx, string name)
    {
        _ctx = ctx;
        Layout = PipelineBuilder.CreateSetLayout(ctx,
            [new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit }],
            name + " glow map");
        _pool = PipelineBuilder.CreatePool(ctx, Slots, [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = Slots }], name + " glow map");
        for (var i = 0; i < Slots; i++)
            _sets[i] = PipelineBuilder.AllocateSet(ctx, _pool, Layout, name + " glow map");
        _white = GpuTexture.Create2D(ctx, 1, 1, [255, 255, 255, 255], TextureColorSpace.Srgb, TextureSampling.LinearClamp, false);
    }

    /// <summary>The set layout (one combined image sampler at binding 0).</summary>
    public DescriptorSetLayout Layout { get; }

    /// <summary>
    /// The set to bind this frame for <paramref name="map"/> (null: none); <paramref name="strength"/> becomes 0 when the
    /// map is missing or not uploaded yet, so the white placeholder changes nothing.
    /// </summary>
    public DescriptorSet For(ulong frame, Texture2D? map, ref float strength)
    {
        var useMap = map is not null && strength > 0f;
        if (useMap)
        {
            if (_map is null || !ReferenceEquals(_map.Texture, map))
            {
                _map?.Release(); // deletion-queued; a slot that binds it is never bound again before it is rewritten
                _map = new TextureGpu(_ctx, map!, TextureColorSpace.Srgb);
                _mapGeneration = -1;
            }

            if (!_map.Update())
                useMap = false;
            else if (_map.Generation != _mapGeneration)
            {
                _mapGeneration = _map.Generation;
                _serial++;
            }
        }

        if (!useMap)
            strength = 0f;
        var wanted = useMap ? _serial : 0;
        if (_slot >= 0 && _key == wanted)
        {
            _usedFrame[_slot] = frame;
            return _sets[_slot];
        }

        for (var i = 1; i <= Slots; i++)
        {
            var slot = (_slot + i + Slots) % Slots;
            if (_usedFrame[slot] != 0 && frame - _usedFrame[slot] < (ulong)IVulkanContext.MaxFramesInFlight)
                continue;
            PipelineBuilder.WriteImage(_ctx, _sets[slot], 0, useMap ? _map!.Gpu!.Descriptor : _white.Descriptor);
            _slot = slot;
            _key = wanted;
            _usedFrame[slot] = frame;
            return _sets[slot];
        }

        throw new InvalidOperationException("Every glow map set is in flight."); // unreachable: one slot per frame
    }

    /// <summary>Hands everything to the deletion queue (sets may still be in flight).</summary>
    public void Retire()
    {
        _map?.Release();
        _white.Dispose();
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_pool));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(Layout));
    }

    /// <summary>Destroys everything (the caller has waited for the device to be idle).</summary>
    public void Dispose()
    {
        _map?.Release();
        _white.Dispose();
        _ctx.Vk.DestroyDescriptorPool(_ctx.Device, _pool, null);
        _ctx.Vk.DestroyDescriptorSetLayout(_ctx.Device, Layout, null);
    }
}
