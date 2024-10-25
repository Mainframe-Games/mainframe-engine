using System.Numerics;
using ImGuiNET;
using Spine;

namespace SilkSpine.UI;

public struct SpineFolder
{
    public string Name;
    public string AtlasPath;
    public string JsonPath;
    public string TexturePath;
}

internal class InspectorUI
{
    public event Action<SpineFolder>? OnModelChanged;
    public event Action<string>? OnAnimationChanged;
    public event Action<bool>? OnFlipped;
    
    // mode
    private int _modelIndex;
    private readonly SpineFolder[] _folders;
    private readonly string[] _modelNames;
    
    // animation
    private int _animationIndex;
    private readonly string[] _animNames = new string[32];
    private bool _isFlipped;
    
    public readonly ProjectSettingsUI ProjectSettingsUI = new();
    
    public int Width { get; } = 300;
    public bool UseOrthographicCamera = true;

    
    public InspectorUI(SpineFolder[] folders)
    {
        _folders = folders;
        _modelNames = new string[folders.Length];
        for (var i = 0; i < folders.Length; i++)
            _modelNames[i] = folders[i].Name;
        
        for (int i = 0; i < _animNames.Length; i++)
            _animNames[i] = string.Empty;
    }

    public void OnImGui(
        Skeleton skeleton,
        ref Vector3 modelPosition,
        ref Vector3 modelRotation,
        ref Vector3 modelScale,
        double deltaTime)
    {
        var io = ImGui.GetIO();
        var screenSize = io.DisplaySize;
        ImGui.SetNextWindowPos(Vector2.One, ImGuiCond.Always, Vector2.Zero);
        ImGui.SetNextWindowSize(screenSize with { X = Width });
        if (ImGui.Begin("Spine Inspector",
                ImGuiWindowFlags.NoCollapse
                | ImGuiWindowFlags.AlwaysAutoResize 
                | ImGuiWindowFlags.NoResize 
                | ImGuiWindowFlags.NoMove))
        {
            ImGui.Value("FPS", io.Framerate, "%.0f");
            
            ImGui.Separator();
            ProjectSettingsUI.DrawImGui();
            
            BuildAnimNames(skeleton);
            
            ImGui.Separator();
            
            ImGui.Checkbox("Orthographic Camera", ref UseOrthographicCamera);
            
            if (ImGui.Combo("Model", ref _modelIndex, _modelNames, _modelNames.Length))
                OnModelChanged?.Invoke(_folders[_modelIndex]);
            
            if (ImGui.Combo("Animation", ref _animationIndex, _animNames, skeleton.Data.Animations.Count))
                OnAnimationChanged?.Invoke(_animNames[_animationIndex]);

            if (ImGui.Checkbox("Flip X", ref _isFlipped))
                OnFlipped?.Invoke(_isFlipped);

            ImGui.Separator();
            ImGui.Text("Transform");
            {
                ImGui.SliderFloat3("Position", ref modelPosition, -100, 100);
                ImGui.SliderFloat3("Rotation", ref modelRotation, -100, 100);
                ImGui.SliderFloat3("Scale", ref modelScale, -10, 10);
                if (ImGui.Button("Reset"))
                {
                    modelPosition = Vector3.Zero;
                    modelRotation = Vector3.Zero;
                    modelScale = Vector3.One;
                }
            }
        }
        ImGui.End();
    }

    private void BuildAnimNames(Skeleton skeleton)
    {
        for (int i = 0; i < skeleton.Data.Animations.Count; i++)
            _animNames[i] = skeleton.Data.Animations.Items[i].Name;
    }
}