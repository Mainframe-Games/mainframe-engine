namespace MainframeEngine.Tests.Saves;

/// <summary>Saves written before ADR 0249 (<c>slot.msave</c>) are renamed to <c>slot.mfsave</c> when the server starts.</summary>
public sealed class LegacySaveFileTests : SaveTestBase
{
    // A save with backups, written by a server and then given the names an older build used.
    private void WriteLegacySlot(string slot, string name)
    {
        using (var server = CreateServer(new SaveOptions { GameName = "Test Game", GameVersion = "1.2.0", Backups = 2 }))
        {
            Assert.True(Pump(server, server.SaveAsync(slot, Data(name + "-1"), Schema)).IsOk);
            Assert.True(Pump(server, server.SaveAsync(slot, Data(name), Schema)).IsOk);
        }

        foreach (var file in Directory.GetFiles(Root, slot + ".mfsave*"))
            File.Move(file, file.Replace(".mfsave", ".msave", StringComparison.Ordinal));
    }

    [Fact]
    public void ALegacySlotIsListedAndLoadsAfterTheRename()
    {
        WriteLegacySlot("slot1", "old");

        using var server = CreateServer();

        Assert.True(File.Exists(PathOf("slot1.mfsave")));
        Assert.True(File.Exists(PathOf("slot1.mfsave.bak1")));
        Assert.Empty(Directory.GetFiles(Root, "*.msave*"));
        Assert.Equal(["slot1"], server.ListSlots().Select(s => s.Slot));
        var loaded = Pump(server, server.LoadAsync("slot1", Schema));
        Assert.Equal("old", loaded.Data!.Name);
    }

    [Fact]
    public void ANewerSlotWithTheSameNameIsNotOverwritten()
    {
        WriteLegacySlot("slot1", "old");
        using (var server = CreateServer())
            Assert.True(Pump(server, server.SaveAsync("slot1", Data("new"), Schema)).IsOk);
        File.WriteAllText(PathOf("slot1.msave"), "stale copy from an old build");

        using var again = CreateServer();

        Assert.True(File.Exists(PathOf("slot1.msave"))); // left alone: its new name is taken
        Assert.Equal("new", Pump(again, again.LoadAsync("slot1", Schema)).Data!.Name);
    }

    [Fact]
    public void FilesThatAreNotSlotsKeepTheirNames()
    {
        File.WriteAllText(PathOf("notes.msavegame"), "x");
        File.WriteAllText(PathOf("Bad Name.msave"), "x");

        using var server = CreateServer();

        Assert.True(File.Exists(PathOf("notes.msavegame")));
        Assert.True(File.Exists(PathOf("Bad Name.msave")));
    }
}
