using System.Globalization;
using System.Text;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Media.Export;

/// <summary>
/// Builds the trim / atrim / afade / concat filter graph for a precise export. The result is
/// written to a file and passed with <c>-filter_complex_script</c>, never inline: a normal
/// recording has hundreds of cuts and would overflow the Windows command line.
/// </summary>
public static class FilterGraphBuilder
{
    /// <summary>Fade at each cut so the splice is not an audible click.</summary>
    public const double FadeSeconds = 0.008;

    public static string Build(IReadOnlyList<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var kept = segments.Where(s => s.Enabled).OrderBy(s => s.Start).ToList();
        if (kept.Count == 0)
        {
            throw new InvalidOperationException("Every segment is removed; there is nothing to export.");
        }

        var sb = new StringBuilder(kept.Count * 200);
        var concatInputs = new StringBuilder(kept.Count * 10);

        for (var i = 0; i < kept.Count; i++)
        {
            var s = kept[i];
            var length = s.End - s.Start;
            var fade = Math.Min(FadeSeconds, length / 2);

            sb.Append("[0:v]trim=start=").Append(F(s.Start)).Append(":end=").Append(F(s.End))
              .Append(",setpts=PTS-STARTPTS[v").Append(i).Append("];\n");

            sb.Append("[0:a]atrim=start=").Append(F(s.Start)).Append(":end=").Append(F(s.End))
              .Append(",asetpts=PTS-STARTPTS")
              .Append(",afade=t=in:st=0:d=").Append(F(fade))
              .Append(",afade=t=out:st=").Append(F(length - fade)).Append(":d=").Append(F(fade))
              .Append("[a").Append(i).Append("];\n");

            concatInputs.Append("[v").Append(i).Append("][a").Append(i).Append(']');
        }

        sb.Append(concatInputs).Append("concat=n=").Append(kept.Count).Append(":v=1:a=1[outv][outa]");
        return sb.ToString();
    }

    /// <summary>Total length of the exported video in seconds.</summary>
    public static double KeptDuration(IReadOnlyList<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return segments.Where(s => s.Enabled).Sum(s => s.End - s.Start);
    }

    /// <summary>Microsecond precision, invariant culture, never scientific notation.</summary>
    internal static string F(double seconds) => Math.Round(seconds, 6).ToString("0.######", CultureInfo.InvariantCulture);
}
