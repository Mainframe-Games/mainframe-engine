using System.Text.Json;

namespace MainframeEngine.Serialization;

/// <summary>
/// An <see cref="AnimationMethodAttribute"/> method as the generator registers it (G1.4): its name, parameters (each
/// with a parser from a JSON argument) and a typed invoker. Method tracks bind keys to it.
/// </summary>
public sealed class AnimationMethodInfo
{
    public AnimationMethodInfo(string name, AnimationMethodParameter[] parameters, Action<object, object?[]> invoke)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        _invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
    }

    private readonly Action<object, object?[]> _invoke;

    public string Name { get; }

    public IReadOnlyList<AnimationMethodParameter> Parameters { get; }

    /// <summary>The type that declares the method.</summary>
    public NodeTypeInfo DeclaringType { get; internal set; } = null!;

    /// <summary>Calls the method on <paramref name="target"/> with <paramref name="arguments"/> (one per parameter, already parsed).</summary>
    public void Invoke(object target, object?[] arguments) => _invoke(target, arguments);

    /// <summary>
    /// Parses a key's arguments (a JSON array, one element per parameter; missing trailing ones are an error) into an
    /// array for <see cref="Invoke"/>. Throws <see cref="FormatException"/> with the reason.
    /// </summary>
    public object?[] ParseArguments(string json)
    {
        var parameters = Parameters;
        var result = new object?[parameters.Count];
        if (string.IsNullOrWhiteSpace(json))
            json = "[]";
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new FormatException($"{Name}: the arguments must be a JSON array, got {document.RootElement.ValueKind}.");
        var count = document.RootElement.GetArrayLength();
        if (count != parameters.Count)
            throw new FormatException($"{Name} takes {parameters.Count} argument(s), the key has {count}.");
        var i = 0;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            try
            {
                result[i] = parameters[i].Parse(element);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or NotSupportedException)
            {
                throw new FormatException($"{Name}: argument '{parameters[i].Name}' ({parameters[i].Type.Name}): {e.Message}", e);
            }

            i++;
        }

        return result;
    }

    /// <summary>The context generated parameter parsers read with (resources cannot be arguments).</summary>
    public static DeserializationContext ArgumentContext { get; } = new NoResources();

    private sealed class NoResources : DeserializationContext
    {
        public override Resource GetResource(string key) =>
            throw new NotSupportedException("Animation method arguments cannot reference resources.");
    }
}

/// <summary>A parameter of an <see cref="AnimationMethodInfo"/>.</summary>
public sealed class AnimationMethodParameter(string name, Type type, Func<JsonElement, object?> parse)
{
    public string Name { get; } = name;

    public Type Type { get; } = type;

    public object? Parse(JsonElement element) => parse(element);
}
