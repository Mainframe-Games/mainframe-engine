namespace MainframeEngine.Editor.Tests;

/// <summary>Help › About lists Credits.Entries (the engine's and the third-party sections, BRUTAL's line among them, ADR 0192).</summary>
[Collection(nameof(SerialEditor))]
public sealed class AboutCreditsTests
{
    [Fact]
    public void AboutListsTheCreditsInAScrollingMessage()
    {
        using var editor = new HeadlessEditor();
        editor.Workspace.Commands.Execute("help.about");
        editor.Tick(2);

        var about = editor.Workspace.Message.Current!;
        Assert.Equal("About", about.Title);
        Assert.True(about.Scrollable);
        Assert.Contains(Credits.BrutalNotice, about.Message, StringComparison.Ordinal);
        Assert.Contains("\n\nEngine:\n• Mainframe Engine — ", about.Message, StringComparison.Ordinal);
        Assert.Contains("\n\nThird-party:\n", about.Message, StringComparison.Ordinal);
        Assert.Contains("The FreeType Project", about.Message, StringComparison.Ordinal);
        Assert.True(editor.Workspace.Message.Document.GetElementById("message").IsClassSet("scroll"));
        Assert.Empty(editor.RmlMessages);
        editor.Workspace.Message.Answer(0);
    }

    [Fact]
    public void TheCreditsTextGroupsEntriesBySection()
    {
        var text = EditorCommands.CreditsText([new("Engine", "A", "a notice"), new("Engine", "B", null), new("Studio", "C", null)]);
        Assert.Equal("Credits\n\nEngine:\n• A — a notice\n• B\n\nStudio:\n• C", text);
    }
}
