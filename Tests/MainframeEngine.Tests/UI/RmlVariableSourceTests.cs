using MainframeEngine.UI.Rml;

namespace MainframeEngine.Tests.UI;

/// <summary><see cref="RmlDataModel.BindVariable"/>: a variable whose structure a <see cref="RmlVariableSource"/> decides as RmlUi walks it.</summary>
[Collection(nameof(SerialRmlUi))]
public sealed class RmlVariableSourceTests
{
    // root (struct) → "name" (scalar, writable), "items" (array of 2 scalars "item 1", "item 2").
    private sealed class PlayerSource : RmlVariableSource
    {
        private const ulong Name = 1, Items = 2, FirstItem = 10;

        public string PlayerName { get; private set; } = "Ada";

        public override RmlVariableKind? Child(ulong node, int index, ReadOnlySpan<byte> name, out ulong child)
        {
            child = 0;
            if (node == 0 && name.SequenceEqual("name"u8))
            {
                child = Name;
                return RmlVariableKind.Scalar;
            }

            if (node == 0 && name.SequenceEqual("items"u8))
            {
                child = Items;
                return RmlVariableKind.Array;
            }

            if (node == Items && index is >= 0 and < 2)
            {
                child = FirstItem + (ulong)index;
                return RmlVariableKind.Scalar;
            }

            return null;
        }

        public override int Size(ulong node) => node == Items ? 2 : 0;

        public override bool Read(ulong node, RmlVariant value)
        {
            if (node == Name)
                value.Set(PlayerName);
            else if (node >= FirstItem)
                value.Set($"item {node - FirstItem + 1}");
            else
                return false;
            return true;
        }

        public override bool Write(ulong node, RmlVariant value)
        {
            if (node != Name)
                return false;
            PlayerName = value.GetString();
            return true;
        }
    }

    [Fact]
    public void ASourceDrivesMembersListsAndTwoWayBindings()
    {
        using var host = new RmlTestHost();
        var source = new PlayerSource();
        var model = host.Context.CreateDataModel("game").BindVariable("player", RmlVariableKind.Struct, source);
        var doc = host.Show(RmlTestHost.Page("""
            <div data-model="game">
              <p id="name">{{ player.name }}</p>
              <div id="items"><span data-for="i : player.items">{{ i }};</span></div>
              <p id="size">{{ player.items.size }}</p>
              <input id="field" type="text" data-value="player.name"/>
            </div>
            """));

        Assert.Contains("Ada", doc.GetElementById("name").InnerRml, StringComparison.Ordinal);
        Assert.Contains("item 1;", doc.GetElementById("items").InnerRml, StringComparison.Ordinal);
        Assert.Contains("item 2;", doc.GetElementById("items").InnerRml, StringComparison.Ordinal);
        Assert.Contains("2", doc.GetElementById("size").InnerRml, StringComparison.Ordinal);

        var field = doc.GetElementById("field");
        field.Focus();
        host.Context.ProcessKeyDown(RmlKey.End, RmlKeyModifiers.None);
        host.Context.ProcessTextInput("!");
        host.Frame();
        Assert.Equal("Ada!", source.PlayerName);
        model.Dirty("player");
        host.Frame();
        Assert.Contains("Ada!", doc.GetElementById("name").InnerRml, StringComparison.Ordinal);
        host.AssertNoRmlErrors();

        Assert.Throws<ArgumentOutOfRangeException>(() => model.BindVariable("bad", (RmlVariableKind)7, source));
    }
}
