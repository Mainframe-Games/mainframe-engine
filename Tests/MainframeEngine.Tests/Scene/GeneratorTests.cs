extern alias generators;

using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using generators::MainframeEngine.Generators;
using MainframeEngine.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MainframeEngine.Tests.Scene;

/// <summary>
/// Drives <c>MainframeEngine.Generators</c> with <see cref="CSharpGeneratorDriver"/> over small game-like
/// sources compiled against the engine, checks the emitted registration, compiles and loads it, and checks
/// the diagnostics.
/// </summary>
[Collection(nameof(SerialResources))] // loads assemblies that register types process-wide
public sealed class GeneratorTests
{
    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        return
        [
            .. trusted.Select(p => MetadataReference.CreateFromFile(p)),
            MetadataReference.CreateFromFile(typeof(Node).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Spine.Skeleton).Assembly.Location),
        ];
    }

    private static (Compilation Output, ImmutableArray<Diagnostic> Diagnostics, GeneratorDriverRunResult Result) Run(
        string source, string assemblyName = "GenTests")
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

    private static string Generated(GeneratorDriverRunResult result) =>
        result.Results.Single().GeneratedSources.Single().SourceText.ToString();

    private const string GameSource = """
        using System;
        using System.Collections.Generic;
        using System.Numerics;
        using MainframeEngine;
        using MainframeEngine.Serialization;

        namespace Game;

        public enum Team { Red, Blue }

        [SerializedVersion(2)]
        public class GenPlayer : Node3D
        {
            [Export(Range = "0,20,0.5")] public float Speed { get; set; } = 5;
            [Export] public Team Team { get; set; }
            [Export] public GenStats? Stats { get; set; }
            [Export] public List<int>? Scores { get; set; }
            [ExportGroup("Combat")]
            [Export(File = "*.ogg")] public string Sound { get; set; } = "";
            [Export] public int Health = 100;
            [Export] internal bool Internal { get; set; }

            [Signal] public event Action<int>? Hit;
            [Signal] public event Action? Died;

            public void TakeHit(int damage) => Hit?.Invoke(damage);
            public void Die() => Died?.Invoke();

            [SerializedMigration(1)]
            internal static void FromV1(PropertyBag bag) => bag.Rename("Velocity", "Speed");
        }

        public sealed partial class GenStats : Resource
        {
            [Export] public int Armor { get; set; }
        }

        public sealed partial class GenStats
        {
            [Export] public int Mana { get; set; }
        }

        [Tool, TypeName("Game.Editor.GenGizmo")]
        internal sealed class GenGizmo : Node { }

        public abstract class GenBase : Node { [Export] public int BaseValue { get; set; } }

        public sealed class GenDerived : GenBase { }

        public class GenGeneric<T> : Node { [Export] public int Value { get; set; } }

        public class NotANode { [Export] public int Ignored { get; set; } }
        """;

    [Fact]
    public void EmitsRegistrationThatCompilesWithoutDiagnostics()
    {
        var (output, diagnostics, result) = Run(GameSource);

        Assert.Empty(diagnostics);
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity >= DiagnosticSeverity.Warning));

        var generated = Generated(result);
        Assert.Contains("[global::System.Runtime.CompilerServices.ModuleInitializer]", generated, StringComparison.Ordinal);
        Assert.Contains("internal static class TypeRegistration_GenTests", generated, StringComparison.Ordinal);
        Assert.Contains("name: \"GenPlayer\"", generated, StringComparison.Ordinal);
        Assert.Contains("name: \"GenStats\"", generated, StringComparison.Ordinal);
        Assert.Contains("name: \"Game.Editor.GenGizmo\"", generated, StringComparison.Ordinal);
        Assert.Contains("name: \"GenDerived\"", generated, StringComparison.Ordinal);
        Assert.Contains("name: \"GenBase\"", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("GenGeneric", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("NotANode", generated, StringComparison.Ordinal);
        Assert.Contains("Codecs.EnumOf<global::Game.Team>()", generated, StringComparison.Ordinal);
        Assert.Contains("Codecs.ResourceOf<global::Game.GenStats>()", generated, StringComparison.Ordinal);
        Assert.Contains("Codecs.ListOf(global::MainframeEngine.Serialization.Codecs.Get<int>())", generated, StringComparison.Ordinal);
        Assert.Contains("Range = \"0,20,0.5\"", generated, StringComparison.Ordinal);
        Assert.Contains("\"Combat\"", generated, StringComparison.Ordinal);
        Assert.Contains("static p => global::Game.GenPlayer.FromV1(p)", generated, StringComparison.Ordinal);
        // Partial type: both parts' members, registered once.
        Assert.Equal(1, CountOccurrences(generated, "name: \"GenStats\""));
        Assert.Contains("\"Mana\"", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedRegistrationWorksAtRuntime()
    {
        var (output, _, _) = Run(GameSource, "GenRuntime");
        using var stream = new MemoryStream();
        var emit = output.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emit.Success, string.Join('\n', emit.Diagnostics));

        var context = new AssemblyLoadContext("generator-test", isCollectible: true);
        try
        {
            stream.Position = 0;
            var assembly = context.LoadFromStream(stream);
            TypeRegistry.EnsureRegistered(assembly);

            var player = TypeRegistry.Get("GenPlayer")!;
            Assert.Equal(2, player.Version);
            Assert.Single(player.Migrations);
            Assert.True(player.IsNode);
            Assert.Equal(
                ["Speed", "Team", "Stats", "Scores", "Sound", "Health", "Internal"],
                player.DeclaredProperties.Select(p => p.Name));
            Assert.Equal("Combat", player.FindProperty("Sound")!.Group);
            Assert.Equal("Combat", player.FindProperty("Health")!.Group);
            Assert.Null(player.FindProperty("Speed")!.Group);
            Assert.Equal("*.ogg", player.FindProperty("Sound")!.Hints.File);
            Assert.True(player.FindProperty("Speed")!.Hints.TryGetRange(out var min, out var max, out var step));
            Assert.Equal((0d, 20d, 0.5d), (min, max, step));
            Assert.NotNull(player.FindProperty("Position")); // inherited from Node3D

            var node = (Node)player.CreateInstance();
            var speed = player.FindProperty("Speed")!;
            Assert.Equal(5f, speed.GetValue(node));
            speed.SetValue(node, 9f);
            Assert.Equal(9f, speed.GetValue(node));
            player.FindProperty("Health")!.SetValue(node, 3); // a field
            Assert.Equal(3, player.FindProperty("Health")!.GetValue(node));

            // Signals connect by name through the generated accessors.
            var hits = new List<object?>();
            var hit = player.FindSignal("Hit")!;
            Assert.Equal([typeof(int)], hit.ParameterTypes);
            hit.Add(node, hit.CreateForwarder(args => hits.Add(args[0])));
            node.GetType().GetMethod("TakeHit")!.Invoke(node, [7]);
            Assert.Equal([7], hits);

            var gizmo = TypeRegistry.Get("Game.Editor.GenGizmo")!;
            Assert.True(gizmo.IsTool);
            var abstractBase = TypeRegistry.Get("GenBase")!;
            Assert.True(abstractBase.IsAbstract);
            Assert.Throws<InvalidOperationException>(() => abstractBase.CreateInstance());
            Assert.NotNull(TypeRegistry.Get("GenDerived")!.FindProperty("BaseValue"));
            Assert.True(TypeRegistry.Get("GenStats")!.IsResource);

            node.Free();
            TypeRegistry.UnregisterAssembly(assembly);
            Assert.Null(TypeRegistry.Get("GenPlayer"));
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void EditorIconsFamiliesAndDocSummariesAreRecorded()
    {
        const string source = """
            using MainframeEngine;
            namespace Game;

            /// <summary>
            /// The player: moves with <see cref="Speed"/>, a <c>Node3D</c> with &lt;feelings&gt;.
            /// </summary>
            [EditorIcon("run", Family = EditorIconFamily.Physics)]
            public class IconPlayer : Node3D
            {
                /// <summary>Metres per second.</summary>
                [Export(Icon = "gauge")] public float Speed { get; set; }

                /// <summary>Health <b>points</b>.</summary>
                [EditorIcon("heart")] [Export] public int Health;

                /// <inheritdoc/>
                [Export] public string Note { get; set; } = "";
            }

            public class IconChild : IconPlayer;

            [EditorIcon("", Family = EditorIconFamily.Audio)]
            public class FamilyOnly : Node3D;
            """;
        var (output, diagnostics, result) = Run(source, "GenIcons");
        Assert.Empty(diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning));
        var generated = Generated(result);
        Assert.Contains("icon: \"run\"", generated, StringComparison.Ordinal);
        Assert.Contains("iconFamily: (global::MainframeEngine.EditorIconFamily)6", generated, StringComparison.Ordinal);
        Assert.Contains("description: \"The player: moves with Speed, a Node3D with <feelings>.\"", generated, StringComparison.Ordinal);
        Assert.Contains("Icon = \"gauge\", Description = \"Metres per second.\"", generated, StringComparison.Ordinal);
        Assert.Contains("Icon = \"heart\", Description = \"Health points.\"", generated, StringComparison.Ordinal);

        using var stream = new MemoryStream();
        var emit = output.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emit.Success, string.Join('\n', emit.Diagnostics));
        var context = new AssemblyLoadContext("generator-icons", isCollectible: true);
        try
        {
            stream.Position = 0;
            var assembly = context.LoadFromStream(stream);
            TypeRegistry.EnsureRegistered(assembly);
            var player = TypeRegistry.Get("IconPlayer")!;
            Assert.Equal("run", player.Icon);
            Assert.Equal(EditorIconFamily.Physics, player.IconFamily);
            Assert.Equal("The player: moves with Speed, a Node3D with <feelings>.", player.Description);
            Assert.Equal("gauge", player.FindProperty("Speed")!.Hints.Icon);
            Assert.Equal("Metres per second.", player.FindProperty("Speed")!.Hints.Description);
            Assert.Equal("heart", player.FindProperty("Health")!.Hints.Icon);
            Assert.Null(player.FindProperty("Note")!.Hints.Description); // <inheritdoc/> has no summary
            Assert.Null(TypeRegistry.Get("IconChild")!.Icon); // inherited by the editor, not copied
            var familyOnly = TypeRegistry.Get("FamilyOnly")!;
            Assert.Null(familyOnly.Icon);
            Assert.Equal(EditorIconFamily.Audio, familyOnly.IconFamily);
            TypeRegistry.UnregisterAssembly(assembly);
        }
        finally
        {
            context.Unload();
        }
    }

    [Theory]
    [InlineData("<summary>Plain.</summary>", "Plain.")]
    [InlineData(" <summary>\n Two\n lines.\n </summary>\n <remarks>x</remarks>", "Two lines.")]
    [InlineData("<summary>See <see cref=\"T:MainframeEngine.Node3D\"/> and <see cref=\"Node.Name\"/>.</summary>", "See Node3D and Name.")]
    [InlineData("<summary>A <see langword=\"null\"/> <paramref name=\"x\"/> <c>code</c>.</summary>", "A null x code.")]
    [InlineData("<summary><see cref=\"X\">custom text</see></summary>", "custom text")]
    [InlineData("<remarks>No summary.</remarks>", null)]
    [InlineData("<summary>   </summary>", null)]
    public void DocSummariesBecomePlainText(string xml, string? expected) => Assert.Equal(expected, DocComments.ToPlainText(xml));

    [Fact]
    public void PartialTypesRegisterWhenTheFirstPartHasNoBaseList()
    {
        var compilation = CSharpCompilation.Create(
            "GenPartial",
            [
                CSharpSyntaxTree.ParseText(
                    "public partial class PartialPlayer { [MainframeEngine.Export] public int Ammo { get; set; } }",
                    path: "A.Input.cs", cancellationToken: TestContext.Current.CancellationToken),
                CSharpSyntaxTree.ParseText(
                    "public partial class PartialPlayer : MainframeEngine.Node3D { }",
                    path: "B.cs", cancellationToken: TestContext.Current.CancellationToken),
            ],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new NodeRegistrationGenerator());
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);

        var generated = Generated(driver.GetRunResult());
        Assert.Equal(1, CountOccurrences(generated, "name: \"PartialPlayer\""));
        Assert.Contains("\"Ammo\"", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsInvalidMembers()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using MainframeEngine;
            using MainframeEngine.Serialization;

            public class Bad : Node
            {
                [Export] private int Hidden { get; set; }
                [Export] public int ReadOnly { get; }
                [Export] public int InitOnly { get; init; }
                [Export] public readonly int ReadOnlyField = 1;
                [Export] public Dictionary<string, int>? Map { get; set; }
                [Signal] public static event Action? StaticSignal;
                [SerializedMigration(1)] public void NotStatic(PropertyBag bag) { }
                [SerializedMigration(2)] private static void Private(PropertyBag bag) { }
            }

            public class Twin : Node { }
            namespace Other { public class Twin : MainframeEngine.Node { } }

            public class Outer
            {
                private sealed class Secret : Node { [Export] public int Value { get; set; } }
                private sealed class Silent : Node { }
            }
            """;

        var (_, diagnostics, _) = Run(source);
        var ids = diagnostics.Select(d => d.Id).ToList();

        Assert.Equal(4, ids.Count(id => id == "MFG001"));
        Assert.Contains(diagnostics, d => d.Id == "MFG002" && d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("Map", StringComparison.Ordinal));
        Assert.Single(ids, id => id == "MFG003");
        Assert.Equal(2, ids.Count(id => id == "MFG004")); // both Twins
        Assert.Equal(2, ids.Count(id => id == "MFG005"));
        Assert.Single(ids, id => id == "MFG006"); // Secret (Silent has nothing to register, so it is skipped quietly)
        Assert.All(diagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        // Every diagnostic points at the offending declaration (file and line survive incremental caching).
        Assert.All(diagnostics, d =>
        {
            var span = d.Location.GetLineSpan();
            Assert.Equal("Game.cs", span.Path);
            Assert.True(span.StartLinePosition.Line > 0);
        });
    }

    [Fact]
    public void TranslatableExportsAreTextOnly()
    {
        const string source = """
            using System.Collections.Generic;
            using MainframeEngine;

            namespace Game;

            public class Label : Node
            {
                [Export(Translatable = true)] public string Text { get; set; } = "";
                [Export(Translatable = true)] public string[] Lines { get; set; } = [];
                [Export(Translatable = true)] public List<string>? Items { get; set; }
                [Export(Translatable = true)] public int Count { get; set; }
                [Export(Translatable = true)] public List<int>? Numbers { get; set; }
            }
            """;

        var (_, diagnostics, result) = Run(source, "GenTranslatable");
        var generated = Generated(result);
        Assert.Equal(3, generated.Split("Translatable = true").Length - 1); // Text, Lines, Items
        var errors = diagnostics.Where(d => d.Id == "MFG010").ToList();
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("'Count' has type 'int'", StringComparison.Ordinal));
        Assert.All(errors, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public void UnrelatedEditsDoNotRegenerate()
    {
        var compilation = CSharpCompilation.Create(
            "GenIncremental",
            [CSharpSyntaxTree.ParseText(GameSource, path: "Game.cs", cancellationToken: TestContext.Current.CancellationToken), CSharpSyntaxTree.ParseText("public static class Util { }", path: "Util.cs", cancellationToken: TestContext.Current.CancellationToken)],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new NodeRegistrationGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
        var first = Generated(driver.GetRunResult());

        // Edit a file that declares no nodes.
        var utilTree = compilation.SyntaxTrees.Single(t => t.FilePath == "Util.cs");
        var edited = compilation.ReplaceSyntaxTree(utilTree, CSharpSyntaxTree.ParseText("public static class Util { public static int X; }", path: "Util.cs", cancellationToken: TestContext.Current.CancellationToken));
        driver = driver.RunGenerators(edited, TestContext.Current.CancellationToken);
        var result = driver.GetRunResult().Results.Single();

        Assert.Equal(first, result.GeneratedSources.Single().SourceText.ToString());
        var outputs = result.TrackedOutputSteps.SelectMany(s => s.Value).SelectMany(step => step.Outputs);
        Assert.All(outputs, o => Assert.True(o.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged, o.Reason.ToString()));
    }

    [Fact]
    public void EngineTypesAreRegisteredWithTheirExports()
    {
        foreach (var name in new[] { "Node", "Node3D", "Node2D", "Camera3D", "Camera2D", "DirectionalLight3D", "OmniLight3D",
                     "SpotLight3D", "WorldEnvironment", "Sky", "MeshInstance3D", "Sprite3D", "BoxMesh", "StandardMaterial3D", "Texture2D", "SubViewport", "SpineNode", "Grid3D", "Timer", "PackedScene" })
            Assert.NotNull(TypeRegistry.Get(name));

        var node3D = TypeRegistry.Get(typeof(Node3D))!;
        Assert.Equal(["Position", "RotationDegrees", "Scale", "Visible"], node3D.DeclaredProperties.Select(p => p.Name));
        // Base members first; within a type, declaration order (partial files by path: Node.Groups.cs, Node.Localization.cs,
        // Node.Processing.cs).
        Assert.Equal(["UniqueNameInOwner", "AutoTranslateMode", "ProcessMode", "ProcessPriority", "Position", "RotationDegrees", "Scale", "Visible"],
            node3D.Properties.Select(p => p.Name));
        Assert.Same(TypeRegistry.Get(typeof(Node)), node3D.Base);
        Assert.Equal(["TreeEntered", "Ready", "TreeExiting", "TreeExited", "Renamed", "ChildEnteredTree", "ChildExitingTree"],
            TypeRegistry.Get(typeof(Node))!.DeclaredSignals.Select(s => s.Name));
        Assert.Equal("0,16,0.01", TypeRegistry.Get("DirectionalLight3D")!.FindProperty("Energy")!.Hints.Range);
        Assert.Equal(["Procedural", "Sun", "Physical"], TypeRegistry.Get("Sky")!.DeclaredProperties.Select(p => p.Group).OfType<string>().Distinct());
        Assert.IsType<Camera3D>(TypeRegistry.CreateNode("Camera3D"));
        Assert.Throws<InvalidOperationException>(() => TypeRegistry.CreateNode("Sky"));

        Assert.True(TypeRegistry.Get(typeof(ToolNode))!.IsTool);
        Assert.Equal("Custom.Renamed", TypeRegistry.Get(typeof(RenamedTypeNode))!.Name);
        Assert.Null(TypeRegistry.Get(typeof(TestFlags)));
        // Types the generator cannot register (inside a generic type) fall back to their nearest registered base.
        Assert.Null(TypeRegistry.Get(typeof(Holder<int>.NotRegistered3D)));
        Assert.Same(TypeRegistry.Get(typeof(Node3D)), TypeRegistry.GetNearest(typeof(Holder<int>.NotRegistered3D)));
    }

    [Fact]
    public void DefaultInstancesExposeTypeDefaults()
    {
        var info = TypeRegistry.Get(typeof(AllTypesNode))!;
        var defaults = (AllTypesNode)info.DefaultInstance!;
        Assert.Equal(7, defaults.Int);
        Assert.Equal("default", defaults.Text);
        Assert.Same(defaults, info.DefaultInstance);
        Assert.False(defaults.IsInsideTree);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }
}

/// <summary>A node type the generator skips: generic contexts cannot be registered.</summary>
internal static class Holder<T>
{
    internal sealed class NotRegistered3D : Node3D;
}
