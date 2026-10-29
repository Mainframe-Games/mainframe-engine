using System.Globalization;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace MainframeEngine;

// G15 (docs/design/crash-reporting.md#gpu-device-loss-and-breadcrumbs): the GPU's identity, per-pass breadcrumbs, and
// VK_EXT_device_fault's report when the device is lost.
internal sealed unsafe partial class VulkanRenderer
{
    private const string DeviceFaultExtension = "VK_EXT_device_fault";

    private GpuInfo? _gpu;
    private GpuBreadcrumbs? _breadcrumbs;
    private readonly bool _breadcrumbsRequested;
    private readonly ulong _simulateDeviceLostAt;
    private ExtDeviceFault? _deviceFault;
    private bool _deviceFaultEnabled;
    private int _lastSubmittedSlot = -1;
    private ulong _lastSubmittedFrame;

    /// <summary>The GPU and driver (read once, after the physical device is picked).</summary>
    public GpuInfo? Gpu => _gpu;

    /// <summary>Breadcrumbs are being written (requested and created).</summary>
    internal bool HasBreadcrumbs => _breadcrumbs is not null;

    /// <summary>The breadcrumb marks of <paramref name="slot"/> (tests).</summary>
    internal (uint Begin, uint End)[]? ReadBreadcrumbs(int slot) => _breadcrumbs?.Read(slot);

