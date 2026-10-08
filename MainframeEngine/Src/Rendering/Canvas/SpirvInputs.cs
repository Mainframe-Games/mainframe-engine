using System.Buffers.Binary;

namespace MainframeEngine;

/// <summary>
/// Reads the <c>Location</c>s of a SPIR-V module's <c>Input</c> variables (stage inputs; built-ins have none). The canvas
/// shader build uses it to make a vertex stage write only what its fragment stage reads: Slang drops unread fragment
/// inputs, and a vertex output no fragment input reads is a validation warning (ADR 0144).
/// </summary>
internal static class SpirvInputs
{
    private const uint Magic = 0x07230203;
    private const int OpDecorate = 71;
    private const int OpVariable = 59;
    private const uint DecorationLocation = 30;
    private const uint DecorationBuiltIn = 11;
    private const uint StorageClassInput = 1;

    /// <exception cref="FormatException">Not a little-endian SPIR-V module.</exception>
    public static HashSet<int> InputLocations(ReadOnlySpan<byte> spirv)
    {
        if (spirv.Length < 20 || spirv.Length % 4 != 0 || BinaryPrimitives.ReadUInt32LittleEndian(spirv) != Magic)
            throw new FormatException("Not a SPIR-V module.");

        var locations = new Dictionary<uint, int>();
        var builtIns = new HashSet<uint>();
        var inputs = new HashSet<uint>();
        for (var offset = 20; offset < spirv.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(spirv[offset..]);
            int opcode = (int)(word & 0xFFFF), count = (int)(word >> 16);
            if (count == 0 || offset + count * 4 > spirv.Length)
                throw new FormatException("Truncated SPIR-V instruction.");
            if (opcode == OpDecorate && count >= 4 && Operand(spirv, offset, 2) == DecorationLocation)
                locations[Operand(spirv, offset, 1)] = (int)Operand(spirv, offset, 3);
            else if (opcode == OpDecorate && count >= 4 && Operand(spirv, offset, 2) == DecorationBuiltIn)
                builtIns.Add(Operand(spirv, offset, 1));
            else if (opcode == OpVariable && count >= 4 && Operand(spirv, offset, 3) == StorageClassInput)
                inputs.Add(Operand(spirv, offset, 2));
            offset += count * 4;
        }

        var result = new HashSet<int>();
        foreach (var id in inputs)
        {
            if (locations.TryGetValue(id, out var location))
                result.Add(location);
            else if (!builtIns.Contains(id))
                // A block-typed input carries its Locations on its members; reporting none would make the vertex stage
                // write nothing, so refuse instead.
                throw new FormatException($"SPIR-V input %{id} has neither a Location nor a BuiltIn decoration (block-typed stage input?).");
        }

        return result;
    }

    // Word i of the instruction starting at byte `offset` (word 0 is the opcode and word count).
    private static uint Operand(ReadOnlySpan<byte> spirv, int offset, int i) =>
        BinaryPrimitives.ReadUInt32LittleEndian(spirv[(offset + i * 4)..]);
}
