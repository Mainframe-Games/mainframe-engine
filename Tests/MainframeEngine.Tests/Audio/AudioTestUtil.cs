using System.Text;

namespace MainframeEngine.Tests.Audio;

/// <summary>Helpers shared by the audio tests: a manual null-device server, generated sounds, signal measurements.</summary>
internal static class AudioTestUtil
{
    public const int Rate = 48000;

    public static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Content", "Audio", name);

    /// <summary>A server on the manual null device with the default bus layout (nothing renders until asked).</summary>
    public static AudioServer CreateServer(SceneTree? tree = null, AudioBusLayout? layout = null)
    {
        var server = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null }, tree);
        if (layout is not null)
            server.ApplyBusLayout(layout);
        tree?.Servers.Register(server);
        return server;
    }

    public static readonly GameTime Frame = new() { DeltaTime = 1f / 60f, FrameCount = 1 };

    /// <summary>Runs the server's frame and renders <paramref name="frames"/> frames into a new capture buffer.</summary>
    public static float[] Render(AudioServer server, int frames, SceneTree? tree = null)
    {
        if (tree is not null)
            tree.Tick(Frame);
        else
            server.Process(Frame);
        var capture = new float[frames * 2];
        server.RenderNullDevice(frames, capture);
        return capture;
    }

    /// <summary>A mono or stereo sine as an in-memory stream.</summary>
    public static AudioStream Sine(float frequency, float seconds, float amplitude = 0.5f, int rate = Rate, int channels = 1)
    {
        var frames = (int)(seconds * rate);
        var samples = new float[frames * channels];
        for (var i = 0; i < frames; i++)
        {
            var v = amplitude * MathF.Sin(2 * MathF.PI * frequency * i / rate);
            for (var c = 0; c < channels; c++)
                samples[i * channels + c] = v;
        }

        return AudioStream.FromSamples(samples, channels, rate, $"sine {frequency} Hz");
    }

    /// <summary>A constant (DC) signal: exact levels through the whole mix chain.</summary>
    public static AudioStream Constant(float value, float seconds, int rate = Rate)
    {
        var samples = new float[(int)(seconds * rate)];
        Array.Fill(samples, value);
        return AudioStream.FromSamples(samples, 1, rate, $"dc {value}");
    }

    public static float Peak(ReadOnlySpan<float> stereo, int channel)
    {
        var peak = 0f;
        for (var i = channel; i < stereo.Length; i += 2)
            peak = Math.Max(peak, Math.Abs(stereo[i]));
        return peak;
    }

    public static float Rms(ReadOnlySpan<float> stereo, int channel)
    {
        double sum = 0;
        var n = 0;
        for (var i = channel; i < stereo.Length; i += 2, n++)
            sum += stereo[i] * stereo[i];
        return n == 0 ? 0 : (float)Math.Sqrt(sum / n);
    }

    /// <summary>Frequency from rising zero crossings (good to a few Hz over ≥ 0.1 s of a clean tone).</summary>
    public static float Frequency(ReadOnlySpan<float> interleaved, int channels, int channel, int rate)
    {
        int first = -1, last = -1, crossings = 0;
        var frames = interleaved.Length / channels;
        for (var f = 1; f < frames; f++)
        {
            var a = interleaved[(f - 1) * channels + channel];
            var b = interleaved[f * channels + channel];
            if (a < 0 && b >= 0)
            {
                if (first < 0)
                    first = f;
                else
                    crossings++;
                last = f;
            }
        }

        return crossings == 0 ? 0 : crossings * (float)rate / (last - first);
    }

    /// <summary>Writes a WAV file (PCM 8/16/24/32 or float 32/64) for decoder tests.</summary>
    public static void WriteWav(string path, float[] interleaved, int channels, int rate, int bits, bool isFloat, bool extensible = false)
    {
        var bytesPerSample = bits / 8;
        var dataBytes = interleaved.Length * bytesPerSample;
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream, Encoding.ASCII);
        var fmtSize = extensible ? 40 : 16;
        w.Write("RIFF"u8);
        w.Write(4 + 8 + fmtSize + 8 + 8 + dataBytes + 8 + 4);
        w.Write("WAVE"u8);
        w.Write("junk"u8); // an unknown chunk the decoder must skip
        w.Write(4);
        w.Write(0);
        w.Write("fmt "u8);
        w.Write(fmtSize);
        w.Write((ushort)(extensible ? 0xFFFE : isFloat ? 3 : 1));
        w.Write((ushort)channels);
        w.Write(rate);
        w.Write(rate * channels * bytesPerSample);
        w.Write((ushort)(channels * bytesPerSample));
        w.Write((ushort)bits);
        if (extensible)
        {
            w.Write((ushort)22);
            w.Write((ushort)bits);
            w.Write(0); // channel mask
            w.Write((ushort)(isFloat ? 3 : 1));
            w.Write(new byte[14]); // rest of the sub-format GUID
        }

        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (var s in interleaved)
        {
            switch (isFloat, bits)
            {
                case (true, 32):
                    w.Write(s);
                    break;
                case (true, 64):
                    w.Write((double)s);
                    break;
                case (false, 8):
                    w.Write((byte)Math.Clamp((int)MathF.Round(s * 127f + 128f), 0, 255));
                    break;
                case (false, 16):
                    w.Write((short)Math.Clamp((int)MathF.Round(s * 32767f), short.MinValue, short.MaxValue));
                    break;
                case (false, 24):
                    var v = Math.Clamp((int)MathF.Round(s * 8388607f), -8388608, 8388607);
                    w.Write((byte)(v & 0xFF));
                    w.Write((byte)((v >> 8) & 0xFF));
                    w.Write((byte)((v >> 16) & 0xFF));
                    break;
                default:
                    w.Write((int)Math.Clamp(Math.Round(s * 2147483647.0), int.MinValue, int.MaxValue));
                    break;
            }
        }

        w.Write("LIST"u8); // trailing chunk after the data
        w.Write(4);
        w.Write(0);
    }
}
