using System.Globalization;

namespace MainframeEngine;

/// <summary>
/// The GPU and driver a renderer runs on (<see cref="IRenderer.Gpu"/>), read once at start-up and logged
/// (<c>[Vulkan] GPU: …</c>). Backend-neutral, so M11's devices fill the same record; crash reports carry it.
/// </summary>
/// <param name="Name">The device name (<c>Apple M3 Pro</c>, <c>NVIDIA GeForce RTX 4070</c>).</param>
/// <param name="VendorId">PCI vendor id (0x10de NVIDIA, 0x1002 AMD, 0x8086 Intel, 0x106b Apple).</param>
/// <param name="DeviceId">The vendor's device id.</param>
/// <param name="Type">IntegratedGpu, DiscreteGpu, VirtualGpu, Cpu or Other.</param>
/// <param name="Driver">The driver's name (<c>MoltenVK</c>, <c>NVIDIA</c>, <c>llvmpipe</c>…).</param>
/// <param name="DriverInfo">The driver's own version text (may be empty).</param>
/// <param name="DriverVersion">The driver version, decoded with the vendor's packing (<see cref="DecodeDriverVersion"/>).</param>
/// <param name="ApiVersion">The graphics API version the device supports (<c>1.2.296</c>).</param>
public sealed record GpuInfo(
    string Name,
    uint VendorId,
    uint DeviceId,
    string Type,
    string Driver,
    string DriverInfo,
    string DriverVersion,
    string ApiVersion)
{
    public const uint VendorNvidia = 0x10de;
    public const uint VendorAmd = 0x1002;
    public const uint VendorIntel = 0x8086;
    public const uint VendorApple = 0x106b;

    /// <summary>
    /// A driver version as people read it: NVIDIA packs 10.8.8.6 bits (<c>537.58.0.0</c>), Intel on Windows 18.14
    /// (<c>101.5333</c>); everyone else uses Vulkan's 10.10.12 (<c>major.minor.patch</c>).
    /// </summary>
    public static string DecodeDriverVersion(uint vendorId, uint version, bool windows)
    {
        if (vendorId == VendorNvidia)
            return string.Create(CultureInfo.InvariantCulture,
                $"{(version >> 22) & 0x3ff}.{(version >> 14) & 0xff}.{(version >> 6) & 0xff}.{version & 0x3f}");
        if (vendorId == VendorIntel && windows)
            return string.Create(CultureInfo.InvariantCulture, $"{version >> 14}.{version & 0x3fff}");
        return string.Create(CultureInfo.InvariantCulture, $"{version >> 22}.{(version >> 12) & 0x3ff}.{version & 0xfff}");
    }

    /// <summary>A Vulkan API version (<c>VK_MAKE_API_VERSION</c>: variant bits dropped) as <c>1.2.296</c>.</summary>
    public static string DecodeApiVersion(uint version) =>
        string.Create(CultureInfo.InvariantCulture, $"{(version >> 22) & 0x7f}.{(version >> 12) & 0x3ff}.{version & 0xfff}");

    /// <summary>One line for the log: <c>Apple M3 Pro (IntegratedGpu), MoltenVK 1.3.0, driver 0.2.2208, Vulkan 1.2.296</c>.</summary>
    public override string ToString()
    {
        var driver = DriverInfo.Length > 0 ? $"{Driver} {DriverInfo}" : Driver;
        return $"{Name} ({Type}), {driver}, driver {DriverVersion}, Vulkan {ApiVersion}";
    }
}
