using System.Numerics;
using MainframeEngine;
using DrawingColor = System.Drawing.Color;

namespace Neon;

/// <summary>
/// The TV's picture: a 2D sub-viewport (<see cref="Width"/> × <see cref="Height"/>) drawing the <see cref="Tv"/>'s channel
/// (0: the news with a ticker, 1: ads for made-up brands, 2: a synthwave music video). The TV's screen shows it through
/// <see cref="Tv.ChannelTexture"/>. Nothing allocates per frame.
/// </summary>
public sealed class TvFeed : Node
{
    public const int Width = 960;
    public const int Height = 540;

    private SubViewport? _viewport;

    public Tv? Tv { get; set; }

    protected override void OnReady()
    {
        if (_viewport is not null || Tv is not { } tv)
            return;
        _viewport = new SubViewport { Name = "Feed", Width = Width, Height = Height, Disable3D = true, ClearColor = DrawingColor.Black };
        _viewport.AddChild(new Picture { Name = "Picture", Tv = tv });
        AddChild(_viewport);
        var texture = _viewport.GetTexture();
        tv.ChannelTexture = _ => texture;
        tv.Brightness = 2.6f;
    }

    private sealed class Picture : Node2D
    {
        private static readonly string[] Headlines =
        [
            "ACID RAIN WARNING EXTENDED TO SECTOR 12",
            "MOMOTEK RECALLS 40,000 OPTIC IMPLANTS",
            "CURFEW: 14 DAYS REMAIN FOR BLOCK K-9",
            "SKY LANE 4 REOPENS AFTER DRONE SWARM",
        ];

        private static readonly (string Brand, string Line, Vector4 Colour)[] Ads =
        [
            ("ZEN-ZERO", "soda that tastes like nothing at all", new Vector4(0.2f, 1f, 0.75f, 1f)),
            ("MOMOTEK", "see further. see everything.", new Vector4(1f, 0.3f, 0.6f, 1f)),
            ("SKYLINE", "hover leasing from 0% apr*", new Vector4(1f, 0.75f, 0.2f, 1f)),
        ];

        private const string Ticker = "   NIKKEI-9 +2.4%   //   RAIN 94%   //   LANE 4 OPEN   //   CURFEW IN EFFECT   //   MOMOTEK -11%   //   ZEN-ZERO NOW IN GRAPE   //";

        private readonly Vector2[] _grid = new Vector2[2];
        private float _time;

        public Tv? Tv { get; set; }

        protected override void OnProcess(in GameTime gameTime)
        {
            if (Tv is not { IsOn: true })
                return;
            _time += gameTime.DeltaTime;
            QueueRedraw();
        }

        protected override void OnDraw()
        {
            if (Tv is not { } tv)
                return;
            switch (tv.Channel)
            {
                case 0:
                    News();
                    break;
                case 1:
                    Ad();
                    break;
                default:
                    MusicVideo();
                    break;
            }

            // Scanlines over every channel.
            for (var y = 0; y < Height; y += 4)
                DrawRect(new Rect2(0, y, Width, 1), new Vector4(0f, 0f, 0f, 0.18f));
        }

        private void News()
        {
            DrawRect(new Rect2(0, 0, Width, Height), new Vector4(0.03f, 0.06f, 0.16f, 1f));
            for (var i = 0; i < 6; i++)
                DrawRect(new Rect2(520 + i * 70, 60, 50, 300), new Vector4(0.06f, 0.12f, 0.3f, 1f));
            // The anchor: a silhouette at a desk.
            DrawCircle(new Vector2(300, 190), 58, new Vector4(0.75f, 0.6f, 0.55f, 1f));
            DrawRect(new Rect2(200, 250, 200, 170), new Vector4(0.12f, 0.12f, 0.16f, 1f));
            DrawRect(new Rect2(120, 380, 360, 60), new Vector4(0.2f, 0.22f, 0.3f, 1f));
            DrawRect(new Rect2(40, 30, 150, 44), new Vector4(0.9f, 0.1f, 0.2f, 1f));
            DrawString(Screens.Mono, new Vector2(54, 62), "NEWS 24", fontSize: 26);
            var headline = Headlines[(int)(_time / 5f) % Headlines.Length];
            DrawRect(new Rect2(0, 440, Width, 50), new Vector4(0.9f, 0.9f, 0.95f, 1f));
            DrawString(Screens.Mono, new Vector2(20, 474), headline, fontSize: 24, modulate: new Vector4(0.05f, 0.05f, 0.1f, 1f));
            DrawRect(new Rect2(0, 490, Width, 50), new Vector4(0.9f, 0.1f, 0.2f, 1f));
            var tickerWidth = Screens.Mono.GetStringSize(Ticker, fontSize: 22).X;
            var x = Width - (_time * 120f % (tickerWidth + Width));
            DrawString(Screens.Mono, new Vector2(x, 523), Ticker, fontSize: 22);
        }

        private void Ad()
        {
            var (brand, line, colour) = Ads[(int)(_time / 4f) % Ads.Length];
            var pulse = 0.5f + 0.5f * MathF.Sin(_time * 3f);
            DrawRect(new Rect2(0, 0, Width, Height), new Vector4(0.02f, 0.02f, 0.04f, 1f));
            DrawCircle(new Vector2(Width * 0.5f, Height * 0.42f), 150 + 20 * pulse, colour * new Vector4(0.35f, 0.35f, 0.35f, 1f));
            DrawCircle(new Vector2(Width * 0.5f, Height * 0.42f), 110, colour);
            DrawString(Screens.Mono, new Vector2(0, 420), brand, HorizontalAlignment.Center, Width, 64, colour);
            DrawString(Screens.Mono, new Vector2(0, 470), line, HorizontalAlignment.Center, Width, 24, new Vector4(0.9f, 0.9f, 0.95f, 1f));
        }

        private void MusicVideo()
        {
            DrawRect(new Rect2(0, 0, Width, Height), new Vector4(0.08f, 0.02f, 0.14f, 1f));
            for (var i = 0; i < 8; i++) // a striped sun
            {
                var y = 120 + i * 22f;
                DrawRect(new Rect2(330, y, 300, 14 - i), new Vector4(1f, 0.3f + i * 0.08f, 0.6f - i * 0.05f, 1f));
            }

            var horizon = 300f;
            for (var i = 0; i < 12; i++) // the grid floor rushing towards the viewer
            {
                var t = ((i + _time * 1.5f) % 12f) / 12f;
                var y = horizon + t * t * (Height - horizon);
                _grid[0] = new Vector2(0, y);
                _grid[1] = new Vector2(Width, y);
                DrawPolyline(_grid, new Vector4(1f, 0.2f, 0.85f, 1f), 2f);
            }

            for (var i = -10; i <= 10; i++)
                DrawLine(new Vector2(Width / 2f + i * 12f, horizon), new Vector2(Width / 2f + i * 110f, Height), new Vector4(1f, 0.2f, 0.85f, 1f), 2f);
            DrawString(Screens.Mono, new Vector2(30, 60), "VOLT SEVEN", fontSize: 40, modulate: new Vector4(0.4f, 1f, 1f, 1f));
            DrawString(Screens.Mono, new Vector2(30, 92), "night drive (official)", fontSize: 20);
        }
    }
}
