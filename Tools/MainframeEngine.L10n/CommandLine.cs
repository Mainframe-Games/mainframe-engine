namespace MainframeEngine.L10n;

/// <summary>A usage error (exit code 2).</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>Minimal option parsing: <c>--name value</c> (repeatable), <c>-o value</c>, <c>--flag</c> and positionals.</summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public CommandLine(IReadOnlyList<string> args, IReadOnlySet<string> flagNames)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith('-') && arg.Length > 1)
            {
                var name = arg switch
                {
                    "-o" => "--output",
                    _ => arg,
                };
                if (flagNames.Contains(name))
                {
                    _flags.Add(name);
                    continue;
                }

                if (i + 1 >= args.Count)
                    throw new UsageException($"{arg} needs a value");
                if (!_options.TryGetValue(name, out var values))
                    _options[name] = values = [];
                values.Add(args[++i]);
                continue;
            }

            Positionals.Add(arg);
        }
    }

    public List<string> Positionals { get; } = [];

    public bool Flag(string name)
    {
        _used.Add(name);
        return _flags.Contains(name);
    }

    public string? Option(string name)
    {
        _used.Add(name);
        if (!_options.TryGetValue(name, out var values))
            return null;
        if (values.Count > 1)
            throw new UsageException($"{name} given more than once");
        return values[0];
    }

    public string Required(string name) => Option(name) ?? throw new UsageException($"{name} is required");

    /// <summary>All values of a repeatable option; comma-separated values are split.</summary>
    public List<string> Many(string name)
    {
        _used.Add(name);
        var result = new List<string>();
        if (_options.TryGetValue(name, out var values))
        {
            foreach (var value in values)
                result.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return result;
    }

    /// <summary>Fails on options the command did not read (typos).</summary>
    public void EnsureAllUsed()
    {
        foreach (var name in _options.Keys.Concat(_flags))
        {
            if (!_used.Contains(name))
                throw new UsageException($"unknown option {name}");
        }
    }
}
