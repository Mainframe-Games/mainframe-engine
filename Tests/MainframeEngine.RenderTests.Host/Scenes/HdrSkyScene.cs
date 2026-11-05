using System.Numerics;
using System.Text;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// G6.2: an HDR panorama (a Radiance <c>.hdr</c> written at load: a blue-to-white sky, a dark ground and a small sun of
/// radiance 60) lights and is reflected by PBR spheres (mirror metal, brushed metal, glossy and rough dielectrics). The
/// sun's reflection only stays brighter than white because the panorama is decoded as half floats. Checks the sky image
/// is RGBA16F and that the sky lighting was captured.
/// </summary>
public sealed class HdrSkyScene(HostOptions host) : RenderTestGame(host)
{
    private WorldEnvironment _environment = null!;
    private string _path = "";

    protected override void LoadScene()
    {
        _path = Path.Combine(Path.GetTempPath(), $"mf-hdr-sky-{Environment.ProcessId}.hdr");
        WritePanorama(_path, 128, 64);

        var scene = new Node3D { Name = nameof(HdrSkyScene) };
        var camera = new Camera3D { Name = "Camera", Position = new Vector3(0, 0.4f, 5.5f) };
        camera.LookAt(new Vector3(0, 0.2f, 0));
        scene.AddChild(camera);

        _environment = new WorldEnvironment
        {
            Name = "Environment",
            Sky = new Sky { Mode = SkyEnvironmentType.Panoramic, Panorama = _path, RadianceSize = SkyRadianceSize.Size64 },
            AmbientSource = AmbientSource.Sky,
        };
        scene.AddChild(_environment);

        var sphere = new SphereMesh { Radius = 0.55f, Height = 1.1f, RadialSegments = 48, Rings = 24 };
        (float Metallic, float Roughness)[] looks = [(1f, 0.02f), (1f, 0.35f), (0f, 0.1f), (0f, 0.8f)];
        for (var i = 0; i < looks.Length; i++)
        {
            scene.AddChild(new MeshInstance3D
            {
                Name = $"Sphere{i}",
                Position = new Vector3((i - 1.5f) * 1.3f, 0.2f, 0),
                Mesh = sphere,
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = ShadingMode.Pbr,
                    AlbedoColor = i < 2 ? Color.FromArgb(255, 230, 230, 235) : Color.FromArgb(255, 180, 40, 30),
                    Metallic = looks[i].Metallic,
                    Roughness = looks[i].Roughness,
                },
            });
        }

        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (gameTime.FrameCount != 3)
            return;
        if (_environment.SkyEnvironment?.TextureFormat is not Silk.NET.Vulkan.Format.R16G16B16A16Sfloat)
            Fail($"the .hdr panorama is {_environment.SkyEnvironment?.TextureFormat}, not RGBA16F");
        if (_environment.Radiance is not { IsBaked: true, Size: 64 })
            Fail("the HDR sky was not captured at radiance size 64");
    }

    protected override void DisposeScene() => File.Delete(_path);

    // An equirectangular sky: zenith blue to a white horizon above, dark brown ground below, a 60× sun at azimuth -40°,
    // elevation 35°. Flat (uncompressed) RGBE rows, as stb reads for any width.
    private static void WritePanorama(string path, int width, int height)
    {
        var sun = Vector3.Normalize(new Vector3(MathF.Sin(-0.7f) * MathF.Cos(0.61f), MathF.Sin(0.61f), -MathF.Cos(-0.7f) * MathF.Cos(0.61f)));
        using var stream = File.Create(path);
        stream.Write(Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {height} +X {width}\n"));
        var texel = new byte[4];
        for (var y = 0; y < height; y++)
        {
            var elevation = (0.5f - (y + 0.5f) / height) * MathF.PI;
            for (var x = 0; x < width; x++)
            {
                var azimuth = ((x + 0.5f) / width - 0.5f) * MathF.Tau;
                var dir = new Vector3(MathF.Sin(azimuth) * MathF.Cos(elevation), MathF.Sin(elevation), -MathF.Cos(azimuth) * MathF.Cos(elevation));
                Vector3 c;
                if (elevation < 0f)
                {
                    c = new Vector3(0.08f, 0.06f, 0.04f);
                }
                else
                {
                    var t = MathF.Pow(1f - elevation / (MathF.PI / 2f), 3f);
                    c = Vector3.Lerp(new Vector3(0.15f, 0.3f, 0.75f), new Vector3(1f, 1f, 1f), t);
                }

                if (Vector3.Dot(dir, sun) > 0.995f)
                    c = new Vector3(60f, 56f, 50f);
                Rgbe(c, texel);
                stream.Write(texel);
            }
        }
    }

    private static void Rgbe(Vector3 c, byte[] texel)
    {
        var max = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        if (max < 1e-32f)
        {
            Array.Clear(texel);
            return;
        }

        var exponent = (int)MathF.Floor(MathF.Log2(max)) + 1; // frexp: max = m · 2^e, m in [0.5, 1)
        var scale = 256f / MathF.Pow(2f, exponent);
        texel[0] = (byte)Math.Clamp(c.X * scale, 0f, 255f);
        texel[1] = (byte)Math.Clamp(c.Y * scale, 0f, 255f);
        texel[2] = (byte)Math.Clamp(c.Z * scale, 0f, 255f);
        texel[3] = (byte)(exponent + 128);
    }
}
