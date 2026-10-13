using System.Buffers.Binary;
using System.Runtime.InteropServices;
using MainframeEngine.Video.Media;

namespace MainframeEngine.Video.Tests.Media;

public sealed class PeakReducerTests
{
    private static byte[] Bytes(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        }

        return bytes;
    }

    [Fact]
    public void FullBucketsRecordMinAndMax()
    {
        var reducer = new PeakReducer(samplesPerBucket: 4);

        reducer.AddSamples([1, -5, 3, 2, 10, -1, 0, 7]);
        var peaks = reducer.Finish();

        Assert.Equal([new Peak(-5, 3), new Peak(-1, 10)], peaks);
    }

    [Fact]
    public void APartialTrailingBucketIsKept()
    {
        var reducer = new PeakReducer(samplesPerBucket: 4);

        reducer.AddSamples([1, -5, 3, 2, 9]);
        var peaks = reducer.Finish();

        Assert.Equal([new Peak(-5, 3), new Peak(9, 9)], peaks);
    }

    [Fact]
    public void BucketsSpanCallsToAddSamples()
    {
        var reducer = new PeakReducer(samplesPerBucket: 4);

        reducer.AddSamples([1, -5]);
        reducer.AddSamples([3]);
        reducer.AddSamples([2, 10, -1, 0, 7]);
        var peaks = reducer.Finish();

        Assert.Equal([new Peak(-5, 3), new Peak(-1, 10)], peaks);
    }

    [Fact]
    public void AddBytesDecodesLittleEndianS16()
    {
        var reducer = new PeakReducer(samplesPerBucket: 2);

        reducer.AddBytes(Bytes(-32768, 32767, 100, -100));
        var peaks = reducer.Finish();

        Assert.Equal([new Peak(-32768, 32767), new Peak(-100, 100)], peaks);
    }

    [Fact]
    public void AddBytesCarriesAnOddTrailingByteToTheNextCall()
    {
        var reducer = new PeakReducer(samplesPerBucket: 2);
        var bytes = Bytes(1000, -2000, 3000, -4000);

        reducer.AddBytes(bytes.AsSpan(0, 3));  // one full sample and half of the next
        reducer.AddBytes(bytes.AsSpan(3));
        var peaks = reducer.Finish();

        Assert.Equal([new Peak(-2000, 1000), new Peak(-4000, 3000)], peaks);
    }

    [Fact]
    public void EmptyInputProducesNoPeaks()
    {
        var reducer = new PeakReducer(samplesPerBucket: 4);

        Assert.Empty(reducer.Finish());
    }

    [Fact]
    public void SamplesSeenCountsEverySample()
    {
        var reducer = new PeakReducer(samplesPerBucket: 4);

        reducer.AddSamples([1, 2, 3]);
        reducer.AddBytes(Bytes(4, 5));

        Assert.Equal(5, reducer.SamplesSeen);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsNonPositiveBucketSize(int size)
    {
        var act = () => new PeakReducer(size);

        Assert.Throws<ArgumentOutOfRangeException>(act);
    }

    [Fact]
    public void PeakIsAFourByteStruct()
    {
        // The finest level of an hour-long recording holds ~1.8M peaks; keep them tight.
        Assert.Equal(4, Marshal.SizeOf<Peak>());
    }
}
