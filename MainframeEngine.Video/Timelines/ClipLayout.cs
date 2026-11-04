namespace MainframeEngine.Video.Timelines;

/// <summary>Where a clip's (cropped) frame lands in the timeline's frame, in timeline pixels (it may overflow the frame).</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
/// <param name="ScaleX">Timeline pixels per source pixel, horizontally.</param>
/// <param name="ScaleY">Timeline pixels per source pixel, vertically.</param>
public readonly record struct ClipPlacement(double X, double Y, double Width, double Height, double ScaleX, double ScaleY);

/// <summary>
/// The part of a clip that is inside the frame, in whole pixels: the source rectangle to crop, and where it goes. This is
/// what export renders (<c>crop</c>, <c>scale</c>, <c>pad</c>) and what the preview's sprite shows.
/// </summary>
public readonly record struct VisiblePart(int SourceX, int SourceY, int SourceWidth, int SourceHeight, int X, int Y, int Width, int Height);

/// <summary>Reframing (pure): fit, fill or stretch, then scale, position and crop (<see cref="ClipTransform"/>).</summary>
public static class ClipLayout
{
    /// <summary>The placement of a <paramref name="sourceWidth"/>×<paramref name="sourceHeight"/> frame in a <paramref name="frameWidth"/>×<paramref name="frameHeight"/> timeline.</summary>
    public static ClipPlacement Place(int sourceWidth, int sourceHeight, ClipTransform transform, int frameWidth, int frameHeight)
    {
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        var crop = transform.Crop;
        var width = Math.Max(1, sourceWidth - crop.Left - crop.Right);
        var height = Math.Max(1, sourceHeight - crop.Top - crop.Bottom);
        double scaleX, scaleY;
        switch (transform.Fit)
        {
            case FrameFit.Fit:
                scaleX = scaleY = Math.Min(frameWidth / width, frameHeight / height);
                break;
            case FrameFit.Stretch:
                scaleX = frameWidth / width;
                scaleY = frameHeight / height;
                break;
            default:
                scaleX = scaleY = Math.Max(frameWidth / width, frameHeight / height);
                break;
        }

        var scale = transform.Scale > 0 ? transform.Scale : 1;
        scaleX *= scale;
        scaleY *= scale;
        var w = width * scaleX;
        var h = height * scaleY;
        return new ClipPlacement((frameWidth - w) / 2 + transform.X, (frameHeight - h) / 2 + transform.Y, w, h, scaleX, scaleY);
    }

    /// <summary>The visible part of the clip, or null when it is entirely outside the frame.</summary>
    public static VisiblePart? Visible(int sourceWidth, int sourceHeight, ClipTransform transform, int frameWidth, int frameHeight)
    {
        var p = Place(sourceWidth, sourceHeight, transform, frameWidth, frameHeight);
        var left = Math.Max(0, p.X);
        var top = Math.Max(0, p.Y);
        var right = Math.Min(frameWidth, p.X + p.Width);
        var bottom = Math.Min(frameHeight, p.Y + p.Height);
        var x = (int)Math.Round(left);
        var y = (int)Math.Round(top);
        var width = (int)Math.Round(right) - x;
        var height = (int)Math.Round(bottom) - y;
        if (width < 1 || height < 1)
            return null;

        var crop = transform.Crop;
        var sx = crop.Left + (left - p.X) / p.ScaleX;
        var sy = crop.Top + (top - p.Y) / p.ScaleY;
        var sw = (right - left) / p.ScaleX;
        var sh = (bottom - top) / p.ScaleY;
        var sourceX = Math.Clamp((int)Math.Round(sx), 0, sourceWidth - 1);
        var sourceY = Math.Clamp((int)Math.Round(sy), 0, sourceHeight - 1);
        var sourceW = Math.Clamp((int)Math.Round(sw), 1, sourceWidth - sourceX);
        var sourceH = Math.Clamp((int)Math.Round(sh), 1, sourceHeight - sourceY);
        return new VisiblePart(sourceX, sourceY, sourceW, sourceH, x, y, width, height);
    }
}
