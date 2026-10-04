using System.Globalization;
using System.Numerics;
using ImGuiNET;
using MainframeEngine.Gizmos;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// Mirrors the Sandbox's per-frame work — Spine, shadows, sky, grid and the ImGui debug window with
/// light and axis gizmos — for the steady-state allocation gate. Its ImGui text shows timings, so it
/// is not used for golden images.
/// </summary>
public sealed class SandboxScene(HostOptions host) : SpineScene(host)
{
    private AudioPlayer3D? _orbiting;

    protected override void AddNodes(Node scene)
    {
        base.AddNodes(scene);

        // Audio (M7), as in the Sandbox: the streamed ambience on a box-like emitter plus an orbiting, looping
        // in-memory tone, so the allocation gate covers AudioServer.Process with moving positional voices.
        scene.AddChild(new AudioPlayer3D
        {
            Name = "Ambience",
            Stream = new AudioStream { File = "Content/Audio/ambient_hum.ogg", LoadMode = AudioLoadMode.Stream, Loop = true, LoopEnd = 6f },
            Bus = "SFX",
            VolumeDb = -18f,
            Autoplay = true,
            UnitSize = 3f,
            MaxDistance = 60f,
            LowPassAtMaxDistance = 1500f,
            Position = new Vector3(3, 1, 0),
        });

        var tone = new float[48000];
        for (var i = 0; i < tone.Length; i++)
            tone[i] = 0.25f * MathF.Sin(2 * MathF.PI * 220 * i / 48000f);
        _orbiting = new AudioPlayer3D
        {
            Name = "Orbiter",
            Stream = AudioStream.FromSamples(tone, 1, 48000, "orbit tone"),
            Loop = true,
            Autoplay = true,
            UnitSize = 2f,
            DopplerTracking = true,
        };
        scene.AddChild(_orbiting);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        base.UpdateScene(gameTime);
        if (_orbiting is not null)
        {
            var angle = gameTime.FrameCount * 0.02f;
            _orbiting.Position = new Vector3(MathF.Cos(angle) * 6f, 1f, MathF.Sin(angle) * 6f);
        }

        // Self-check (before the measured window): both voices really play, so the gate measures live audio.
        if (gameTime.FrameCount == 60)
        {
            var audio = Servers.Get<AudioServer>();
            if (audio is null)
                Fail("No AudioServer registered.");
            else if (audio.Stats.ActiveVoices < 2 || _orbiting?.Playing != true)
                Fail($"Expected the ambience and the orbiter to play; {audio.Stats.ActiveVoices} active voices.");
            else if (audio.Stats.StreamErrors > 0 || audio.Stats.Faulted)
                Fail("Audio stream errors or a mixer fault.");
        }
    }

    protected override void OnImGui(in GameTime gameTime)
    {
        Lights.DrawLightGizmos(Camera.RenderCamera);

        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always, Vector2.Zero);
        if (ImGui.Begin("Game Window", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Value("FrameCount", gameTime.FrameCount);
            ImGui.Value("DeltaTime", gameTime.DeltaTime);
            ImGui.Value("FPS", gameTime.FramesPerSecond);

            Span<char> text = stackalloc char[64];
            var p = Camera.GlobalPosition;
            if (text.TryWrite(CultureInfo.InvariantCulture, $"Position: <{p.X:0.00}, {p.Y:0.00}, {p.Z:0.00}>", out var written))
                ImGui.TextUnformatted(text[..written]);

            if (Servers.Get<AudioServer>() is { } audio)
                AudioImGui.DrawMixer(audio);
        }
        ImGui.End();

        ImGuiCoordGizmo.DrawCoordinateGizmo(Camera.RenderCamera);
        RendererDebugWindow.Draw(Renderer);
    }
}
