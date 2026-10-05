using System.Text;
using MainframeEngine.Serialization;

namespace MainframeEngine.Tests.Scene;

/// <summary>A game value type marked [SerializableValue] with a registered codec is storable in resources.</summary>
public sealed class SerializableValueTests
{
    [SerializableValue]
    public readonly record struct Rgba(float R, float G, float B, float A);

    public sealed class Swatch : Resource
    {
        [Export]
        public Rgba Colour { get; set; } = new(1, 1, 1, 1);

        [Export]
        public Rgba[] Palette { get; set; } = [];
    }

    [Fact]
    public void RoundTripsThroughAResourceFile()
    {
        Codecs.Register(Codecs.FloatArray<Rgba>(4, static (c, d) => { d[0] = c.R; d[1] = c.G; d[2] = c.B; d[3] = c.A; },
            static f => new Rgba(f[0], f[1], f[2], f[3])));
        var swatch = new Swatch { Colour = new Rgba(0.1f, 0.2f, 0.3f, 1), Palette = [new Rgba(1, 0, 0, 1), new Rgba(0, 0, 1, 0.5f)] };
        var path = Path.Combine(Path.GetTempPath(), $"mf-swatch-{Guid.NewGuid():N}.mres");
        try
        {
            ResourceSaver.Save(swatch, path);
            Assert.Contains("0.1", File.ReadAllText(path, Encoding.UTF8), StringComparison.Ordinal);
            var copy = ResourceLoader.Load<Swatch>(path);
            Assert.Equal(swatch.Colour, copy.Colour);
            Assert.Equal(swatch.Palette, copy.Palette);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
