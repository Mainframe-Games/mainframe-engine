using System.Buffers.Binary;

namespace MainframeEngine;

/// <summary>
/// Marks a vertex shader's position output <c>Invariant</c> (ADR 0172): GLSL's <c>invariant gl_Position</c>, which Slang
/// cannot express. Two vertex shaders that compute the position with the same expressions then produce bit-identical
/// positions, so a depth prepass and its colour pass meet under an <c>EQUAL</c> test. Without it the drivers may contract
/// and reorder each module's arithmetic on its own (MoltenVK compiles every module with fast math): the foliage's long
/// hierarchical wind then landed whole cards a rounding step away from their prepass depth, and they vanished.
/// <see cref="ShaderModuleCache"/> applies it to <see cref="Applies"/> modules when it loads them.
/// </summary>
public static class SpirvInvariance
{
    private const uint MagicNumber = 0x07230203;
    private const ushort OpDecorate = 71, OpMemberDecorate = 72;
    private const uint DecorationBuiltIn = 11, DecorationInvariant = 18, BuiltInPosition = 0;

    /// <summary>
    /// The vertex shaders whose position must match another module's exactly: the foliage and impostor colour and
    /// prepass stages (content paths ending in these names).
    /// </summary>
    public static readonly string[] Shaders =
    [
        "Foliage/Foliage.vk.vert.spv",
        "Foliage/FoliageDepth.vk.vert.spv",
        "Impostor/Impostor.vk.vert.spv",
        "Impostor/ImpostorDepth.vk.vert.spv",
    ];

    /// <summary>True when the module at <paramref name="spvPath"/> gets an invariant position.</summary>
    public static bool Applies(string spvPath)
    {
        var path = spvPath.Replace('\\', '/');
        foreach (var name in Shaders)
        {
            if (path.EndsWith(name, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// <paramref name="code"/> with an <c>OpDecorate … Invariant</c> after every decoration of the <c>Position</c> built-in
    /// (a variable or a block member); the module unchanged when it has none or already says so. Load time (allocates).
    /// </summary>
    public static byte[] MakePositionInvariant(byte[] code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code.Length < 20 || code.Length % 4 != 0 || BinaryPrimitives.ReadUInt32LittleEndian(code) != MagicNumber)
            throw new InvalidDataException("Not a little-endian SPIR-V module.");

        var words = new uint[code.Length / 4];
        for (var i = 0; i < words.Length; i++)
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan(i * 4));

        var output = new List<uint>(words.Length + 8);
        output.AddRange(words.AsSpan(0, 5));
        var added = 0;
        for (var i = 5; i < words.Length;)
        {
            var count = (int)(words[i] >> 16);
            var op = (ushort)(words[i] & 0xFFFF);
            if (count == 0 || i + count > words.Length)
                throw new InvalidDataException("Truncated SPIR-V instruction.");
            var instruction = words.AsSpan(i, count);
            output.AddRange(instruction);
            if (op == OpDecorate && count >= 3 && instruction[2] == DecorationInvariant)
                return code; // already invariant
            if (op == OpDecorate && count == 4 && instruction[2] == DecorationBuiltIn && instruction[3] == BuiltInPosition)
            {
                output.AddRange([(3u << 16) | OpDecorate, instruction[1], DecorationInvariant]);
                added++;
            }
            else if (op == OpMemberDecorate && count == 5 && instruction[3] == DecorationBuiltIn && instruction[4] == BuiltInPosition)
            {
                output.AddRange([(4u << 16) | OpMemberDecorate, instruction[1], instruction[2], DecorationInvariant]);
                added++;
            }

            i += count;
        }

        if (added == 0)
            return code;
        var result = new byte[output.Count * 4];
        for (var i = 0; i < output.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4), output[i]);
        return result;
    }
}
