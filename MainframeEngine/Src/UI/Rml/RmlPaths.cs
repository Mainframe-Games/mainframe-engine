namespace MainframeEngine.UI.Rml;

/// <summary>
/// RmlUi's path joining (<c>SystemInterface::JoinPath</c>) with one change: an absolute path to an existing file stays
/// absolute (RmlUi strips the leading <c>/</c>, which breaks absolute image paths on macOS and Linux).
/// </summary>
public static class RmlPaths
{
    public static string Join(string documentPath, string path)
    {
        ArgumentNullException.ThrowIfNull(documentPath);
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
            return path;
        if ((path[0] == '/' || path[0] == '\\') && File.Exists(path))
            return path;
        if (path[0] == '/')
            return path[1..];
        var colon = path.IndexOf(':');
        var slash = path.IndexOfAny(['/', '\\']);
        if (colon >= 0 && (slash < 0 || colon < slash))
            return path;

        var lastSlash = documentPath.Replace('\\', '/').LastIndexOf('/');
        var folder = lastSlash >= 0 ? documentPath[..(lastSlash + 1)].Replace('\\', '/') : "";
        return Normalize(folder + path.Replace('\\', '/'));
    }

    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part is "." or "")
                continue;
            if (part == ".." && parts.Count > 0 && parts[^1] != "..")
                parts.RemoveAt(parts.Count - 1);
            else
                parts.Add(part);
        }

        // A rooted document folder stays rooted (a drive prefix survives as the first segment).
        return (path.StartsWith('/') ? "/" : "") + string.Join('/', parts);
    }
}
