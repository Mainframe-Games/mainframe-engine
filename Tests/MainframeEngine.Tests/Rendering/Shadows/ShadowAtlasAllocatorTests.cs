namespace MainframeEngine.Tests.Rendering;

public sealed class ShadowAtlasAllocatorTests
{
    [Fact]
    public void QuadrantsFillTheAtlasInZOrder()
    {
        var atlas = new ShadowAtlasAllocator(4096);
        var tiles = new List<ShadowAtlasTile>();
        for (var i = 0; i < 4; i++)
        {
            Assert.True(atlas.TryAllocate(2048, out var tile));
            tiles.Add(tile);
        }

        Assert.Equal([new(0, 0, 2048), new(2048, 0, 2048), new(0, 2048, 2048), new(2048, 2048, 2048)], tiles);
        Assert.False(atlas.TryAllocate(64, out _));
        Assert.Equal(4096L * 4096, atlas.UsedArea);
    }

    [Fact]
    public void RequestsRoundUpToPowersOfTwoAndTheMinimumTile()
    {
        var atlas = new ShadowAtlasAllocator(1024, 64);
        Assert.True(atlas.TryAllocate(300, out var a));
        Assert.Equal(512, a.Size);
        Assert.True(atlas.TryAllocate(10, out var b));
        Assert.Equal(64, b.Size);
        Assert.False(atlas.TryAllocate(2048, out _));
    }

    [Fact]
    public void FreeingMergesBuddiesBackIntoLargerTiles()
    {
        var atlas = new ShadowAtlasAllocator(2048);
        var small = new List<ShadowAtlasTile>();
        for (var i = 0; i < 16; i++)
        {
            Assert.True(atlas.TryAllocate(512, out var tile));
            small.Add(tile);
        }

        Assert.False(atlas.TryAllocate(1024, out _));
        foreach (var tile in small)
            atlas.Free(tile);
        Assert.Equal(0, atlas.TileCount);
        Assert.Equal(0, atlas.UsedArea);
        Assert.True(atlas.TryAllocate(2048, out var whole));
        Assert.Equal(new ShadowAtlasTile(0, 0, 2048), whole);
    }

    [Fact]
    public void FreedSpaceIsReusedWithoutDisturbingOtherTiles()
    {
        var atlas = new ShadowAtlasAllocator(2048);
        Assert.True(atlas.TryAllocate(1024, out var a));
        Assert.True(atlas.TryAllocate(1024, out var b));
        Assert.True(atlas.TryAllocate(512, out var c));
        atlas.Free(b);
        Assert.True(atlas.TryAllocate(1024, out var d));
        Assert.Equal(b, d); // the freed quadrant comes back
        Assert.False(a.Overlaps(d) || c.Overlaps(d) || a.Overlaps(c));
    }

    [Fact]
    public void InvalidFreesThrow()
    {
        var atlas = new ShadowAtlasAllocator(1024);
        Assert.True(atlas.TryAllocate(512, out var tile));
        atlas.Free(tile);
        Assert.Throws<ArgumentException>(() => atlas.Free(tile)); // double free
        Assert.Throws<ArgumentException>(() => atlas.Free(new ShadowAtlasTile(100, 0, 512))); // misaligned
        Assert.Throws<ArgumentException>(() => atlas.Free(new ShadowAtlasTile(0, 0, 2048))); // larger than the atlas
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShadowAtlasAllocator(1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShadowAtlasAllocator(1024, 2048));
    }

    [Fact]
    public void PackPlacesEverythingThatFitsAtFullSize()
    {
        var atlas = new ShadowAtlasAllocator(4096);
        int[] requests = [1024, 2048, 512, 512, 1024, 1024];
        var tiles = new ShadowAtlasTile[requests.Length];

        Assert.Equal(0, atlas.Pack(requests, tiles));
        for (var i = 0; i < requests.Length; i++)
            Assert.Equal(requests[i], tiles[i].Size);
        AssertDisjointAndInside(tiles, 4096);
        Assert.Equal(new ShadowAtlasTile(0, 0, 2048), tiles[1]); // largest first
    }

