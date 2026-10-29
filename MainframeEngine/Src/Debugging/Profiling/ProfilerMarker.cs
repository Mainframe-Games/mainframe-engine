using System.Runtime.CompilerServices;

namespace MainframeEngine;

/// <summary>
/// A named profiler scope. Create it once (a <c>static readonly</c> field): creation registers the name (and may
/// allocate); markers made at the same source line with the same name share one id. Record with
/// <c>using var _ = Marker.Auto();</c>, or <see cref="Begin"/>/<see cref="End"/> where a using block does not fit.
/// Recording costs nothing (one static load and a branch) while <see cref="Profiler.IsRecording"/> is false.
/// </summary>
/// <example><code>
/// private static readonly ProfilerMarker Think = new("Enemy.Think", ProfilerCategory.Script);
/// protected override void OnPhysicsProcess(float delta) { using var _ = Think.Auto(); /* ... */ }
/// </code></example>
public readonly struct ProfilerMarker : IEquatable<ProfilerMarker>
{
    public ProfilerMarker(string name, ProfilerCategory category = ProfilerCategory.Game,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        : this(name, category, ProfilerMarkerKind.Cpu, file, line)
    {
    }

    internal ProfilerMarker(string name, ProfilerCategory category, ProfilerMarkerKind kind, string file, int line)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Id = Profiler.RegisterMarker(name, category, kind, file ?? "", line);
    }

    internal ProfilerMarker(ushort id) => Id = id;

    /// <summary>The registered id (0 for <c>default</c>, or when the registry is full).</summary>
    public ushort Id { get; }

    public string Name => Profiler.MarkerName(Id);

    public ProfilerCategory Category => Profiler.MarkerAt(Id).Category;

    /// <summary>Starts a scope that ends when the returned value is disposed (a no-op while not recording).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ProfilerScope Auto()
    {
        if (!Profiler.IsAvailable || !Profiler.s_recording)
            return default;
        return Profiler.BeginScope(Id);
    }

    /// <summary>Starts a scope that the next <see cref="End"/> of this marker on this thread ends.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Begin()
    {
        if (!Profiler.IsAvailable || !Profiler.s_recording)
            return;
        Profiler.BeginScope(Id);
    }

    /// <summary>Ends this marker's innermost open scope on this thread (scopes opened inside it and left open are closed and counted).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void End()
    {
        if (!Profiler.IsAvailable)
            return;
        Profiler.EndMarker(Id);
    }

    public bool Equals(ProfilerMarker other) => Id == other.Id;

    public override bool Equals(object? obj) => obj is ProfilerMarker other && Equals(other);

    public override int GetHashCode() => Id;

    public static bool operator ==(ProfilerMarker left, ProfilerMarker right) => left.Equals(right);

    public static bool operator !=(ProfilerMarker left, ProfilerMarker right) => !left.Equals(right);

    public override string ToString() => Name;
}

/// <summary>
/// An open profiler scope (<see cref="ProfilerMarker.Auto"/>, <see cref="Profiler.Scope"/>); disposing it ends the scope.
/// A <c>ref struct</c>: it cannot be boxed or escape. A scope begun while not recording is a no-op.
/// </summary>
public ref struct ProfilerScope
{
    private readonly ProfilerThreadBuffer? _buffer;
    private readonly int _depth;
    private readonly int _serial;

    internal ProfilerScope(ProfilerThreadBuffer buffer, int depth, int serial)
    {
        _buffer = buffer;
        _depth = depth;
        _serial = serial;
    }

    /// <summary>Ends the scope (scopes opened inside it and left open are closed with it and counted).</summary>
    public readonly void Dispose() => _buffer?.End(_depth, _serial);
}

/// <summary>
/// A per-frame value the profiler records with every frame (draw calls, enemies alive). Declare it once
/// (<c>static readonly</c>); <see cref="Set"/> keeps the value until the next set, <see cref="Add"/> accumulates over a
/// frame and restarts from 0 at each frame start. Both are lock-free and allocation-free from any thread.
/// </summary>
public readonly struct ProfilerCounter : IEquatable<ProfilerCounter>
{
    public ProfilerCounter(string name, ProfilerCounterUnit unit = ProfilerCounterUnit.Count)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Id = Profiler.RegisterCounter(name, unit);
    }

    /// <summary>The registered id (<see cref="Profiler.InvalidCounter"/> when the registry is full).</summary>
    public ushort Id { get; }

    public string Name => Profiler.CounterAt(Id).Name;

    /// <summary>The value this frame so far.</summary>
    public long Value => Profiler.CounterValue(Id);

    /// <summary>Sets the value (kept until the next set).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(long value)
    {
        if (Profiler.IsAvailable)
            Profiler.SetCounter(Id, value);
    }

    /// <summary>Adds to this frame's value (restarts from 0 at the next frame).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(long value)
    {
        if (Profiler.IsAvailable)
            Profiler.AddCounter(Id, value);
    }

    public bool Equals(ProfilerCounter other) => Id == other.Id;

    public override bool Equals(object? obj) => obj is ProfilerCounter other && Equals(other);

    public override int GetHashCode() => Id;

    public static bool operator ==(ProfilerCounter left, ProfilerCounter right) => left.Equals(right);

    public static bool operator !=(ProfilerCounter left, ProfilerCounter right) => !left.Equals(right);

    public override string ToString() => Name;
}
