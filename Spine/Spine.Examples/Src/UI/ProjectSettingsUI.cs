using ImGuiNET;

namespace SilkSpine.UI;

public class ProjectSettingsUI
{
    public bool UseViewport = false;
    public bool Scissor = true;
    
    public void DrawImGui()
    {
        ImGui.Separator();

        if (!ImGui.CollapsingHeader("Project Settings", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.Checkbox("Viewport", ref UseViewport);
        ImGui.Checkbox("Scissor", ref Scissor);
    }
}