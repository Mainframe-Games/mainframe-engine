using System.Numerics;
using MainframeEngine;
using Silk.NET.Input;

namespace Demo;

/// <summary>Click anywhere: plays a ZzFX blip at the click position and drops a fading ring.</summary>
public sealed class ClickToPlay2D : Node2D
{
    private static AudioStream? s_blip;

    protected override void OnUnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton { Button: MouseButton.Left, Pressed: true } click)
            return;
        AddChild(new FadingMarker(s_blip ??= Blip()) { Position = CanvasInput.WindowToCanvas(this, click.Position) });
    }

    // A ZzFX blip (ZzfxPresets.Blip): synthesised on first play, a slightly different pitch every click.
    private static ZzfxStream Blip() => new()
    {
        ResourceName = "Blip",
        Parameters = ZzfxPresets.Blip(new Random(7)) with { Randomness = 0.1f },
    };

    /// <summary>The ring, and the positional blip that plays from its centre (AudioServer one-shots are 3D-only).</summary>
    private sealed class FadingMarker : Node2D
    {
        private float _life = 1f;
        private readonly AudioStream _blip;

        public FadingMarker(AudioStream blip) => _blip = blip;

        protected override void OnReady() =>
            AddChild(new AudioPlayer2D { Stream = _blip, Bus = "SFX", VolumeDb = -4f, Autoplay = true });

        protected override void OnProcess(in GameTime gameTime)
        {
            _life -= gameTime.DeltaTime;
            if (_life <= 0f)
                QueueFree();
            else
                QueueRedraw();
        }

        protected override void OnDraw() =>
            DrawCircle(Vector2.Zero, 18f + (1f - _life) * 40f, new Vector4(0.98f, 0.8f, 0.2f, MathF.Max(_life, 0f)),
                filled: false, width: 3f, antialiased: true);
    }
}
