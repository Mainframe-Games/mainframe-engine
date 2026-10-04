using System.Numerics;
using MainframeEngine.Serialization;
using DrawingColor = System.Drawing.Color;

namespace MainframeEngine.Editor.Tests;

public enum Mood
{
    Calm,
    Happy,
    Grumpy,
}

[Flags]
public enum Layers
{
    None = 0,
    World = 1,
    Player = 2,
    Enemies = 4,
    Everything = World | Player | Enemies,
}

/// <summary>A resource with a couple of values (resource slots, nested sub-inspectors).</summary>
public class TestSettings : Resource
{
    [Export]
    public float Strength { get; set; } = 1f;

    [Export]
    public string Label { get; set; } = "";
}

/// <summary>Every exported type and hint the inspector supports.</summary>
public class AllHintsNode : Node3D
{
    [ExportGroup("Numbers")]
    [Export]
    public int Count { get; set; } = 3;

    [Export]
    public float Speed { get; set; } = 1.5f;

    [Export(Range = "0,10,0.5")]
    public float Volume { get; set; } = 5f;

    [Export(Range = "1,8")]
    public int Slots { get; set; } = 2;

    [Export]
    public double Precise { get; set; }

    [ExportGroup("Text")]
    [Export]
    public bool Enabled { get; set; } = true;

    [Export]
    public string Title { get; set; } = "hello";

    [Export(Multiline = true)]
    public string Notes { get; set; } = "";

    [Export(File = "*.png,*.jpg")]
    public string Texture { get; set; } = "";

    [Export(Directory = true)]
    public string Folder { get; set; } = "";

    [ExportGroup("Choices")]
    [Export]
    public Mood Mood { get; set; } = Mood.Happy;

    [Export]
    public Layers Mask { get; set; } = Layers.World;

    [Export(Flags = true)]
    public Mood MoodAsFlags { get; set; }

    [ExportGroup("Math")]
    [Export]
    public Vector2 Offset { get; set; }

    [Export]
    public Vector3 Direction { get; set; } = Vector3.UnitZ;

    [Export]
    public Vector4 Weights { get; set; }

    [Export]
    public Quaternion Orientation { get; set; } = Quaternion.Identity;

    [Export]
    public DrawingColor Tint { get; set; } = DrawingColor.FromArgb(255, 255, 0, 0);

    [Export]
    public Vector3 GlowColor { get; set; } = new(1f, 0.5f, 0f);

    [Export]
    public Transform3D Frame { get; set; } = Transform3D.Identity;

    [ExportGroup("References")]
    [Export(NodeType = typeof(Camera3D))]
    public NodePath CameraPath { get; set; } = new("");

    [Export]
    public TestSettings? Settings { get; set; }

    [Export]
    public float[] Samples { get; set; } = [1f, 2f];

    [Export]
    public List<string> Tags { get; set; } = ["a"];

    [ExportGroup("")]
    [Export]
    public int Ungrouped { get; set; }
}

/// <summary>A game type with its own icon and family (like a game's player node).</summary>
[EditorIcon("bolt", Family = EditorIconFamily.Network)]
public class IconNode : Node3D
{
    /// <summary>How fast the <c>IconNode</c> charges, in units per second.</summary>
    [Export(Icon = "gauge")]
    public float Charge { get; set; } = 2f;

    /// <summary>Where the node points.</summary>
    [EditorIcon("target")]
    [Export]
    public NodePath Aim { get; set; } = new("");

    /// <summary>A free-form note.</summary>
    [Export]
    public string Note { get; set; } = "";
}

/// <summary>A subclass without an icon: it inherits <see cref="IconNode"/>'s.</summary>
public class InheritsIconNode : IconNode;

/// <summary>A node with a signal and a handler (connections survive delete/undo and duplicate).</summary>
public class SignalNode : Node3D
{
    [Signal]
    public event Action? Pinged;

    public int Received { get; private set; }

    public void Ping() => Pinged?.Invoke();

    public void OnPinged() => Received++;
}

/// <summary>A test custom inspector (found by attribute scan).</summary>
[CustomInspector(typeof(SignalNode))]
public sealed class SignalNodeInspector : ICustomInspector
{
    public static int Actions { get; private set; }

    public string? GetHeaderRml(object target) => "<div class=\"notice\" id=\"custom-header\"><button data-action=\"ping\">Ping</button></div>";

    public bool ShowProperty(object target, ExportPropertyInfo property) => property.Name != "ProcessPriority";

    public void OnAction(object target, string action, EditedScene scene)
    {
        if (action == "ping")
            Actions++;
    }
}