    private void ReadGpuInfo()
    {
        var driver = new PhysicalDeviceDriverProperties { SType = StructureType.PhysicalDeviceDriverProperties };
        var props = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &driver };
        _vk!.GetPhysicalDeviceProperties2(_physicalDevice, &props);
        var p = props.Properties;
        var name = SilkMarshal.PtrToString((nint)p.DeviceName) ?? "";
        var driverName = SilkMarshal.PtrToString((nint)driver.DriverName) ?? "";
        var driverInfo = SilkMarshal.PtrToString((nint)driver.DriverInfo) ?? "";
        if (driverName.Length == 0)
            driverName = driver.DriverID == 0 ? "unknown" : driver.DriverID.ToString();
        var type = p.DeviceType switch
        {
            PhysicalDeviceType.IntegratedGpu => "IntegratedGpu",
            PhysicalDeviceType.DiscreteGpu => "DiscreteGpu",
            PhysicalDeviceType.VirtualGpu => "VirtualGpu",
            PhysicalDeviceType.Cpu => "Cpu",
            _ => "Other",
        };
        _gpu = new GpuInfo(name, p.VendorID, p.DeviceID, type, driverName, driverInfo,
            GpuInfo.DecodeDriverVersion(p.VendorID, p.DriverVersion, OperatingSystem.IsWindows()), GpuInfo.DecodeApiVersion(p.ApiVersion));
        Log.Info($"[Vulkan] GPU: {_gpu}");
        CrashContext.Gpu = _gpu;
    }

    // Called while building the device: enables VK_EXT_device_fault (and its feature) when the device has it. Returns the
    // feature struct to chain into VkDeviceCreateInfo.pNext (null when unavailable).
    private bool QueryDeviceFault(List<string> extensions, out PhysicalDeviceFaultFeaturesEXT features)
    {
        features = new PhysicalDeviceFaultFeaturesEXT { SType = StructureType.PhysicalDeviceFaultFeaturesExt };
        if (!IsDeviceExtensionAvailable(_physicalDevice, DeviceFaultExtension))
            return false;
        var query = new PhysicalDeviceFaultFeaturesEXT { SType = StructureType.PhysicalDeviceFaultFeaturesExt };
        var features2 = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &query };
        _vk!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        if (!query.DeviceFault)
            return false;
        extensions.Add(DeviceFaultExtension);
        features.DeviceFault = true;
        return true;
    }

    private void CreateDeviceFault()
    {
        if (!_deviceFaultEnabled)
            return;
        if (_vk!.TryGetDeviceExtension(_instance, _device, out ExtDeviceFault fault))
            _deviceFault = fault;
        else
            _deviceFaultEnabled = false;
    }

    private void CreateBreadcrumbs()
    {
        if (_breadcrumbsRequested)
            _breadcrumbs = new GpuBreadcrumbs(this);
        CrashContext.DeviceLoss = DescribeDeviceLoss;
    }

    /// <summary>Marks <paramref name="pass"/>'s start in the current frame (no render pass may be active; a no-op otherwise).</summary>
    internal void BeginPassMark(GpuPass pass)
    {
        if (_breadcrumbs is { } crumbs && _frameStarted && _passState == PassState.None)
            crumbs.Begin(_commandBuffers[_currentFrame], _currentFrame, pass, _frameNumber);
    }

    /// <summary>Marks <paramref name="pass"/>'s end in the current frame (no render pass may be active; a no-op otherwise).</summary>
    internal void EndPassMark(GpuPass pass)
    {
        if (_breadcrumbs is { } crumbs && _frameStarted && _passState == PassState.None)
            crumbs.End(_commandBuffers[_currentFrame], _currentFrame, pass, _frameNumber);
    }

    // Inside the pass sequence, where _passState still names the pass that just ended (its render pass is closed).
    private void BeginPassMarkUnchecked(CommandBuffer cb, GpuPass pass) => _breadcrumbs?.Begin(cb, _currentFrame, pass, _frameNumber);

    private void EndPassMarkUnchecked(CommandBuffer cb, GpuPass pass) => _breadcrumbs?.End(cb, _currentFrame, pass, _frameNumber);

    private void NoteSubmitted()
    {
        _lastSubmittedSlot = _currentFrame;
        _lastSubmittedFrame = _frameNumber;
    }

    // Tests (EngineOptions.SimulateDeviceLostAtFrame): the frame is submitted and finished, then reported lost, so the
    // breadcrumbs are deterministic (every pass of the frame completed).
    private void SimulateDeviceLoss(Fence frameFence)
    {
        if (_simulateDeviceLostAt == 0 || _frameNumber != _simulateDeviceLostAt)
            return;
        _vk!.WaitForFences(_device, 1, in frameFence, true, ulong.MaxValue);
        throw new VulkanDeviceLostException(string.Create(CultureInfo.InvariantCulture,
            $"[Vulkan] vkQueueSubmit (frame) failed: the device was lost (VK_ERROR_DEVICE_LOST, simulated at frame {_frameNumber})."));
    }

    /// <summary>What the GPU was doing at a loss: the last submitted frame's breadcrumbs and the driver's fault report.</summary>
    private CrashGpuLoss? DescribeDeviceLoss()
    {
        if (_disposed)
            return null;
        var loss = _breadcrumbs is { } crumbs && _lastSubmittedSlot >= 0
            ? crumbs.Describe(_lastSubmittedSlot, _lastSubmittedFrame)
            : new CrashGpuLoss { Frame = (long)_lastSubmittedFrame };
        try
        {
            ReadDeviceFault(loss);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            loss.FaultDescription = $"vkGetDeviceFaultInfoEXT failed: {e.Message}";
        }

        return loss;
    }

    private void ReadDeviceFault(CrashGpuLoss loss)
    {
        if (_deviceFault is not { } fault || _device.Handle == 0)
            return;
        var counts = new DeviceFaultCountsEXT { SType = StructureType.DeviceFaultCountsExt };
        var result = fault.GetDeviceFaultInfo(_device, &counts, null);
        if (result is not (Result.Success or Result.Incomplete))
            return;
        var addresses = counts.AddressInfoCount > 0 ? new DeviceFaultAddressInfoEXT[counts.AddressInfoCount] : [];
        var vendors = counts.VendorInfoCount > 0 ? new DeviceFaultVendorInfoEXT[counts.VendorInfoCount] : [];
        counts.VendorBinarySize = 0; // the vendor's binary crash dump is not kept
        fixed (DeviceFaultAddressInfoEXT* pAddresses = addresses)
        fixed (DeviceFaultVendorInfoEXT* pVendors = vendors)
        {
            var info = new DeviceFaultInfoEXT
            {
                SType = StructureType.DeviceFaultInfoExt,
                PAddressInfos = pAddresses,
                PVendorInfos = pVendors,
            };
            result = fault.GetDeviceFaultInfo(_device, &counts, &info);
            if (result is not (Result.Success or Result.Incomplete))
                return;
            loss.FaultDescription = SilkMarshal.PtrToString((nint)info.Description) ?? "";
        }

        foreach (var address in addresses)
            loss.FaultAddresses.Add(string.Create(CultureInfo.InvariantCulture,
                $"{address.AddressType} 0x{address.ReportedAddress:x16} (±0x{address.AddressPrecision:x})"));
        foreach (var vendor in vendors)
            loss.FaultVendorCodes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{SilkMarshal.PtrToString((nint)vendor.Description)} (code 0x{vendor.VendorFaultCode:x}, data 0x{vendor.VendorFaultData:x})"));
    }

    // After a loss nothing waits on the GPU (vkDeviceWaitIdle would return VK_ERROR_DEVICE_LOST, or hang on some drivers).
    private static bool DeviceIsLost => VulkanDeviceLostException.Occurred;

    private static void IgnoreDisposeError(Exception e)
    {
        try
        {
            Console.Error.WriteLine($"[Vulkan] Releasing the lost device: {e.GetType().Name}: {e.Message}");
        }
        catch (IOException)
        {
        }
    }

    private void DisposeCrashObjects()
    {
        if (ReferenceEquals(CrashContext.DeviceLoss?.Target, this))
            CrashContext.DeviceLoss = null;
        _breadcrumbs?.Dispose();
        _breadcrumbs = null;
        _deviceFault?.Dispose();
        _deviceFault = null;
    }
}
