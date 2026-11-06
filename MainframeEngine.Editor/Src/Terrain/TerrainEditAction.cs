namespace MainframeEngine.Editor;

/// <summary>
/// One terrain stroke, ramp or rectangle fill as a history entry: the chunk tiles <see cref="TerrainEdit"/> recorded
/// before and after it. Undo and redo write them back (meshes and collision rebuild, <see cref="Terrain3D.Changed"/> is
/// raised with <c>FromUndo</c>, so foliage follows). It references the terrain node, not the tool, so Cmd+Z works after
/// deselecting.
/// </summary>
public sealed class TerrainEditAction(Terrain3D terrain, TerrainEditRecord record, string name) : IEditorAction
{
    public Terrain3D Terrain { get; } = terrain ?? throw new ArgumentNullException(nameof(terrain));

    public TerrainEditRecord Record { get; } = record ?? throw new ArgumentNullException(nameof(record));

    public string Name { get; } = name;

    /// <summary>Bytes the recorded tiles hold (before and after).</summary>
    public long Bytes
    {
        get
        {
            long bytes = 0;
            foreach (var tile in Record.Tiles)
                bytes += 4L * ((tile.BeforeHeights?.Length ?? 0) + (tile.AfterHeights?.Length ?? 0) + (tile.Before?.Length ?? 0) + (tile.After?.Length ?? 0));
            return bytes;
        }
    }

    public void Do() => Record.Redo();

    public void Undo() => Record.Undo();
}
