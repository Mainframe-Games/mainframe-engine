namespace MainframeEngine;

/// <summary>
/// One branch level of a <see cref="TreeOptions"/> (Ez Tree's <c>branch.&lt;x&gt;[level]</c> values), saved inline.
/// Level 0 is the trunk; <see cref="Angle"/> and <see cref="Start"/> do not apply to it, and <see cref="Children"/>
/// does not apply to level 3. Every generator input is a double so preset values reach the generator unchanged.
/// </summary>
/// <remarks>Ranges are Ez Tree's slider ranges (<c>src/app/ui.js</c>). Setters raise <see cref="Resource.Changed"/>.</remarks>
public sealed class TreeLevel : Resource
{
    private int _version;

    /// <summary>Changes whenever a property changes.</summary>
    public int Version => _version;

    /// <summary>Angle of the branches at this level to their parent, degrees (<c>branch.angle[n]</c>, n ≥ 1).</summary>
    [Export(Range = "0,180,1")]
    public double Angle { get; set => Set(ref field, value); }

    /// <summary>Child branches per branch of this level (<c>branch.children[n]</c>, n ≤ 2).</summary>
    [Export(Range = "0,10,1")]
    public int Children { get; set => Set(ref field, value); }

    /// <summary>Random curl of each section (<c>branch.gnarliness[n]</c>).</summary>
    [Export(Range = "-0.5,0.5,0.01")]
    public double Gnarliness { get; set => Set(ref field, value); }

    /// <summary>Branch length, Ez Tree units (<c>branch.length[n]</c>).</summary>
    [Export(Range = "0.1,100,0.1")]
    public double Length { get; set => Set(ref field, value); }

    /// <summary>Radius relative to the parent at the attachment point; the trunk's is absolute (<c>branch.radius[n]</c>).</summary>
    [Export(Range = "0.1,5,0.01")]
    public double Radius { get; set => Set(ref field, value); }

    /// <summary>Rings along a branch (<c>branch.sections[n]</c>).</summary>
    [Export(Range = "1,20,1")]
    public int Sections { get; set => Set(ref field, value); }

    /// <summary>Sides around a branch (<c>branch.segments[n]</c>).</summary>
    [Export(Range = "3,16,1")]
    public int Segments { get; set => Set(ref field, value); }

    /// <summary>Where branches of this level start along their parent, 0–1 (<c>branch.start[n]</c>, n ≥ 1).</summary>
    [Export(Range = "0,1,0.01")]
    public double Start { get; set => Set(ref field, value); }

    /// <summary>Radius lost from base to tip, 0–1 (<c>branch.taper[n]</c>; evergreens always taper fully).</summary>
    [Export(Range = "0,1,0.01")]
    public double Taper { get; set => Set(ref field, value); }

    /// <summary>Twist per section, radians (<c>branch.twist[n]</c>).</summary>
    [Export(Range = "-0.5,0.5,0.01")]
    public double Twist { get; set => Set(ref field, value); }

    /// <summary>
    /// Engine-only (ADR 0172): the length of this level's branches × this profile at where each starts along its parent
    /// (0..1; samples evenly spaced, linear between them; empty: 1). Shapes crowns: conical spruces and firs (shorter
    /// towards the top), beeches' layered spread. Assign a new array to change it.
    /// </summary>
    [Export]
    public double[] LengthProfile { get; set => Set(ref field, value ?? []); } = [];

    /// <summary>Ez Tree's <c>new TreeOptions()</c> branch values for levels 0–3.</summary>
    public static TreeLevel[] EzTreeDefaults() =>
    [
        new() { Children = 7, Gnarliness = 0.15, Length = 20, Radius = 1.5, Sections = 12, Segments = 8, Taper = 0.7 },
        new() { Angle = 70, Children = 7, Gnarliness = 0.2, Length = 20, Radius = 0.7, Sections = 10, Segments = 6, Start = 0.4, Taper = 0.7 },
        new() { Angle = 60, Children = 5, Gnarliness = 0.3, Length = 10, Radius = 0.7, Sections = 8, Segments = 4, Start = 0.3, Taper = 0.7 },
        new() { Angle = 60, Gnarliness = 0.02, Length = 1, Radius = 0.7, Sections = 6, Segments = 3, Start = 0.3, Taper = 0.7 },
    ];

    private void Set<T>(ref T storage, T value)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return;
        storage = value;
        _version++;
        EmitChanged();
    }
}
