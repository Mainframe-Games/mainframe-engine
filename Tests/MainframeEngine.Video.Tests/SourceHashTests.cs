using MainframeEngine.Video.Projects;

namespace MainframeEngine.Video.Tests;

public sealed class SourceHashTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"cutback-hash-{Guid.NewGuid():N}");

    public SourceHashTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Pattern(int length, byte seed = 0)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i * 31 + seed);
        }

        return bytes;
    }

    [Fact]
    public async Task SameContentGivesTheSameHash()
    {
        var a = Write("a.bin", Pattern(1000));
        var b = Write("b.bin", Pattern(1000));

        Assert.Equal(await SourceHash.ComputeAsync(b, TestContext.Current.CancellationToken), await SourceHash.ComputeAsync(a, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HashIsLowerCaseHexSha256Length()
    {
        var a = Write("a.bin", Pattern(10));

        var hash = await SourceHash.ComputeAsync(a, TestContext.Current.CancellationToken);

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]+$", hash);
    }

    [Fact]
    public async Task DifferentContentGivesADifferentHash()
    {
        var a = Write("a.bin", Pattern(1000, seed: 1));
        var b = Write("b.bin", Pattern(1000, seed: 2));

        Assert.NotEqual(await SourceHash.ComputeAsync(b, TestContext.Current.CancellationToken), await SourceHash.ComputeAsync(a, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OnlyTheFirst8MBAreReadButTheLengthStillCounts()
    {
        var head = Pattern(SourceHash.PrefixLength);
        var sameHeadDifferentTail1 = head.Concat(Pattern(100, seed: 1)).ToArray();
        var sameHeadDifferentTail2 = head.Concat(Pattern(100, seed: 2)).ToArray();
        var sameHeadLongerTail = head.Concat(Pattern(200, seed: 1)).ToArray();

        var a = Write("a.bin", sameHeadDifferentTail1);
        var b = Write("b.bin", sameHeadDifferentTail2);
        var c = Write("c.bin", sameHeadLongerTail);

        var hashA = await SourceHash.ComputeAsync(a, TestContext.Current.CancellationToken);
        var hashB = await SourceHash.ComputeAsync(b, TestContext.Current.CancellationToken);
        var hashC = await SourceHash.ComputeAsync(c, TestContext.Current.CancellationToken);

        Assert.Equal(hashB, hashA); // bytes past the prefix are not read
        Assert.NotEqual(hashC, hashA); // the file length is part of the hash
    }

    [Fact]
    public async Task EmptyFileHashesWithoutError()
    {
        var a = Write("empty.bin", []);

        var hash = await SourceHash.ComputeAsync(a, TestContext.Current.CancellationToken);

        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public async Task MissingFileThrowsFileNotFound()
    {
        var act = () => SourceHash.ComputeAsync(Path.Combine(_dir, "missing.bin"), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<FileNotFoundException>(act);
    }
}
