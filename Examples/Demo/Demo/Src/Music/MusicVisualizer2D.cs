using System.Numerics;
using MainframeEngine;
using MainframeEngine.Audio.Music;

namespace Demo;

/// <summary>
/// Draws the adaptive music of its sibling <c>Music</c> player: the playable sections as blocks (the one playing lit, the
/// one a transition is scheduled to outlined), the current bar's beats (the beat playing pulses on the player's
/// <see cref="AdaptiveMusicPlayer.Beat"/> signal), and one bar per layer whose height is the layer's gain and whose
/// brightness follows its low-pass cutoff. It reads the player every frame and allocates nothing.
/// </summary>
public sealed class MusicVisualizer2D : Node2D
{
    public static readonly Vector2 Center = new(170f, 10f);

    private static readonly Vector4[] LayerColors =
    [
        new(0.36f, 0.6f, 1f, 1f),   // Main: bass and kick
        new(0.98f, 0.7f, 0.2f, 1f), // Perc: hats
        new(0.93f, 0.33f, 0.6f, 1f), // Lead
    ];

    private const float BlockWidth = 220f, BlockHeight = 46f, BlockGap = 24f, BlocksY = -200f;
    private const float BarWidth = 120f, BarGap = 40f, MaxHeight = 260f, BaseY = 300f;

    private AdaptiveMusicPlayer? _player;
    private Font? _font;
    private float _pulse;
    private int _beat;
    private bool _downbeat;

    protected override void OnReady()
    {
        base.OnReady();
        _player = Parent?.GetNodeOrNull<AdaptiveMusicPlayer>("Music");
        // The engine's UI font for the captions (loaded here, not saved with the scene).
        var font = ContentPaths.Resolve("Assets/UI/fonts/LatoLatin-Regular.ttf");
        if (File.Exists(font))
            _font = Font.FromFile(font);
        if (_player is not null)
            _player.Beat += OnBeat;
    }

    protected override void OnExitTree()
    {
        if (_player is not null)
            _player.Beat -= OnBeat;
        _player = null;
        base.OnExitTree();
    }

    private void OnBeat(int bar, int beat)
    {
        _beat = beat;
        _downbeat = beat == 1;
        _pulse = 1f;
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        _pulse = MathF.Max(0f, _pulse - gameTime.DeltaTime * 3f);
        QueueRedraw();
    }

    protected override void OnDraw()
    {
        if (_player?.Instance?.Director is not { } director)
            return;
        var plan = director.Plan;
        var current = _player.CurrentSection;
        var pending = _player.PendingTransition;

        // The sections: Intro, Explore, Combat (stingers are not places).
        var playable = 0;
        foreach (var section in plan.Sections)
        {
            if (section.Kind == MusicSectionKind.Section)
                playable++;
        }

        var index = 0;
        foreach (var section in plan.Sections)
        {
            if (section.Kind != MusicSectionKind.Section)
                continue;
            var playing = string.Equals(section.Id, current, StringComparison.Ordinal);
            var color = SectionColor(section.Id);
            var center = SectionCenter(index++, playable);
            var rect = new Rect2(center - new Vector2(BlockWidth, BlockHeight) / 2, new Vector2(BlockWidth, BlockHeight));
            DrawRect(rect, color with { W = playing ? 0.55f + 0.35f * _pulse * (_downbeat ? 1f : 0.4f) : 0.12f });
            DrawRect(rect, color with { W = playing ? 1f : 0.35f }, filled: false, width: playing ? 3f : 1.5f);
            if (string.Equals(section.Id, pending, StringComparison.Ordinal))
                DrawRect(new Rect2(rect.Position - new Vector2(6, 6), rect.Size + new Vector2(12, 12)), new Vector4(1f, 0.85f, 0.3f, 0.5f + 0.5f * _pulse), filled: false, width: 2f);
            Caption(section.Name, center + new Vector2(0, 8), 22, playing ? 1f : 0.55f);
        }

        // The bar's beats.
        var beats = Math.Max(1, plan.Grid.BeatsPerBar);
        for (var b = 1; b <= beats; b++)
        {
            var at = new Vector2(Center.X + (b - (beats + 1) / 2f) * 70f, Center.Y - 90f);
            var on = b == _beat;
            DrawCircle(at, on ? 16f + 10f * _pulse : 14f, on ? new Vector4(1f, 1f, 1f, 0.5f + 0.5f * _pulse) : new Vector4(1f, 1f, 1f, 0.15f));
        }

        // The layers: height from the gain (−60…0 dB), brightness from the cutoff.
        var layers = plan.Layers.Length;
        for (var l = 0; l < layers; l++)
        {
            var level = Math.Clamp((director.LayerGainDb(l) + 60f) / 60f, 0f, 1f);
            var open = plan.Layers[l].Filter == MusicLayerFilter.None ? 1f : Math.Clamp(MathF.Log(director.LayerCutoffHz(l) / 200f) / MathF.Log(100f), 0.2f, 1f);
            var color = LayerColors[l % LayerColors.Length];
            var left = LayerCenter(l, layers).X - BarWidth / 2;
            DrawRect(new Rect2(new Vector2(left, BaseY - MaxHeight), new Vector2(BarWidth, MaxHeight)), color with { W = 0.08f });
            var height = level * MaxHeight * (0.92f + 0.08f * _pulse);
            DrawRect(new Rect2(new Vector2(left, BaseY - height), new Vector2(BarWidth, height)), color * new Vector4(open, open, open, 0.9f));
            Caption(plan.Layers[l].Name, LayerCenter(l, layers) + new Vector2(0, 34), 20, 0.4f + 0.6f * level);
        }
    }

    // Text centred on a point (its baseline a little below), when the font loaded.
    private void Caption(string text, Vector2 center, int size, float alpha)
    {
        if (_font is { } font)
            DrawString(font, center - new Vector2(BarWidth, 0), text, HorizontalAlignment.Center, BarWidth * 2, size, new Vector4(0.9f, 0.93f, 1f, alpha));
    }

    /// <summary>The centre of playable section <paramref name="index"/>'s block (of <paramref name="count"/>).</summary>
    public static Vector2 SectionCenter(int index, int count) =>
        new(Center.X + (index - (count - 1) / 2f) * (BlockWidth + BlockGap), Center.Y + BlocksY);

    /// <summary>The bottom centre of layer <paramref name="index"/>'s bar (of <paramref name="count"/>).</summary>
    public static Vector2 LayerCenter(int index, int count) =>
        new(Center.X + (index - (count - 1) / 2f) * (BarWidth + BarGap), BaseY);

    private static Vector4 SectionColor(string id) => id switch
    {
        "combat" => new Vector4(0.94f, 0.27f, 0.27f, 1f),
        "explore" => new Vector4(0.23f, 0.51f, 0.96f, 1f),
        _ => new Vector4(0.6f, 0.65f, 0.75f, 1f),
    };
}
