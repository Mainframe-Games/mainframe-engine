namespace MainframeEngine;

/// <summary>Resizing of RGBA8 images (editor thumbnails, export icons).</summary>
public static class ImageResize
{
    /// <summary>Box-filters RGBA8 down to fit <paramref name="size"/>² (aspect kept, never enlarged), weighting colour by alpha.</summary>
    public static (int Width, int Height, byte[] Pixels) Downscale(int width, int height, byte[] rgba, int size)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new InvalidDataException("the image has no pixels.");
        var scale = Math.Min(1.0, Math.Min((double)size / width, (double)size / height));
        var outWidth = Math.Max(1, (int)Math.Round(width * scale));
        var outHeight = Math.Max(1, (int)Math.Round(height * scale));
        if (outWidth == width && outHeight == height)
            return (width, height, rgba.AsSpan(0, width * height * 4).ToArray());

        var result = new byte[outWidth * outHeight * 4];
        for (var y = 0; y < outHeight; y++)
        {
            var y0 = y * height / outHeight;
            var y1 = Math.Max(y0 + 1, (y + 1) * height / outHeight);
            for (var x = 0; x < outWidth; x++)
            {
                var x0 = x * width / outWidth;
                var x1 = Math.Max(x0 + 1, (x + 1) * width / outWidth);
                long r = 0, g = 0, b = 0, a = 0, count = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var row = sy * width * 4;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var i = row + sx * 4;
                        var alpha = rgba[i + 3];
                        r += rgba[i] * alpha;
                        g += rgba[i + 1] * alpha;
                        b += rgba[i + 2] * alpha;
                        a += alpha;
                        count++;
                    }
                }

                var o = (y * outWidth + x) * 4;
                if (a > 0)
                {
                    result[o] = (byte)((r + a / 2) / a);
                    result[o + 1] = (byte)((g + a / 2) / a);
                    result[o + 2] = (byte)((b + a / 2) / a);
                }

                result[o + 3] = (byte)((a + count / 2) / count);
            }
        }

        return (outWidth, outHeight, result);
    }
}
