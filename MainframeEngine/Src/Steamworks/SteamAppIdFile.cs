using System.Globalization;

namespace MainframeEngine;

/// <summary>
/// Development <c>steam_appid.txt</c>: lets <c>SteamAPI_Init</c> find the app id when the game was not launched by
/// Steam. Steam reads the file from the working directory, or the <c>SteamAppId</c> environment variable.
/// </summary>
internal static class SteamAppIdFile
{
    public const string FileName = "steam_appid.txt";

    /// <summary>Writes the file into <paramref name="directory"/> (if missing or different) and sets <c>SteamAppId</c>.</summary>
    /// <returns>False if the file could not be written (logged); the environment variable is set regardless.</returns>
    public static bool Apply(string directory, uint appId)
    {
        var text = appId.ToString(CultureInfo.InvariantCulture);
        Environment.SetEnvironmentVariable("SteamAppId", text);
        return TryWrite(directory, appId);
    }

    /// <summary>Writes <c>steam_appid.txt</c> containing <paramref name="appId"/>, unless it already does.</summary>
    public static bool TryWrite(string directory, uint appId)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        var text = appId.ToString(CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, FileName);
        try
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() == text)
                return true;
            File.WriteAllText(path, text);
            Log.Debug($"[Steam] wrote {path} ({text})");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Steam] could not write {path}: {e.Message}");
            return false;
        }
    }
}
