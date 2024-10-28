using ImGuiNET;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;

namespace SilkTest.Examples;

internal class ImGuiExample : ExampleBase
{
    // Declare some variables
    private ImGuiController controller = null!;

    public override void OnLoad(IWindow window)
    {
        base.OnLoad(window);
        controller = new ImGuiController(Gl, window, InputContext);
    }

    public override void OnRender(double deltaTime)
    {
        base.OnRender(deltaTime);

        // Make sure ImGui is up-to-date
        controller.Update((float)deltaTime);

        // This is where you'll do any rendering beneath the ImGui context
        // Here, we just have a blank screen.
        Gl.Clear((uint)ClearBufferMask.ColorBufferBit);

        // This is where you'll do all of your ImGUi rendering
        // Here, we're just showing the ImGui built-in demo window.
        ImGui.ShowDemoWindow();

        // Make sure ImGui renders too!
        controller.Render();
    }

    public override void OnClose()
    {
        // Dispose our controller first
        controller.Dispose();

        base.OnClose();
    }
}
