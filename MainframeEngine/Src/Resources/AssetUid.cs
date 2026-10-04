using System.Security.Cryptography;

namespace MainframeEngine;

/// <summary>
/// Stable asset identifiers: a three-letter kind prefix and lowercase hex, e.g. <c>scn_3f9a1c2e07b4</c>. Scene
/// and resource files store their own UID; other assets get one in a <c>.meta</c> sidecar. References between
/// files use UIDs (with the path as a hint), so moving or renaming a file does not break them.
/// </summary>
public static class AssetUid
{
    /// <summary>Scenes (<c>.mscene</c>).</summary>
    public const string ScenePrefix = "scn";

    /// <summary>Resources (<c>.mres</c>).</summary>
    public const string ResourcePrefix = "res";

    /// <summary>Images.</summary>
    public const string TexturePrefix = "tex";

    /// <summary>Sounds.</summary>
    public const string AudioPrefix = "aud";

    /// <summary>Models.</summary>
    public const string ModelPrefix = "mdl";

    /// <summary>Anything else (Spine data, UI documents, fonts, ...).</summary>
    public const string AssetPrefix = "ast";

    /// <summary>
    /// Hex digits in a new UID. 48 bits (the design sketch showed 32) keeps accidental collisions across
    /// branches negligible for projects with tens of thousands of assets.
    /// </summary>
    public const int HexDigits = 12;

    /// <summary>A new random UID with <paramref name="prefix"/>.</summary>
    public static string Generate(string prefix)
    {
        if (prefix is not { Length: 3 } || !prefix.All(char.IsAsciiLetterLower))
            throw new ArgumentException("UID prefixes are three lowercase letters.", nameof(prefix));
        return $"{prefix}_{RandomNumberGenerator.GetHexString(HexDigits, lowercase: true)}";
    }

    /// <summary>True for <c>abc_</c> followed by 8–16 lowercase hex digits.</summary>
    public static bool IsUid(ReadOnlySpan<char> text)
    {
        if (text.Length < 12 || text.Length > 20 || text[3] != '_')
            return false;
        for (var i = 0; i < 3; i++)
            if (!char.IsAsciiLetterLower(text[i]))
                return false;
        foreach (var c in text[4..])
            if (!char.IsAsciiHexDigitLower(c) && !char.IsAsciiDigit(c))
                return false;
        return true;
    }

    /// <summary>The prefix for a new asset file of this extension (<c>.png</c> → <c>tex</c>).</summary>
    public static string PrefixForExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".mscene" => ScenePrefix,
        ".mres" => ResourcePrefix,
        ".png" or ".jpg" or ".jpeg" or ".hdr" or ".ktx" or ".ktx2" => TexturePrefix,
        ".ogg" or ".wav" or ".mp3" or ".flac" => AudioPrefix,
        ".gltf" or ".glb" or ".fbx" or ".obj" or ".dae" => ModelPrefix,
        _ => AssetPrefix,
    };
}
