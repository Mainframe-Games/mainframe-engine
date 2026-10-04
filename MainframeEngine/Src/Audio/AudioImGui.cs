using System.Globalization;
using System.Numerics;
using ImGuiNET;

namespace MainframeEngine;

/// <summary>
/// ImGui debug widgets for the <see cref="AudioServer"/>: a bus mixer (fader, mute, solo, peak meter per bus) and
/// the server's counters. Allocation-free, so it can sit in a steady-state frame.
/// </summary>
public static class AudioImGui
{
    /// <summary>Draws the audio section into the current ImGui window.</summary>
    public static void DrawMixer(AudioServer audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ImGui.SeparatorText("Audio");
        ImGui.TextUnformatted(audio.DeviceName);

        var stats = audio.Stats;
        Span<char> text = stackalloc char[96];
        if (text.TryWrite(CultureInfo.InvariantCulture,
                $"Voices {stats.ActiveVoices}/{stats.TotalVoices}  steals {stats.Steals}  underruns {stats.Underruns}", out var written))
            ImGui.TextUnformatted(text[..written]);

        var buses = audio.Buses;
        for (var i = 0; i < buses.Count; i++)
        {
            var bus = buses[i];
            ImGui.PushID(i);

            var volume = bus.VolumeDb;
            ImGui.SetNextItemWidth(150);
            if (ImGui.SliderFloat(bus.Name, ref volume, -60f, 6f, "%.1f dB"))
                bus.VolumeDb = volume <= -60f ? AudioMath.SilenceDb : volume;

            ImGui.SameLine();
            var mute = bus.Mute;
            if (ImGui.Checkbox("M", ref mute))
                bus.Mute = mute;

            ImGui.SameLine();
            var solo = bus.Solo;
            if (ImGui.Checkbox("S", ref solo))
                bus.Solo = solo;

            ImGui.SameLine();
            // Meter on a dB scale: −60 dB .. 0 dB.
            var level = Math.Clamp((AudioMath.LinearToDb(bus.Peak) + 60f) / 60f, 0f, 1f);
            ImGui.ProgressBar(level, new Vector2(70, 0), string.Empty);

            ImGui.PopID();
        }
    }
}
