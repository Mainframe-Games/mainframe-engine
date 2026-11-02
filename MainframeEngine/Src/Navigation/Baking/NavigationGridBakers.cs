namespace MainframeEngine;

/// <summary>
/// Fills a <see cref="NavigationGrid2D"/>'s cells from somewhere else in the scene: the static bodies
/// (<see cref="GridPhysicsBaker2D"/>, <see cref="NavigationGridSource.Physics"/>) or a <see cref="TileMapLayer"/>
/// (<see cref="TileMapLayerBaker"/>, <see cref="NavigationGridSource.TileMapLayer"/>).
/// Bakers run on the main thread and may allocate (edit time, level loads, a tile layer that changed).
/// </summary>
public interface INavigationGridBaker
{
    /// <summary>
    /// Called before the grid lays out for a bake: a source that owns the layout (a tile layer's cell size, position and
    /// used rectangle) sets the node's <see cref="NavigationGrid2D.Region"/>, <see cref="NavigationGrid2D.CellSize"/> and
    /// position here. Default: nothing (the node's own layout).
    /// </summary>
    void PrepareLayout(NavigationGrid2D node)
    {
    }

    /// <summary>
    /// Writes solidity and weights into <paramref name="cells"/> (updated to the node's layout, every cell walkable at
    /// weight 1). Returns false with <paramref name="failure"/> when the source cannot be read (the grid stays all walkable).
    /// </summary>
    bool Bake(NavigationGrid2D node, AStarGrid2D cells, out string? failure);

    /// <summary>
    /// A live source (a tile layer) re-reads itself when it changes: subscribe here and call
    /// <see cref="NavigationGrid2D.MarkSourceChanged"/>, which re-bakes the live grid at the next sync (at most once per
    /// sync, without touching <see cref="NavigationGrid2D.Data"/>). Called when the node enters the tree.
    /// </summary>
    void Attach(NavigationGrid2D node)
    {
    }

    /// <summary>Undoes <see cref="Attach"/>; called when the node leaves the tree or its source changes.</summary>
    void Detach(NavigationGrid2D node)
    {
    }

    /// <summary>A hash of what the bake reads, compared with <see cref="NavigationGridData.SourceHash"/> to tell a stale bake (null: unknown).</summary>
    string? SourceHash(NavigationGrid2D node) => null;
}

/// <summary>
/// The bakers of each <see cref="NavigationGridSource"/>: <see cref="NavigationGridSource.Physics"/> and
/// <see cref="NavigationGridSource.TileMapLayer"/> are built in, and a game may replace them. Register on the main thread
/// before scenes load.
/// </summary>
public static class NavigationGridBakers
{
    private static readonly INavigationGridBaker?[] s_builtIn = [null, new GridPhysicsBaker2D(), new TileMapLayerBaker()];
    private static readonly INavigationGridBaker?[] s_bakers = [.. s_builtIn];

    /// <summary>The baker of <paramref name="source"/>, or null (<see cref="NavigationGridSource.Manual"/>, or one not registered yet).</summary>
    public static INavigationGridBaker? Get(NavigationGridSource source) =>
        (uint)source < (uint)s_bakers.Length ? s_bakers[(int)source] : null;

    /// <summary>Sets the baker of <paramref name="source"/> (not <see cref="NavigationGridSource.Manual"/>).</summary>
    public static void Register(NavigationGridSource source, INavigationGridBaker baker)
    {
        ArgumentNullException.ThrowIfNull(baker);
        if (source == NavigationGridSource.Manual || (uint)source >= (uint)s_bakers.Length)
            throw new ArgumentOutOfRangeException(nameof(source), $"{source} cannot have a baker.");
        s_bakers[(int)source] = baker;
    }

    /// <summary>Puts back the built-in baker of <paramref name="source"/> (tests).</summary>
    internal static void Unregister(NavigationGridSource source)
    {
        if ((uint)source < (uint)s_bakers.Length)
            s_bakers[(int)source] = s_builtIn[(int)source];
    }
}
