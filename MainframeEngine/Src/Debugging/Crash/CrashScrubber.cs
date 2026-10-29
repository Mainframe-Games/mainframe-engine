namespace MainframeEngine;

/// <summary>
/// Removes the player's identity from report text: the home folder becomes <c>~</c> and the user name <c>&lt;user&gt;</c>
/// (local builds put absolute source paths into frames; logs name files in the home folder). The machine name is never
/// collected.
/// </summary>
internal sealed class CrashScrubber
{
    private readonly string? _home;
    private readonly string? _altHome;
    private readonly string? _user;

    public CrashScrubber(string? home, string? user)
    {
        _home = string.IsNullOrEmpty(home) ? null : home.TrimEnd('/', '\\');
        if (_home is { Length: 0 })
            _home = null;
        // Windows paths also show up with forward slashes (CallerFilePath under some tools).
        _altHome = _home is not null && _home.Contains('\\', StringComparison.Ordinal) ? _home.Replace('\\', '/') : null;
        // Short names ("a", "pi") would scrub ordinary words: only names of three or more characters.
        _user = string.IsNullOrEmpty(user) || user.Length < 3 ? null : user;
    }

    /// <summary>The scrubber for this machine's home folder and user.</summary>
    public static CrashScrubber ForThisMachine()
    {
        string? home = null;
        string? user = null;
        try
        {
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            user = Environment.UserName;
        }
        catch (Exception e) when (e is PlatformNotSupportedException or InvalidOperationException)
        {
        }

        return new CrashScrubber(home, user);
    }

    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";
        var result = text;
        if (_home is not null)
            result = result.Replace(_home, "~", StringComparison.OrdinalIgnoreCase);
        if (_altHome is not null)
            result = result.Replace(_altHome, "~", StringComparison.OrdinalIgnoreCase);
        if (_user is not null)
            result = result.Replace(_user, "<user>", StringComparison.OrdinalIgnoreCase);
        return result;
    }

    public string? ScrubOrNull(string? text) => text is null ? null : Scrub(text);
}