    [Fact]
    public void PackShrinksTheLargestTilesWhenTheAtlasIsOversubscribed()
    {
        var atlas = new ShadowAtlasAllocator(4096);
        int[] requests = [2048, 2048, 2048, 2048, 2048, 1024];
        var tiles = new ShadowAtlasTile[requests.Length];

        var degraded = atlas.Pack(requests, tiles);
        Assert.All(tiles, t => Assert.False(t.IsEmpty)); // every light keeps a shadow
        Assert.Equal(2, degraded);
        Assert.Equal([2048, 2048, 2048, 1024, 1024, 1024], tiles.Select(t => t.Size));
        AssertDisjointAndInside(tiles, 4096);
    }

    [Fact]
    public void PackDropsTheLastRequestsOnlyWhenEveryTileIsMinimal()
    {
        var atlas = new ShadowAtlasAllocator(512, 256);
        int[] requests = [512, 256, 256, 256, 512];
        var tiles = new ShadowAtlasTile[requests.Length];

        Assert.Equal(2, atlas.Pack(requests, tiles)); // the first 512 shrunk, the last request dropped
        Assert.Equal([256, 256, 256, 256, 0], tiles.Select(t => t.Size));
        AssertDisjointAndInside(tiles, 512);
    }

    [Fact]
    public void OversizedRequestsAreClampedToTheAtlas()
    {
        var atlas = new ShadowAtlasAllocator(1024);
        var tiles = new ShadowAtlasTile[1];
        Assert.Equal(1, atlas.Pack([4096], tiles));
        Assert.Equal(new ShadowAtlasTile(0, 0, 1024), tiles[0]);
    }

    [Fact]
    public void RepackingStartsFromAnEmptyAtlas()
    {
        var atlas = new ShadowAtlasAllocator(2048);
        var tiles = new ShadowAtlasTile[4];
        atlas.Pack([1024, 1024, 1024, 1024], tiles);
        Assert.Equal(4, atlas.TileCount);
        atlas.Pack([2048], tiles);
        Assert.Equal(1, atlas.TileCount);
        Assert.Equal(new ShadowAtlasTile(0, 0, 2048), tiles[0]);
    }

    [Fact]
    public void RandomRequestSetsNeverOverlap()
    {
        var random = new Random(1234);
        var atlas = new ShadowAtlasAllocator(4096);
        var requests = new int[11];
        var tiles = new ShadowAtlasTile[11];
        for (var round = 0; round < 200; round++)
        {
            for (var i = 0; i < requests.Length; i++)
                requests[i] = 64 << random.Next(0, 6); // 64 .. 2048
            var degraded = atlas.Pack(requests, tiles);
            AssertDisjointAndInside(tiles, 4096);
            Assert.All(tiles, t => Assert.False(t.IsEmpty));

            long area = 0;
            foreach (var r in requests)
                area += (long)r * r;
            if (area <= 4096L * 4096)
                Assert.Equal(0, degraded); // largest-first power-of-two packing never fragments
            else
                Assert.True(degraded > 0);
        }
    }

    [Theory]
    [InlineData(new[] { 1024, 1024, 1024 }, 2048)]
    [InlineData(new[] { 1024, 1024, 1024, 1024 }, 2048)]
    [InlineData(new[] { 2048, 1024 }, 4096)]
    [InlineData(new[] { 256 }, 512)]
    [InlineData(new[] { 4096, 4096 }, 4096)]
    public void RequiredSizeIsTheSmallestAtlasThatHoldsEverything(int[] requests, int expected)
    {
        Assert.Equal(expected, ShadowAtlasAllocator.RequiredSize(requests, 64, 512, 4096));
    }

    [Fact]
    public void PackingAllocatesNothing()
    {
        var atlas = new ShadowAtlasAllocator(4096);
        int[] requests = [1024, 2048, 512, 512, 1024, 1024, 256, 256, 128, 1024, 512];
        var tiles = new ShadowAtlasTile[requests.Length];
        atlas.Pack(requests, tiles);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            atlas.Pack(requests, tiles);
            atlas.Free(tiles[0]);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void AssertDisjointAndInside(ShadowAtlasTile[] tiles, int size)
    {
        for (var i = 0; i < tiles.Length; i++)
        {
            var t = tiles[i];
            if (t.IsEmpty)
                continue;
            Assert.True(t.X >= 0 && t.Y >= 0 && t.X + t.Size <= size && t.Y + t.Size <= size, $"{t} leaves the atlas");
            Assert.Equal(0, t.X % t.Size);
            Assert.Equal(0, t.Y % t.Size);
            for (var j = i + 1; j < tiles.Length; j++)
                Assert.False(t.Overlaps(tiles[j]), $"{t} overlaps {tiles[j]}");
        }
    }
}
