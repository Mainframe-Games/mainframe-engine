namespace MainframeEngine;

/// <summary>Turns a captured frame into a save thumbnail (on the save worker): centre crop, box filter, opaque PNG.</summary>
internal static class SaveThumbnail
{
    /// <summary>
    /// The PNG of <paramref name="rgba"/> (<paramref name="width"/>×<paramref name="height"/>, top row first) cropped to
    /// the target aspect around its centre and box-filtered down to exactly <paramref name="targetWidth"/>×<paramref name="targetHeight"/>.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, int targetWidth, int targetHeight)
    {
        var pixels = Downscale(rgba, width, height, targetWidth, targetHeight);
        return Png.EncodeRgba8(targetWidth, targetHeight, pixels);
    }

    /// <summary>The box-filtered, centre-cropped RGBA8 pixels (alpha forced to 255: the swapchain's alpha means nothing).</summary>
    public static byte[] Downscale(ReadOnlySpan<byte> rgba, int width, int height, int targetWidth, int targetHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetHeight);
        if (rgba.Length < (long)width * height * 4)
            throw new ArgumentException($"Expected {width * height * 4} bytes of RGBA8, got {rgba.Length}.", nameof(rgba));

        // The largest centred source rectangle with the target's aspect ratio.
        int cropWidth = width, cropHeight = height;
        if ((long)width * targetHeight > (long)height * targetWidth)
            cropWidth = Math.Max(1, (int)((long)height * targetWidth / targetHeight));
        else
            cropHeight = Math.Max(1, (int)((long)width * targetHeight / targetWidth));
        var left = (width - cropWidth) / 2;
        var top = (height - cropHeight) / 2;

        var result = new byte[targetWidth * targetHeight * 4];
        for (var y = 0; y < targetHeight; y++)
        {
            var y0 = top + (int)((long)y * cropHeight / targetHeight);
            var y1 = Math.Max(y0 + 1, top + (int)((long)(y + 1) * cropHeight / targetHeight));
            for (var x = 0; x < targetWidth; x++)
            {
                var x0 = left + (int)((long)x * cropWidth / targetWidth);
                var x1 = Math.Max(x0 + 1, left + (int)((long)(x + 1) * cropWidth / targetWidth));
                long r = 0, g = 0, b = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var row = sy * width * 4;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var i = row + sx * 4;
                        r += rgba[i];
                        g += rgba[i + 1];
                        b += rgba[i + 2];
                    }
                }

                var count = (long)(y1 - y0) * (x1 - x0);
                var o = (y * targetWidth + x) * 4;
                result[o] = (byte)((r + count / 2) / count);
                result[o + 1] = (byte)((g + count / 2) / count);
                result[o + 2] = (byte)((b + count / 2) / count);
                result[o + 3] = 255;
            }
        }

        return result;
    }
}
