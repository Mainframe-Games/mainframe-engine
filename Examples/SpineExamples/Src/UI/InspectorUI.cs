using System.Numerics;
using ImGuiNET;
using MainframeEngine;
using Spine;

namespace SilkSpine.UI;

internal class InspectorUI
{
    public event Action<SpineFolder>? OnModelChanged;
    public event Action<string>? OnAnimationChanged;
    
    // mode
    private int _modelIndex;
    private readonly SpineFolder[] _folders;
    private readonly string[] _modelNames;
    
    // animation
    private int _animationIndex;
    private readonly string[] _animNames = new string[32];
    public bool IsFlipped;
    public float SpineScale;
    
    public int Width { get; } = 300;

    public bool UseOrthographicCamera = true;
    public float ZSpacing = 0.5f;
    public bool UpdatePhysics;

    public InspectorUI(SpineFolder[] folders)
    {
        _folders = folders;
        _modelNames = new string[folders.Length];
        for (var i = 0; i < folders.Length; i++)
            _modelNames[i] = folders[i].Name;
        
        for (int i = 0; i < _animNames.Length; i++)
            _animNames[i] = string.Empty;
    }

    public void OnImGui(Skeleton skeleton,
        ICamera camera,
        ref float cameraSpeed,
        ref Vector3 modelPosition,
        ref Vector3 modelRotation,
        ref float modelScale)
    {
        var io = ImGui.GetIO();
        var screenSize = io.DisplaySize;
        ImGui.SetNextWindowPos(Vector2.One, ImGuiCond.Always, Vector2.Zero);
        ImGui.SetNextWindowSize(screenSize with { X = Width });
        if (ImGui.Begin("Spine Inspector",
                ImGuiWindowFlags.AlwaysAutoResize 
                | ImGuiWindowFlags.NoResize 
                | ImGuiWindowFlags.NoMove))
        {
            ImGui.Value("FPS", io.Framerate, "%.0f");
            
            BuildAnimNames(skeleton);
            
            ImGui.Separator();
            DrawCameraOptions(camera, ref cameraSpeed);
            
            ImGui.Separator();
            DrawSpineOptions(skeleton);

            ImGui.Separator();
            DrawTransformOptions(ref modelPosition, ref modelRotation, ref modelScale);

            ImGui.Separator();

        }
        ImGui.End();
    }

    private static void DrawTransformOptions(ref Vector3 modelPosition, ref Vector3 modelRotation, ref float modelScale)
    {
        ImGui.Text("Transform");
        ImGui.SliderFloat3("Position", ref modelPosition, -10, 10, "%.3f");
        ImGui.SliderFloat3("Rotation", ref modelRotation, -180, 180, "%.2f");
        ImGui.SliderFloat("Scale", ref modelScale, 0.01f, 5, "%.2f");
        if (ImGui.Button("Reset Transform"))
        {
            modelPosition = Vector3.Zero;
            modelRotation = Vector3.Zero;
            modelScale = 1;
        }
    }

    private void DrawSpineOptions(Skeleton skeleton)
    {
        ImGui.Text("Spine Options");
            
        if (ImGui.Combo("File", ref _modelIndex, _modelNames, _modelNames.Length))
            OnModelChanged?.Invoke(_folders[_modelIndex]);
            
        if (ImGui.Combo("Animation", ref _animationIndex, _animNames, skeleton.Data.Animations.Count))
            OnAnimationChanged?.Invoke(_animNames[_animationIndex]);
    
        ImGui.Checkbox("Update Physics", ref UpdatePhysics);
        ImGui.Checkbox("Flip X", ref IsFlipped);
            
        ImGui.SliderFloat("Spine Scale", ref SpineScale, 0.02f, 1f, "%.2f");
        ImGui.SliderFloat("Z Spacing", ref ZSpacing, 0.01f, 0.5f, "%.2f");
    }

    private void DrawCameraOptions(ICamera camera, ref float cameraSpeed)
    {
        ImGui.Text("Camera");
        ImGui.Text($"Camera Position: {camera.Position:0.0}");
        ImGui.Checkbox("Orthographic Camera", ref UseOrthographicCamera);
        ImGui.SliderFloat("Camera Speed", ref cameraSpeed, 1, 200, "%.1f");
    }

    private void BuildAnimNames(Skeleton skeleton)
    {
        for (int i = 0; i < skeleton.Data.Animations.Count; i++)
            _animNames[i] = skeleton.Data.Animations.Items[i].Name;
    }
}