using System.Drawing;
using System.Numerics;
using MainframeEngine.Serialization;

namespace MainframeEngine.Tests.Scene;

// Node and resource types used by the scene tests. They are internal (not private nested) so the source
// generator registers them, exactly like a game's types.

/// <summary>Appends every lifecycle callback to a shared log as "Name:Callback".</summary>
internal sealed class LoggingNode : Node
{
    public List<string>? Log { get; set; }

    public List<float> PhysicsDeltas { get; } = [];
    public int ProcessCount { get; private set; }
    public Action<LoggingNode>? OnProcessAction { get; set; }
    public Action<LoggingNode>? OnReadyAction { get; set; }
    public Action<LoggingNode>? OnEnterAction { get; set; }

    protected override void OnEnterTree() { Log?.Add($"{Name}:enter"); OnEnterAction?.Invoke(this); }

    protected override void OnReady() { Log?.Add($"{Name}:ready"); OnReadyAction?.Invoke(this); }

    protected override void OnExitTree() => Log?.Add($"{Name}:exit");

    protected override void OnProcess(in GameTime gameTime)
    {
        ProcessCount++;
        Log?.Add($"{Name}:process");
        OnProcessAction?.Invoke(this);
    }

    protected override void OnPhysicsProcess(float delta)
    {
        PhysicsDeltas.Add(delta);
        Log?.Add($"{Name}:physics");
    }
}

/// <summary>A node with no callbacks (never enters the process lists).</summary>
internal sealed class PlainNode : Node;

/// <summary>Records input routing.</summary>
internal sealed class InputNode : Node
{
    public List<string>? Log { get; set; }
    public bool HandleInput { get; set; }
    public bool HandleUnhandled { get; set; }

    protected override void OnInput(InputEvent inputEvent)
    {
        Log?.Add($"{Name}:input");
        if (HandleInput)
            GetViewport()!.SetInputAsHandled();
    }

    protected override void OnUnhandledInput(InputEvent inputEvent)
    {
        Log?.Add($"{Name}:unhandled");
        if (HandleUnhandled)
            GetViewport()!.SetInputAsHandled();
    }
}

internal enum TestMode
{
    First,
    Second,
    Third,
}

[Flags]
internal enum TestFlags
{
    None = 0,
    A = 1,
    B = 2,
    C = 4,
}

/// <summary>One exported member of every supported kind.</summary>
internal sealed class AllTypesNode : Node3D
{
    [Export] public bool Flag { get; set; }
    [Export] public byte Byte { get; set; }
    [Export] public int Int { get; set; } = 7;
    [Export] public long Long { get; set; }
    [Export] public uint UInt { get; set; }
    [Export] public ulong ULong { get; set; }
    [Export] public short Short { get; set; }
    [Export(Range = "0,10,0.5")] public float Float { get; set; } = 1.5f;
    [Export] public double Double { get; set; }
    [Export(Multiline = true)] public string Text { get; set; } = "default";
    [Export] public string? NullableText { get; set; }
    [Export] public TestMode Mode { get; set; }
    [Export(Flags = true)] public TestFlags Flags { get; set; }
    [Export] public Vector2 Vec2 { get; set; }
    [Export] public Vector4 Vec4 { get; set; }
    [Export] public Quaternion Quat { get; set; } = Quaternion.Identity;
    [Export] public Color Tint { get; set; } = Color.White;
    [Export] public Transform3D Xform { get; set; } = Transform3D.Identity;
    [Export] public Transform2D Xform2D { get; set; } = Transform2D.Identity;
    [Export(NodeType = typeof(Node3D))] public NodePath Target { get; set; }
    [Export] public float[]? Weights { get; set; }
    [Export] public List<string>? Tags { get; set; }
    [Export] public TestResource? Data { get; set; }
    [Export] public List<TestResource>? DataList { get; set; }

    [ExportGroup("Fields")]
    [Export] public int Field = 3;
}

internal sealed class TestResource : Resource
{
    [Export] public int Value { get; set; }
    [Export] public string Label { get; set; } = string.Empty;
    [Export] public TestResource? Next { get; set; }
}

/// <summary>Signals with different shapes, and handlers.</summary>
internal sealed class SignalNode : Node
{
    [Signal] public event Action? Pinged;
    [Signal] public event Action<int>? Counted;
    [Signal] public event Action<string, Node>? Named;

    public List<string> Received { get; } = [];

    public void Ping() => Pinged?.Invoke();
    public void Count(int n) => Counted?.Invoke(n);
    public void EmitNamed(string s, Node n) => Named?.Invoke(s, n);

    public void OnPinged() => Received.Add("pinged");
    public void OnCounted(int n) => Received.Add($"counted {n}");
    public void OnNamed(string s, Node n) => Received.Add($"named {s} {n.Name}");

    [SignalHandler]
    private void PrivateHandler() => Received.Add("private");

    private void Hidden() => Received.Add("hidden");

    internal void UseHidden() => Hidden(); // keeps the analyzer quiet about the unused method
}

/// <summary>Version 3 of a type whose "Speed" was "Velocity" in v1 and a scalar "Size" became a vector in v2.</summary>
[SerializedVersion(3)]
internal sealed class MigratedNode : Node
{
    [Export] public float Speed { get; set; }
    [Export] public Vector3 Size { get; set; } = Vector3.One;

    [SerializedMigration(1)]
    internal static void From1(PropertyBag properties) => properties.Rename("Velocity", "Speed");

    [SerializedMigration(2)]
    internal static void From2(PropertyBag properties)
    {
        if (properties.TryGet("Size", out var size) && size.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            var s = size.GetSingle();
            properties.SetNumbers("Size", s, s, s);
        }
    }
}

[TypeName("Custom.Renamed")]
internal sealed class RenamedTypeNode : Node
{
    [Export] public int Value { get; set; }
}

[Tool]
internal sealed class ToolNode : Node;

/// <summary>Counts transform notifications (light/camera-style server sync).</summary>
internal sealed class NotifyingNode3D : Node3D
{
    public NotifyingNode3D() => SetNotifyTransform(true);

    public int Notifications { get; private set; }

    protected override void OnTransformChanged() => Notifications++;
}

/// <summary>A 3D node that moves every frame (allocation and benchmark load).</summary>
internal sealed class SpinnerNode3D : Node3D
{
    protected override void OnProcess(in GameTime gameTime) =>
        RotationDegrees += new Vector3(0, 90f * gameTime.DeltaTime, 0);
}

/// <summary>Counts its callbacks without allocating.</summary>
internal sealed class CounterNode : Node
{
    public int Processed { get; private set; }
    public int PhysicsSteps { get; private set; }

    protected override void OnProcess(in GameTime gameTime) => Processed++;

    protected override void OnPhysicsProcess(float delta) => PhysicsSteps++;
}
