using Microsoft.CodeAnalysis;

namespace MainframeEngine.Generators;

/// <summary>
/// Finds every class deriving from <c>MainframeEngine.Node</c> or <c>MainframeEngine.Resource</c> and emits
/// their <c>NodeTypeInfo</c> registration (see <see cref="RegistrationEmitter"/>), plus replication state and RPC
/// dispatch for networked members (<see cref="ReplicationEmitter"/>). The pipeline is incremental:
/// per-type models are value-equatable, so edits that do not change a type's shape do not regenerate.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class NodeRegistrationGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var types = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => TypeModelBuilder.IsCandidate(node),
                transform: static (ctx, ct) => TypeModelBuilder.Build(ctx, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!);

        var assemblyName = context.CompilationProvider.Select(static (compilation, _) => compilation.AssemblyName ?? "Assembly");

        var collected = types.Collect().Combine(assemblyName);

        context.RegisterSourceOutput(collected, static (spc, input) => RegistrationEmitter.Emit(spc, input.Left, input.Right));

        // Networking ([Replicated], [Rpc]): a second file, only for assemblies that declare networked members.
        context.RegisterSourceOutput(collected, static (spc, input) => ReplicationEmitter.Emit(spc, input.Left, input.Right));
    }
}
