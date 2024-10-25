using ImGuiNET;

namespace SilkSpine.UI;

public class ProjectSettingsUI
{
    public bool UseViewport = true;
    public bool Scissor = true;
    
    public void DrawImGui()
    {
        if (!ImGui.CollapsingHeader("Project Settings", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.Checkbox("Viewport", ref UseViewport);
        ImGui.Checkbox("Scissor", ref Scissor);
    }
}