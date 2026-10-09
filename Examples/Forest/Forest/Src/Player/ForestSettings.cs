using System.Text.Json;
using MainframeEngine;

namespace Forest;

/// <summary>
/// The player's settings (FOV, mouse sensitivity, invert Y, pad look speed, head bob, audio volumes, the graphics options
/// changed in the pause menu), stored as a small JSON file in the game's user data folder until the engine's
/// <c>UserSettings</c> (G4) exists. The pause menu (<see cref="PauseMenu"/>) edits it;
/// <see cref="FirstPersonController.ApplySettings"/>, <see cref="ForestAudio.ApplySettings"/> and
/// <see cref="ForestOptions"/> apply it.
/// </summary>
public sealed class ForestSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Horizontal FOV in degrees (60–110).</summary>
    public float HorizontalFov { get; set; } = 90f;

    /// <summary>Degrees per mouse count.</summary>
    public float MouseSensitivity { get; set; } = 0.1f;

    public bool InvertY { get; set; }

    /// <summary>Degrees per second at full right-stick deflection.</summary>
    public float GamepadLookSpeed { get; set; } = 140f;

    public bool HeadBob { get; set; } = true;

    /// <summary>Bus volumes (linear 0–1 over the bus layout's levels); <see cref="ForestAudio.ApplySettings"/> applies them.</summary>
    public ForestAudioSettings Audio { get; set; } = new();

    /// <summary>
    /// The pause menu's graphics options the player changed (<see cref="ForestOptions"/> keys → values; ADR 0180). Only
    /// what differs from the scene's and the project's own values is stored, so a changed scene default still reaches
    /// players who never touched that option. <see cref="PauseMenu"/> applies them at start in normal play.
    /// </summary>
    public Dictionary<string, float> Graphics { get; set; } = new(StringComparer.Ordinal);

    /// <summary><c>{user data}/settings.json</c> of the running game.</summary>
    public static string DefaultPath => GameHost.UserDataPath("settings.json");

    /// <summary>The settings in <paramref name="path"/>, or the defaults when it is missing or unreadable.</summary>
    public static ForestSettings LoadOrDefault(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<ForestSettings>(File.ReadAllText(path), Options) ?? new ForestSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warning($"[Forest] Ignoring unreadable settings '{path}': {e.Message}");
        }

        return new ForestSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }
}
