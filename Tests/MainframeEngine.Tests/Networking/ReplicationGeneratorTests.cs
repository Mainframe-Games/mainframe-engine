extern alias generators;

using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.Loader;
using generators::MainframeEngine.Generators;
using MainframeEngine.Networking;
using MainframeEngine.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MainframeEngine.Tests.Networking;

/// <summary>
/// The <c>[Replicated]</c>/<c>[Rpc]</c> half of <c>MainframeEngine.Generators</c>: emitted state, dispatch and senders
/// compile, register and work when loaded; invalid members are reported.
/// </summary>
[Collection(nameof(Scene.SerialResources))] // loads assemblies that register types process-wide
public sealed class ReplicationGeneratorTests
{
    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)),
        MetadataReference.CreateFromFile(typeof(Node).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Spine.Skeleton).Assembly.Location),
    ];

    private static (Compilation Output, ImmutableArray<Diagnostic> Diagnostics, GeneratorDriverRunResult Result) Run(string source, string assemblyName)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, path: "Game.cs", cancellationToken: TestContext.Current.CancellationToken)],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new NodeRegistrationGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics, TestContext.Current.CancellationToken);
        return (output, diagnostics, driver.GetRunResult());
    }

    private const string GameSource = """
        using System;
        using System.Numerics;
        using MainframeEngine;
        using MainframeEngine.Networking;

        namespace Game.Net;

        public enum Stance : short { Stand, Crouch }

        public struct Stats : INetworkTransferable, IEquatable<Stats>
        {
            public int Level;
            public void NetworkWrite(NetBufferWriter w) => w.Write(Level);
            public void NetworkRead(NetBufferReader r) => Level = r.ReadInt32();
            public bool Equals(Stats other) => Level == other.Level;
        }

        public class GenShip : Node3D
        {
            [Replicated(Interpolate = true)] public Vector3 Velocity { get; set; }
            [Replicated] public Stance Stance { get; set; }
            [Replicated] public Stats Stats { get; set; }
            [Replicated] public string Callsign = "";
            [Export, Replicated] public int Fuel { get; set; } = 10;

            public string Log = "";

            [Rpc] public void Fire(Vector3 direction, Stance stance) => Log += $"fire {direction} {stance};";
            [Rpc(RpcMode.Server, Reliable = false)] internal void Explode(Stats stats) => Log += $"boom {stats.Level};";
            [Rpc(RpcMode.AnyPeer, CallLocal = true)] public void Hail(string text) => Log += $"hail {text};";
        }

        public sealed class GenCarrier : GenShip
        {
            [Replicated] public byte Hangars { get; set; }
            [Rpc] public void Launch() => Log += "launch;";
        }

        public class Outer
        {
            public sealed class GenNested : Node { [Rpc] public void Ping() { } }
        }

        internal sealed class GenInternal : Node { [Rpc] public void Secret(int value) { } }

        public class GenPlain : Node { }
        """;

    [Fact]
    public void EmitsReplicationThatCompilesWithoutDiagnostics()
    {
        var (output, diagnostics, result) = Run(GameSource, "NetGen");
        Assert.Empty(diagnostics);
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity >= DiagnosticSeverity.Warning));

        var sources = result.Results.Single().GeneratedSources;
        Assert.Equal(2, sources.Length); // registration + replication
        var generated = sources.Single(s => s.HintName == "MainframeEngine.Replication.g.cs").SourceText.ToString();
        Assert.Contains("ReplicationRegistration_NetGen", generated, StringComparison.Ordinal);
        Assert.Contains("class Replication_Game_Net_GenShip : global::MainframeEngine.Networking.ReplicatedState", generated, StringComparison.Ordinal);
        Assert.Contains("InterpolationBuffer<global::System.Numerics.Vector3> _b0", generated, StringComparison.Ordinal);
        Assert.Contains("NetCodec.Write(writer, (short)_v1)", generated, StringComparison.Ordinal);       // enum → underlying
        Assert.Contains("NetCodec.WriteValue(writer, in _v2)", generated, StringComparison.Ordinal);      // transferable
        Assert.Contains("public static class GenShipRpcExtensions", generated, StringComparison.Ordinal);
        Assert.Contains("public static void RpcFire(this global::Game.Net.GenShip __mfNode, global::System.Numerics.Vector3 direction", generated, StringComparison.Ordinal);
        Assert.Contains("internal static void RpcExplode(", generated, StringComparison.Ordinal);       // internal method → internal sender
        Assert.Contains("public static class Outer_GenNestedRpcExtensions", generated, StringComparison.Ordinal);
        Assert.Contains("internal static class GenInternalRpcExtensions", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("GenPlain", generated, StringComparison.Ordinal);
        Assert.Contains("createState: null", generated, StringComparison.Ordinal);                         // RPC-only types
    }

    [Fact]
    public void RpcParametersMayUseAnyNameTheGeneratedCodeUses()
    {
        // Regression: the generated senders once declared locals named node/peer/rpc/call/writer/previous.
        const string source = """
            using MainframeEngine;
            using MainframeEngine.Networking;

            public class Names : Node
            {
                [Rpc] public void All(int node, int peer, int rpc, int call, int writer, int previous, int reader, int o, int v, int a0) { }
                [Rpc] public void Keyword(int @class, string @event) { }
                [Rpc] public void Reserved(int __mfNode) { }
            }

            public class Global : Node { [Replicated] public int @int; [Rpc] public void Ping(PeerId peer) { } }
            """;
        var (output, diagnostics, _) = Run(source, "NetNames");
        var reserved = Assert.Single(diagnostics);
        Assert.Equal("MFG008", reserved.Id);
        Assert.Contains("__mf", reserved.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity >= DiagnosticSeverity.Warning));
    }

    [Fact]
    public void AssembliesWithoutNetworkedMembersGetNoReplicationFile()
    {
        var (_, _, result) = Run("public class Quiet : MainframeEngine.Node { [MainframeEngine.Export] public int A { get; set; } }", "NetQuiet");
        Assert.Single(result.Results.Single().GeneratedSources);
    }

    [Fact]
    public void GeneratedReplicationWorksAtRuntime()
    {
        var (output, _, _) = Run(GameSource, "NetGenRuntime");
        using var stream = new MemoryStream();
        var emit = output.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emit.Success, string.Join('\n', emit.Diagnostics));

        var context = new AssemblyLoadContext("net-generator-test", isCollectible: true);
        try
        {
            stream.Position = 0;
            var assembly = context.LoadFromStream(stream);
            TypeRegistry.EnsureRegistered(assembly);
            var shipType = assembly.GetType("Game.Net.GenShip")!;
            var carrierType = assembly.GetType("Game.Net.GenCarrier")!;

            var ship = ReplicationRegistry.Get(shipType)!;
            Assert.Equal(["Velocity", "Stance", "Stats", "Callsign", "Fuel"], ship.Properties.Select(p => p.Name));
            Assert.True(ship.Properties[0].Interpolate);
            Assert.Equal(typeof(Vector3), ship.Properties[0].ValueType);
            Assert.Equal(["Fire", "Explode", "Hail"], ship.Rpcs.Select(r => r.Name));
            Assert.Equal([RpcMode.Authority, RpcMode.Server, RpcMode.AnyPeer], ship.Rpcs.Select(r => r.Mode));
            Assert.False(ship.Rpcs[1].Reliable);
            Assert.True(ship.Rpcs[2].CallLocal);
            var carrier = ReplicationRegistry.Get(carrierType)!;
            Assert.Same(ship, carrier.Base);
            Assert.Equal(3, carrier.Rpcs[0].WireIndex);
            Assert.NotEqual(ship.SchemaHash, carrier.SchemaHash);
            Assert.NotNull(TypeRegistry.Get("GenShip")!.FindProperty("Fuel")); // [Export] and [Replicated] together

            // A state round trip through the generated code: capture on one node, apply to another.
            var source = (Node)Activator.CreateInstance(carrierType)!;
            var target = (Node)Activator.CreateInstance(carrierType)!;
            shipType.GetProperty("Velocity")!.SetValue(source, new Vector3(1, 2, 3));
            shipType.GetProperty("Fuel")!.SetValue(source, 4);
            shipType.GetField("Callsign")!.SetValue(source, "Red Five");
            carrierType.GetProperty("Hangars")!.SetValue(source, (byte)2);

            var chain = ReplicationRegistry.GetChain(carrierType)!;
            var sourceStates = chain.CreateStates();
            var targetStates = chain.CreateStates();
            Assert.Equal(2, sourceStates.Length);
            using var writer = new NetBufferWriter();
            foreach (var state in sourceStates)
            {
                state.Capture(source, 5);
                var mask = state.ChangedSince(4);
                writer.WriteVarUInt64(mask);
                state.Write(writer, mask);
            }

            Assert.Equal(0b11001UL, sourceStates[0].ChangedSince(4)); // Velocity, Callsign, Fuel (Stance/Stats default)
            Assert.Equal(0UL, sourceStates[0].ChangedSince(5));
            using var reader = new NetBufferReader(writer.WrittenSpan);
            foreach (var state in targetStates)
                state.Read(reader, target, reader.ReadVarUInt64(), new ReplicationReadContext(5, 5, ApplyDirectly: true));
            Assert.Equal(0, reader.Remaining);
            Assert.Equal(new Vector3(1, 2, 3), shipType.GetProperty("Velocity")!.GetValue(target));
            Assert.Equal(4, shipType.GetProperty("Fuel")!.GetValue(target));
            Assert.Equal("Red Five", shipType.GetField("Callsign")!.GetValue(target));
            Assert.Equal((byte)2, carrierType.GetProperty("Hangars")!.GetValue(target));

            // RPC dispatch decodes typed arguments.
            using var args = new NetBufferWriter();
            NetCodec.Write(args, new Vector3(0, 1, 0));
            NetCodec.Write(args, (short)1);
            using var argsReader = new NetBufferReader(args.WrittenSpan);
            chain.GetRpc(0)!.Dispatch(target, argsReader);
            Assert.Equal("fire <0, 1, 0> Crouch;", shipType.GetField("Log")!.GetValue(target));

            source.Free();
            target.Free();
            ReplicationRegistry.UnregisterAssembly(assembly);
            TypeRegistry.UnregisterAssembly(assembly);
            Assert.Null(ReplicationRegistry.Get(shipType));
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void ReportsInvalidNetworkedMembers()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using MainframeEngine;
            using MainframeEngine.Networking;

            public struct NotEquatable : INetworkTransferable
            {
                public void NetworkWrite(NetBufferWriter w) { }
                public void NetworkRead(NetBufferReader r) { }
            }

            public class BadNet : Node
            {
                [Replicated] private int Hidden { get; set; }
                [Replicated] public int ReadOnly { get; }
                [Replicated] public readonly int Field = 1;
                [Replicated] public List<int>? List { get; set; }
                [Replicated] public NotEquatable Custom { get; set; }
                [Replicated(Interpolate = true)] public int Integer { get; set; }

                [Rpc] public int Returns() => 0;
                [Rpc] public static void Static() { }
                [Rpc] private void Private() { }
                [Rpc] public void Generic<T>() { }
                [Rpc] public void ByRef(ref int value) { }
                [Rpc] public void Unsupported(object value) { }
                [Rpc] public void TransferableArgOk(NotEquatable value) { }
            }

            public class Huge : Node
            {
            #pragma warning disable
            HUGE_MEMBERS
            }

            public class Outer
            {
                private sealed class Hidden : Node { [Rpc] public void Ping() { } }
            }
            """;
        var huge = string.Join('\n', Enumerable.Range(0, 65).Select(i => $"[Replicated] public int F{i};"));
        var (_, diagnostics, _) = Run(source.Replace("HUGE_MEMBERS", huge, StringComparison.Ordinal), "NetBad");
        var ids = diagnostics.Select(d => d.Id).ToList();

        Assert.Equal(6, ids.Count(id => id == "MFG007"));
        Assert.Equal(6, ids.Count(id => id == "MFG008")); // the transferable argument is fine without IEquatable
        Assert.Single(ids, id => id == "MFG009");
        Assert.Single(ids, id => id == "MFG006");
        Assert.All(diagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.Contains(diagnostics, d => d.Id == "MFG007" && d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("IEquatable", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Id == "MFG007" && d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("cannot be interpolated", StringComparison.Ordinal));
    }

    // Registers a type process-wide: lives in this serial collection.
    [Fact]
    public void TheHandshakeFingerprintCoversOnlyTheSpawnableScenes()
    {
        using var net = new NetHarness();
        var before = net.Server.ReplicationFingerprint;
        // A networked type from an unrelated assembly or tool registering on one side only must not matter…
        ReplicationRegistry.Register(new ReplicationTypeInfo(typeof(UnrelatedNetNode), 0xBADu, [], [], null));
        var client = net.Join();
        Assert.Equal(before, client.Api.ReplicationFingerprint);
        // …but the scenes' networked layout does: a scene whose root differs changes it.
        var other = NetHarness.CreateTree();
        using var api = MultiplayerApi.Attach(other);
        api.RegisterScene(NetHarness.PlayerScene, NetHarness.PlayerUid);
        api.StartServer(LoopbackTransport.CreateServer());
        Assert.NotEqual(before, api.ReplicationFingerprint);
        api.Stop();
        other.Shutdown();
    }

    private sealed class UnrelatedNetNode : Node;

    [Fact]
    public void TheFingerprintCoversEveryRegisteredSchema()
    {
        var fingerprint = ReplicationRegistry.Fingerprint;
        Assert.Equal(fingerprint, ReplicationRegistry.Fingerprint);
        Assert.Contains(ReplicationRegistry.All, info => info.Type == typeof(NetPlayer));
        Assert.Null(ReplicationRegistry.GetChain(typeof(Node3D)));
        Assert.Null(ReplicationRegistry.GetNearest(typeof(Node3D)));
        Assert.Equal(2, ReplicationRegistry.GetChain(typeof(NetBoss))!.Infos.Length);
    }
}
