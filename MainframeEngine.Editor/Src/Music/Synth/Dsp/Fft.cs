namespace MainframeEngine.Editor.Music;

/// <summary>
/// A small in-place radix-2 complex FFT (UI and worker threads only: wavetable mip-maps and imports). Sizes are powers of
/// two; the inverse is unscaled-forward-then-scaled (divides by the length).
/// </summary>
public static class Fft
{
    /// <summary>Transforms <paramref name="re"/>/<paramref name="im"/> (equal power-of-two lengths) in place.</summary>
    public static void Transform(Span<double> re, Span<double> im, bool inverse)
    {
        var n = re.Length;
        if (n != im.Length || n == 0 || (n & (n - 1)) != 0)
            throw new ArgumentException("The FFT needs two arrays of the same power-of-two length.");

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = 2 * Math.PI / length * (inverse ? 1 : -1);
            var (wr, wi) = (Math.Cos(angle), Math.Sin(angle));
            for (var start = 0; start < n; start += length)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < length / 2; k++)
                {
                    var a = start + k;
                    var b = a + length / 2;
                    var tr = re[b] * cr - im[b] * ci;
                    var ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }

        if (!inverse)
            return;
        for (var i = 0; i < n; i++)
        {
            re[i] /= n;
            im[i] /= n;
        }
    }
}
