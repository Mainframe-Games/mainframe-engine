using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;

namespace SilkVulkanExamples;

public unsafe class SilkVulkan : IDisposable
{
    private IWindow? window;
    private Vk? vk;
    private Instance instance;

    public SilkVulkan(string title, int width, int height)
    {
        InitWindow(title, width, height);
        InitVulkan(title);
    }

    #region Setup
    
    private void InitWindow(string title, int width, int height)
    {
        //Create a window.
        var options = WindowOptions.DefaultVulkan with
        {
            Size = new Vector2D<int>(width, height),
            Title = title,
        };

        window = Window.Create(options);
        window.Initialize();
        window.Center();

        if (window.VkSurface is null)
        {
            throw new Exception("Windowing platform doesn't support Vulkan.");
        }
    }
    
    private void InitVulkan(string appName)
    {
        CreateInstance(appName, "Mainframe Engine");
    }

    private void CreateInstance(string appName, string engineName)
    {
        vk = Vk.GetApi();

        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)Marshal.StringToHGlobalAnsi(appName),
            ApplicationVersion = new Version32(1, 0, 0),
            PEngineName = (byte*)Marshal.StringToHGlobalAnsi(engineName),
            EngineVersion = new Version32(1, 0, 0),
            ApiVersion = Vk.Version12
        };

        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo
        };

        var glfwExtensions = window!.VkSurface!.GetRequiredExtensions(out var glfwExtensionCount);

        createInfo.EnabledExtensionCount = glfwExtensionCount;
        createInfo.PpEnabledExtensionNames = glfwExtensions;
        createInfo.EnabledLayerCount = 0;

        if (vk.CreateInstance(in createInfo, null, out instance) != Result.Success)
        {
            throw new Exception("failed to create instance!");
        }

        Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
        Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);
    }

    #endregion
    

    public void Run()
    {
        window?.Run();
    }

    public void Dispose()
    {
        vk?.DestroyInstance(instance, null);
        vk?.Dispose();
        
        window?.Dispose();
    }
}