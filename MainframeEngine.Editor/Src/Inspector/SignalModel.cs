using System.Reflection;
using System.Text;
using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>One signal of the inspected node in the Signals tab, with its editor (persisted) connections.</summary>
public sealed record SignalRow(SignalInfo Signal, string Signature, IReadOnlyList<SignalConnection> Connections)
{
    public string Name => Signal.Name;
}

/// <summary>A method a signal can be connected to: public (or <c>[SignalHandler]</c>), returning void, parameters assignable from the signal's.</summary>
public sealed record SignalHandlerChoice(string Name, string Signature, MethodInfo Method);

/// <summary>
/// The inspector's Signals tab model (Godot's Node dock): the <c>[Signal]</c> events of a node's type with the
/// connections saved in the scene, and the methods of a target node a signal can connect to — the same rule
/// <see cref="Node.Connect"/> applies at run time.
/// </summary>
public static class SignalModel
{
    /// <summary>
    /// The signals of <paramref name="node"/> (base types first) with its persisted outgoing connections that
    /// <paramref name="sceneRoot"/>'s file owns (made in the editor or loaded from that file). Connections made in code
    /// (not <see cref="ConnectFlags.Persist"/>) or by an instanced sub-scene's file are not listed: the scene does not
    /// save them.
    /// </summary>
    public static IReadOnlyList<SignalRow> For(Node node, Node? sceneRoot = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (TypeRegistry.GetNearest(node.GetType()) is not { } info)
            return [];
        var rows = new List<SignalRow>(info.Signals.Count);
        var connections = node.GetSignalConnections();
        foreach (var signal in info.Signals)
        {
            var list = new List<SignalConnection>();
            foreach (var connection in connections)
                if ((connection.Flags & ConnectFlags.Persist) != 0 && string.Equals(connection.Signal, signal.Name, StringComparison.Ordinal) &&
                    (connection.OriginScene is null || ReferenceEquals(connection.OriginScene, sceneRoot)))
                    list.Add(connection);
            rows.Add(new SignalRow(signal, Signature(signal.Name, signal.ParameterTypes), list));
        }

        return rows;
    }

    /// <summary>"BodyEntered(Node)" — a signal or method with its parameter types.</summary>
    public static string Signature(string name, IReadOnlyList<Type> parameters)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(parameters);
        var builder = new StringBuilder(name).Append('(');
        for (var i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
                builder.Append(", ");
            builder.Append(TypeName(parameters[i]));
        }

        return builder.Append(')').ToString();
    }

    private static string TypeName(Type type) => type switch
    {
        _ when type == typeof(int) => "int",
        _ when type == typeof(float) => "float",
        _ when type == typeof(double) => "double",
        _ when type == typeof(bool) => "bool",
        _ when type == typeof(string) => "string",
        _ when type == typeof(long) => "long",
        _ when type == typeof(object) => "object",
        _ => type.Name,
    };

    /// <summary>
    /// The methods of <paramref name="targetType"/> that <paramref name="signal"/> can call: instance methods returning
    /// void, public or marked <see cref="SignalHandlerAttribute"/>, whose parameters accept the signal's arguments.
    /// Property/event accessors and <see cref="object"/>'s members are left out. Sorted by name.
    /// </summary>
    public static IReadOnlyList<SignalHandlerChoice> CompatibleMethods(Type targetType, SignalInfo signal)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        ArgumentNullException.ThrowIfNull(signal);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
        var result = new List<SignalHandlerChoice>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in targetType.GetMethods(flags))
        {
            if (method.ReturnType != typeof(void) || method.IsSpecialName || method.IsGenericMethodDefinition ||
                method.DeclaringType == typeof(object))
                continue;
            if (!method.IsPublic && method.GetCustomAttribute<SignalHandlerAttribute>() is null)
                continue;
            if (!Accepts(method, signal) || !seen.Add(method.Name))
                continue;
            var parameters = method.GetParameters();
            var types = new Type[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
                types[i] = parameters[i].ParameterType;
            result.Add(new SignalHandlerChoice(method.Name, Signature(method.Name, types), method));
        }

        result.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    /// <summary>Whether <paramref name="method"/> can receive <paramref name="signal"/> (the rule of <see cref="Node.Connect"/>).</summary>
    public static bool Accepts(MethodInfo method, SignalInfo signal)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(signal);
        var parameters = method.GetParameters();
        if (parameters.Length != signal.ParameterTypes.Count)
            return false;
        for (var i = 0; i < parameters.Length; i++)
            if (parameters[i].ParameterType.IsByRef || !parameters[i].ParameterType.IsAssignableFrom(signal.ParameterTypes[i]))
                return false;
        return true;
    }

    /// <summary>The handler name Godot would suggest: <c>On{Source}{Signal}</c> ("OnButtonPressed").</summary>
    public static string SuggestedMethodName(Node source, SignalInfo signal)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(signal);
        var builder = new StringBuilder("On");
        foreach (var c in source.Name)
            if (char.IsLetterOrDigit(c) || c == '_')
                builder.Append(c);
        return builder.Append(signal.Name).ToString();
    }
}
