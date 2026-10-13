using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MainframeEngine.Video.Projects;

/// <summary>
/// Cheap identity for a source video: SHA-256 over the first 8 MB followed by the 8-byte file
/// length. Reading a whole multi-gigabyte recording just to detect that it moved would be silly;
/// this catches replaced, re-encoded and truncated files in a few milliseconds.
/// </summary>
public static class SourceHash
{
    public const int PrefixLength = 8 * 1024 * 1024;

    /// <returns>64 lower-case hex characters.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static async Task<string> ComputeAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16, useAsync: true);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[1 << 16];
        long remaining = PrefixLength;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            sha.AppendData(buffer, 0, read);
            remaining -= read;
        }

        var lengthBytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, stream.Length);
        sha.AppendData(lengthBytes);

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>True if the file at <paramref name="path"/> still matches <paramref name="expectedHash"/>. False if it differs or is missing.</summary>
    public static async Task<bool> MatchesAsync(string path, string expectedHash, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var actual = await ComputeAsync(path, cancellationToken).ConfigureAwait(false);
        return string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase);
    }
}
