using MainframeEngine.Serialization;

namespace MainframeEngine.Tests.Scene;

public sealed class SignalTests
{
    [Fact]
    public void GeneratedInfoListsSignalsIncludingInheritedOnes()
    {
        var info = TypeRegistry.Get(typeof(SignalNode))!;
        Assert.Equal(["Pinged", "Counted", "Named"], info.DeclaredSignals.Select(s => s.Name));
        Assert.Contains(info.Signals, s => s.Name == "TreeEntered"); // from Node
        var named = info.FindSignal("Named")!;
        Assert.Equal([typeof(string), typeof(Node)], named.ParameterTypes);
        Assert.Equal(typeof(Action<string, Node>), named.DelegateType);
    }

    [Fact]
    public void ConnectByNameInvokesTheMethodAndDisconnectStopsIt()
    {
        using var source = new SignalNode { Name = "Source" };
        using var target = new SignalNode { Name = "Target" };

        source.Connect("Pinged", target, nameof(SignalNode.OnPinged));
        source.Connect("Counted", target, nameof(SignalNode.OnCounted));
        source.Connect("Named", target, nameof(SignalNode.OnNamed));
        Assert.True(source.IsConnected("Pinged", target, nameof(SignalNode.OnPinged)));

        source.Ping();
        source.Count(3);
        source.EmitNamed("x", source);
        Assert.Equal(["pinged", "counted 3", "named x Source"], target.Received);

        Assert.True(source.Disconnect("Pinged", target, nameof(SignalNode.OnPinged)));
        Assert.False(source.Disconnect("Pinged", target, nameof(SignalNode.OnPinged)));
        source.Ping();
        Assert.Equal(3, target.Received.Count);
        Assert.Equal(2, source.GetSignalConnections().Count);
        Assert.Equal(2, target.GetIncomingConnections().Count);
    }

    [Fact]
    public void ConnectAcceptsSignalHandlerMethodsButNotOtherPrivateOnes()
    {
        using var source = new SignalNode();
        using var target = new SignalNode();

        source.Connect("Pinged", target, "PrivateHandler");
        source.Ping();
        Assert.Equal(["private"], target.Received);

        Assert.Throws<ArgumentException>(() => source.Connect("Pinged", target, "Hidden"));
        Assert.Throws<ArgumentException>(() => source.Connect("Pinged", target, nameof(SignalNode.OnCounted))); // wrong shape
        Assert.Throws<ArgumentException>(() => source.Connect("NoSuchSignal", target, nameof(SignalNode.OnPinged)));
        Assert.Throws<InvalidOperationException>(() => source.Connect("Pinged", target, "PrivateHandler")); // twice
    }

    [Fact]
    public void BuiltInSignalsConnectByName()
    {
        var tree = new SceneTree();
        var source = new SignalNode();
        var target = new SignalNode();
        tree.Root.AddChild(target);
        source.Connect("Ready", target, nameof(SignalNode.OnPinged));
        tree.Root.AddChild(source);
        Assert.Equal(["pinged"], target.Received);
        tree.Shutdown();
    }

    [Fact]
    public void OneShotConnectionsDisconnectAfterTheFirstEmission()
    {
        using var source = new SignalNode();
        using var target = new SignalNode();
        source.Connect("Counted", target, nameof(SignalNode.OnCounted), ConnectFlags.OneShot);

        source.Count(1);
        source.Count(2);

        Assert.Equal(["counted 1"], target.Received);
        Assert.False(source.IsConnected("Counted", target, nameof(SignalNode.OnCounted)));
    }

    [Fact]
    public void DeferredConnectionsRunAtTheEndOfTheFrame()
    {
        var tree = new SceneTree();
        var source = new SignalNode();
        var target = new SignalNode();
        tree.Root.AddChild(source);
        tree.Root.AddChild(target);
        source.Connect("Counted", target, nameof(SignalNode.OnCounted), ConnectFlags.Deferred);

        source.Count(5);
        Assert.Empty(target.Received);
        tree.FlushDeferred();
        Assert.Equal(["counted 5"], target.Received);
        tree.Shutdown();
    }

    [Fact]
    public void FreeingEitherEndRemovesTheConnection()
    {
        var source = new SignalNode();
        var target = new SignalNode();
        source.Connect("Pinged", target, nameof(SignalNode.OnPinged));

        target.Free();
        Assert.Empty(source.GetSignalConnections());
        source.Ping(); // no call into the freed node

        var other = new SignalNode();
        source.Connect("Pinged", other, nameof(SignalNode.OnPinged));
        source.Free();
        Assert.Empty(other.GetIncomingConnections());
        other.Free();
    }

    [Fact]
    public void CodeConnectionsAreOrdinaryEvents()
    {
        using var source = new SignalNode();
        var count = 0;
        source.Counted += n => count += n;
        source.Count(2);
        source.Count(3);
        Assert.Equal(5, count);
        Assert.Empty(source.GetSignalConnections()); // not tracked, never saved
    }
}
