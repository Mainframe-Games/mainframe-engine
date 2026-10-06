using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests;

public sealed class PlayShortcutTests
{
    [Theory]
    [InlineData(Key.F5, EditorModifiers.None, "play.main")]
    [InlineData(Key.F5, EditorModifiers.Shift, "play.another")]
    [InlineData(Key.F5, EditorModifiers.Command, "play.instances")]
    [InlineData(Key.F6, EditorModifiers.None, "play.scene")]
    [InlineData(Key.B, EditorModifiers.Command | EditorModifiers.Shift, "project.build_reload")]
    [InlineData(Key.F5, EditorModifiers.Alt, null)]
    public void RunShortcutsMapToTheirCommands(Key key, EditorModifiers modifiers, string? command) =>
        Assert.Equal(command, EditorWorkspace.ProjectShortcutFor(key, modifiers));
}
