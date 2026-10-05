using MainframeEngine;
using MainframeEngine.UI.Rml;

namespace Demo;

/// <summary>The Basic 3D scene's controls: pause the orbit, toggle the sun and lamps, tweak exposure.</summary>
public sealed class Basic3DPanel : UiDocument
{
    private static readonly string[] LampNames = ["Blue", "WarmSpot", "MintSpot"];

    private RmlDataModel? _model;
    private IVulkanContext? _vulkan;
    private float _enterExposure;

    public Basic3DPanel()
    {
        Source = "Content/Basic3D/panel.rml";
        AutoFocus = false;
    }

    private Node3D Scene => (Node3D)Parent!.Parent!;
    private OrbitCamera Camera => Scene.GetNode<OrbitCamera>("Camera");

    private static void SetLamps(Basic3DPanel panel, bool on)
    {
        foreach (var name in LampNames)
            panel.Scene.GetNode<Light3D>("Lights/" + name).Visible = on;
    }

    protected override void OnReady()
    {
        // Exposure is renderer-global: remember it so leaving this scene restores the other scenes' look.
        _vulkan = Tree?.Servers.Render?.Vulkan;
        _enterExposure = _vulkan?.Exposure ?? 1f;
        _model = CreateDataModel("basic3d")
            .Bind("paused", this, static d => d.Camera.Paused, static (d, v) => d.Camera.Paused = v)
            .Bind("sun", this, static d => d.Scene.GetNode<Light3D>("Lights/Sun").Visible, static (d, v) => d.Scene.GetNode<Light3D>("Lights/Sun").Visible = v)
            .Bind("lamps", this, static d => d.Scene.GetNode<Light3D>("Lights/Blue").Visible, SetLamps)
            .Bind("exposure", this, static d => d._vulkan?.Exposure ?? 1f,
                static (d, v) => { if (d._vulkan is { } vk) vk.Exposure = v; });
    }

    protected override void OnExitTree()
    {
        if (_vulkan is { } vk)
            vk.Exposure = _enterExposure;
        _vulkan = null;
        base.OnExitTree();
    }
}
