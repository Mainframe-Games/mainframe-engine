using System.Globalization;
using System.Text.Json;

namespace MainframeEngine.Video.Media;

/// <summary>
/// Reads the output of <c>ffprobe -print_format json -show_format -show_streams</c> into a <see cref="MediaInfo"/>. This is
/// all the library needs from probing, so it parses the JSON itself rather than taking a dependency on a wrapper package.
/// </summary>
internal static class FfprobeJson
{
    /// <exception cref="MediaProbeException">The output is not JSON, has no video stream, or reports no usable duration.</exception>
    public static MediaInfo Parse(string json, string path)
    {
        ArgumentNullException.ThrowIfNull(json);
        var name = Path.GetFileName(path);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new MediaProbeException($"ffprobe could not read \"{name}\". Its output was not understood.\n\n{ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new MediaProbeException($"ffprobe could not read \"{name}\". Its output was not understood.");
            }

            JsonElement? video = null;
            JsonElement? audio = null;
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = String(stream, "codec_type");
                    if (type == "video")
                    {
                        video ??= stream;
                    }
                    else if (type == "audio")
                    {
                        audio ??= stream;
                    }
                }
            }

            if (video is not { } v)
            {
                throw new MediaProbeException($"\"{name}\" has no video stream.");
            }

            var duration = root.TryGetProperty("format", out var format) ? Number(format, "duration") : double.NaN;
            if (!double.IsFinite(duration) || duration <= 0)
            {
                throw new MediaProbeException($"\"{name}\" reports no duration. Live streams and some raw formats are not supported.");
            }

            // Phone recordings store landscape frames with a rotation; present the displayed size.
            var rotated = Math.Abs((int)Math.Round(Rotation(v))) % 180 == 90;
            var width = (int)Number(v, "width", 0);
            var height = (int)Number(v, "height", 0);

            // r_frame_rate can be a bogus 90000 for variable-rate sources; prefer the average when it is sane.
            var average = Fraction(String(v, "avg_frame_rate"));
            var frameRate = average is > 0 and < 1000 ? average : Fraction(String(v, "r_frame_rate"));

            return new MediaInfo(
                duration,
                rotated ? height : width,
                rotated ? width : height,
                frameRate,
                HasAudio: audio is not null,
                VideoCodec: String(v, "codec_name"),
                AudioCodec: audio is { } a ? String(a, "codec_name") : null);
        }
    }

    /// <summary>The stream's rotation in degrees: a display-matrix entry in <c>side_data_list</c>, else the older <c>tags.rotate</c>.</summary>
    private static double Rotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in sideData.EnumerateArray())
            {
                var rotation = Number(entry, "rotation");
                if (double.IsFinite(rotation))
                {
                    return rotation;
                }
            }
        }

        if (stream.TryGetProperty("tags", out var tags))
        {
            var rotate = Number(tags, "rotate");
            if (double.IsFinite(rotate))
            {
                return rotate;
            }
        }

        return 0;
    }

    /// <summary>A rational such as <c>30000/1001</c>, or a plain number. 0 when it is missing, malformed or has a zero denominator.</summary>
    private static double Fraction(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var parts = text.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator))
        {
            return denominator == 0 ? 0 : numerator / denominator;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain) ? plain : 0;
    }

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A property that ffprobe writes as a JSON number or as a string holding one; <paramref name="missing"/> when it is neither.</summary>
    private static double Number(JsonElement element, string property, double missing = double.NaN)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return missing;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => missing,
        };
    }
}
