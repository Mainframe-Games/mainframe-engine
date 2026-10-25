using System.Numerics;
using System.Text;
using System.Text.Json;

namespace MainframeEngine.Tests.TestAssets;

/// <summary>
/// Generates the skinned test character (CC0, ADR 0207/0208): <c>character.glb</c> with its <c>.meta</c>, laid out the way
/// Blender's glTF exporter writes a rigged character. The committed copy lives in <c>Tests/Assets/Models/Character</c> (the
/// unit, render and benchmark tests and the Demo import it); <c>TheCommittedCharacterMatchesTheGenerator</c> keeps the two in
/// sync (<c>UPDATE_TEST_ASSETS=1</c> rewrites it).
/// </summary>
/// <remarks>
/// <para>Content: a node <c>Armature</c> whose child <c>Hips</c> roots a 31-bone humanoid (spine, neck, head, shoulders,
/// arms, hands with two-bone thumbs and index fingers, legs, feet, toes; rests are translations, the T-pose), and a sibling
/// node <c>Body</c> with one skinned mesh of two primitives (<c>Suit</c> and <c>Skin</c> materials, about 6 000 triangles):
/// tapered tubes along the bones with joints blended over the ends, at most four weights per vertex.</para>
/// <para>Eight actions: <c>idle</c>, <c>walk</c>, <c>run</c> (looping through <c>extras.loop</c>), <c>roll</c>,
/// <c>swing</c> (a <c>HammerFist</c> event at 0.4 s in <c>extras.events</c>, the object form), <c>craft</c> (looped and
/// given a <c>Tap</c> event by the <c>.meta</c> clips), <c>hit</c> and <c>down</c>; <c>run</c> has <c>Step</c> events in the
/// array form. Every value is computed in doubles and quantised to 1/4096 so the file is identical on every OS.</para>
/// </remarks>
public static class SkinnedCharacter
{
    public const string File = "character.glb";
    public const string Uid = "mdl_c4a2ac7e2000";
    public const int BoneCount = 31;
    public const string HandBone = "Hand.R";
    public const float SwingEventTime = 0.4f;

    public static readonly string[] Clips = ["idle", "walk", "run", "roll", "swing", "craft", "hit", "down"];

    private const int Radial = 16;

    private sealed record Bone(string Name, int Parent, Vector3 Offset);

    // Rest offsets from the parent (metres, +Y up, the character faces +Z, its left is +X).
    private static readonly Bone[] Bones = BuildBones();

    private static Bone[] BuildBones()
    {
        var bones = new List<Bone>
        {
            new("Hips", -1, new Vector3(0, 1.0f, 0)),
            new("Spine", 0, new Vector3(0, 0.12f, 0)),
            new("Chest", 1, new Vector3(0, 0.15f, 0)),
            new("UpperChest", 2, new Vector3(0, 0.15f, 0)),
            new("Neck", 3, new Vector3(0, 0.12f, 0)),
            new("Head", 4, new Vector3(0, 0.08f, 0)),
            new("HeadTop", 5, new Vector3(0, 0.22f, 0)),
        };
        foreach (var (side, x) in new[] { ("L", 1f), ("R", -1f) })
        {
            var shoulder = bones.Count;
            bones.Add(new($"Shoulder.{side}", 3, new Vector3(0.06f * x, 0.10f, 0)));
            bones.Add(new($"UpperArm.{side}", shoulder, new Vector3(0.12f * x, 0, 0)));
            bones.Add(new($"LowerArm.{side}", shoulder + 1, new Vector3(0.28f * x, 0, 0)));
            bones.Add(new($"Hand.{side}", shoulder + 2, new Vector3(0.26f * x, 0, 0)));
            bones.Add(new($"Thumb1.{side}", shoulder + 3, new Vector3(0.03f * x, 0, 0.04f)));
            bones.Add(new($"Thumb2.{side}", shoulder + 4, new Vector3(0.04f * x, 0, 0.02f)));
            bones.Add(new($"Index1.{side}", shoulder + 3, new Vector3(0.09f * x, 0, 0.02f)));
            bones.Add(new($"Index2.{side}", shoulder + 6, new Vector3(0.04f * x, 0, 0)));
        }

        foreach (var (side, x) in new[] { ("L", 1f), ("R", -1f) })
        {
            var upper = bones.Count;
            bones.Add(new($"UpperLeg.{side}", 0, new Vector3(0.1f * x, -0.05f, 0)));
            bones.Add(new($"LowerLeg.{side}", upper, new Vector3(0, -0.43f, 0)));
            bones.Add(new($"Foot.{side}", upper + 1, new Vector3(0, -0.42f, 0)));
            bones.Add(new($"Toe.{side}", upper + 2, new Vector3(0, -0.06f, 0.12f)));
        }

        return [.. bones];
    }

    private static int B(string name) => Array.FindIndex(Bones, b => b.Name == name);

    private static Vector3 GlobalRest(int bone)
    {
        var p = Vector3.Zero;
        for (var b = bone; b >= 0; b = Bones[b].Parent)
            p += Bones[b].Offset;
        return p;
    }

    /// <summary>Writes <c>character.glb</c> and its <c>.meta</c> into <paramref name="directory"/>.</summary>
    public static void Write(string directory)
    {
        Directory.CreateDirectory(directory);
        System.IO.File.WriteAllBytes(Path.Combine(directory, File), BuildGlb());
        System.IO.File.WriteAllText(Path.Combine(directory, File + ".meta"),
            $$"""
            {
              "uid": "{{Uid}}",
              "importer": "model",
              "settings": {
                "scale": 1,
                "clips": [
                  { "name": "craft", "loop": "Linear", "events": [ { "name": "Tap", "time": 0.5 } ] }
                ]
              }
            }

            """.ReplaceLineEndings("\n"));
    }

    // ── Mesh ─────────────────────────────────────────────────────────────────

    private sealed class Primitive
    {
        public readonly List<Vector3> Positions = [];
        public readonly List<Vector3> Normals = [];
        public readonly List<Vector2> UVs = [];
        public readonly List<(int J0, int J1, float W0, float W1)> Influences = [];
        public readonly List<int> Indices = [];
    }

    // A tube from bone `bone`'s head to `end` (skeleton space): rings blend into the parent near the start and into
    // `child` near the end. Radii taper from r0 to r1; both ends are capped.
    private static void Tube(Primitive p, int bone, Vector3 end, int child, float r0, float r1, int rings)
    {
        var start = GlobalRest(bone);
        var axis = end - start;
        var length = axis.Length();
        var dir = axis / length;
        var helper = MathF.Abs(dir.Y) > 0.9f ? Vector3.UnitZ : Vector3.UnitY;
        var u = Vector3.Normalize(Vector3.Cross(helper, dir));
        var v = Vector3.Cross(dir, u);
        var parent = Bones[bone].Parent;

        (int, int, float, float) Influence(double t)
        {
            if (t < 0.25 && parent >= 0)
            {
                var w = 0.5 * (1 - t / 0.25);
                return (bone, parent, (float)(1 - w), (float)w);
            }

            if (t > 0.75 && child >= 0)
            {
                var w = 0.5 * ((t - 0.75) / 0.25);
                return (bone, child, (float)(1 - w), (float)w);
            }

            return (bone, bone, 1f, 0f);
        }

        var first = p.Positions.Count;
        for (var ring = 0; ring <= rings; ring++)
        {
            var t = ring / (double)rings;
            var r = r0 + (r1 - r0) * t;
            var center = start + axis * (float)t;
            for (var k = 0; k < Radial; k++)
            {
                var a = 2 * Math.PI * k / Radial;
                var n = u * (float)Math.Cos(a) + v * (float)Math.Sin(a);
                p.Positions.Add(center + n * (float)r);
                p.Normals.Add(n);
                p.UVs.Add(new Vector2(k / (float)Radial, (float)t));
                p.Influences.Add(Influence(t));
            }
        }

        for (var ring = 0; ring < rings; ring++)
            for (var k = 0; k < Radial; k++)
            {
                var a = first + ring * Radial + k;
                var b = first + ring * Radial + (k + 1) % Radial;
                var c = a + Radial;
                var d = b + Radial;
                p.Indices.AddRange([a, b, d, a, d, c]); // counter-clockwise seen from outside
            }

        // Caps: a centre vertex per end.
        foreach (var (ringIndex, normal, t) in new[] { (0, -dir, 0.0), (rings, dir, 1.0) })
        {
            var center = p.Positions.Count;
            p.Positions.Add(start + axis * (float)t);
            p.Normals.Add(normal);
            p.UVs.Add(new Vector2(0.5f, (float)t));
            p.Influences.Add(Influence(t));
            for (var k = 0; k < Radial; k++)
            {
                var a = first + ringIndex * Radial + k;
                var b = first + ringIndex * Radial + (k + 1) % Radial;
                if (ringIndex == 0)
                    p.Indices.AddRange([center, b, a]);
                else
                    p.Indices.AddRange([center, a, b]);
            }
        }
    }

    private static (Primitive Suit, Primitive Skin) BuildMesh()
    {
        var suit = new Primitive();
        var skin = new Primitive();
        Vector3 At(string bone) => GlobalRest(B(bone));

        // Torso.
        Tube(suit, B("Hips"), At("Spine"), B("Spine"), 0.15f, 0.14f, 4);
        Tube(suit, B("Spine"), At("Chest"), B("Chest"), 0.14f, 0.15f, 4);
        Tube(suit, B("Chest"), At("UpperChest"), B("UpperChest"), 0.15f, 0.17f, 4);
        Tube(suit, B("UpperChest"), At("Neck"), B("Neck"), 0.17f, 0.12f, 4);
        Tube(skin, B("Neck"), At("Head"), B("Head"), 0.05f, 0.05f, 2);
        Tube(skin, B("Head"), At("HeadTop"), -1, 0.1f, 0.09f, 6);
        Tube(suit, B("Hips"), (At("UpperLeg.L") + At("UpperLeg.R")) / 2 + new Vector3(0, -0.06f, 0), -1, 0.15f, 0.13f, 2);

        foreach (var side in new[] { "L", "R" })
        {
            // Arms.
            Tube(suit, B($"Shoulder.{side}"), At($"UpperArm.{side}"), B($"UpperArm.{side}"), 0.07f, 0.06f, 3);
            Tube(suit, B($"UpperArm.{side}"), At($"LowerArm.{side}"), B($"LowerArm.{side}"), 0.06f, 0.05f, 8);
            Tube(suit, B($"LowerArm.{side}"), At($"Hand.{side}"), B($"Hand.{side}"), 0.05f, 0.04f, 8);
            Tube(skin, B($"Hand.{side}"), At($"Index1.{side}"), B($"Index1.{side}"), 0.04f, 0.035f, 4);
            Tube(skin, B($"Index1.{side}"), At($"Index2.{side}"), B($"Index2.{side}"), 0.015f, 0.013f, 2);
            Tube(skin, B($"Index2.{side}"), At($"Index2.{side}") + Bones[B($"Index2.{side}")].Offset * 0.8f, -1, 0.013f, 0.01f, 2);
            Tube(skin, B($"Thumb1.{side}"), At($"Thumb2.{side}"), B($"Thumb2.{side}"), 0.016f, 0.014f, 2);
            Tube(skin, B($"Thumb2.{side}"), At($"Thumb2.{side}") + Bones[B($"Thumb2.{side}")].Offset * 0.8f, -1, 0.014f, 0.01f, 2);

            // Legs.
            Tube(suit, B($"UpperLeg.{side}"), At($"LowerLeg.{side}"), B($"LowerLeg.{side}"), 0.085f, 0.065f, 9);
            Tube(suit, B($"LowerLeg.{side}"), At($"Foot.{side}"), B($"Foot.{side}"), 0.06f, 0.045f, 9);
            Tube(suit, B($"Foot.{side}"), At($"Toe.{side}"), B($"Toe.{side}"), 0.05f, 0.045f, 3);
            Tube(suit, B($"Toe.{side}"), At($"Toe.{side}") + new Vector3(0, 0, 0.07f), -1, 0.045f, 0.03f, 2);
        }

        return (suit, skin);
    }

    /// <summary>Triangles of the generated mesh (both primitives).</summary>
    public static int TriangleCount
    {
        get
        {
            var (suit, skin) = BuildMesh();
            return (suit.Indices.Count + skin.Indices.Count) / 3;
        }
    }

    // ── Animations ───────────────────────────────────────────────────────────

    private sealed record Clip(string Name, double Length, int Keys, Func<double, Dictionary<int, Quaternion>> Rotations,
        Func<double, Vector3>? HipsTranslation, string Extras);

    private static Quaternion Q(Vector3 axis, double degrees) => Quaternion.CreateFromAxisAngle(axis, (float)(degrees * Math.PI / 180));

    private static Quaternion Euler(double x, double y, double z) =>
        Quaternion.Normalize(Q(Vector3.UnitX, x) * Q(Vector3.UnitY, y) * Q(Vector3.UnitZ, z));

    // Arms hang from the T-pose: the left arm rotates −Z, the right +Z.
    private static void Arms(Dictionary<int, Quaternion> r, double down, double swingL, double swingR, double elbowL, double elbowR)
    {
        r[B("UpperArm.L")] = Euler(swingL, 0, -down);
        r[B("UpperArm.R")] = Euler(swingR, 0, down);
        r[B("LowerArm.L")] = Euler(0, -elbowL, 0);
        r[B("LowerArm.R")] = Euler(0, elbowR, 0);
    }

    private static void Legs(Dictionary<int, Quaternion> r, double hipL, double hipR, double kneeL, double kneeR)
    {
        r[B("UpperLeg.L")] = Euler(-hipL, 0, 0);
        r[B("UpperLeg.R")] = Euler(-hipR, 0, 0);
        r[B("LowerLeg.L")] = Euler(kneeL, 0, 0);
        r[B("LowerLeg.R")] = Euler(kneeR, 0, 0);
    }

    private static readonly Clip[] Animations =
    [
        new("idle", 2.0, 21, t =>
        {
            var r = new Dictionary<int, Quaternion>();
            var breath = Math.Sin(t / 2.0 * 2 * Math.PI);
            Arms(r, 72 + 2 * breath, 0, 0, 8, 8);
            r[B("Chest")] = Euler(2 * breath, 0, 0);
            r[B("Head")] = Euler(-1.5 * breath, 3 * breath, 0);
            Legs(r, 0, 0, 0, 0);
            return r;
        }, t => new Vector3(0, 1.0f - (float)(0.008 * (1 + Math.Sin(t / 2.0 * 2 * Math.PI))), 0), """{ "loop": true }"""),
        new("walk", 1.0, 21, t =>
        {
            var r = new Dictionary<int, Quaternion>();
            var s = Math.Sin(t * 2 * Math.PI);
            var c = Math.Cos(t * 2 * Math.PI);
            Arms(r, 75, -18 * s, 18 * s, 15, 15);
            Legs(r, 25 * s, -25 * s, 20 + 20 * Math.Max(0, c), 20 + 20 * Math.Max(0, -c));
            r[B("Spine")] = Euler(3, 5 * s, 0);
            return r;
        }, t => new Vector3(0, 0.98f + (float)(0.02 * Math.Abs(Math.Cos(t * 2 * Math.PI))), 0), """{ "loop": true }"""),
        new("run", 0.66, 23, t =>
        {
            var r = new Dictionary<int, Quaternion>();
            var phase = t / 0.66 * 2 * Math.PI;
            var s = Math.Sin(phase);
            var c = Math.Cos(phase);
            Arms(r, 70, -40 * s, 40 * s, 80, 80);
            Legs(r, 45 * s, -45 * s, 30 + 60 * Math.Max(0, c), 30 + 60 * Math.Max(0, -c));
            r[B("Spine")] = Euler(14, 8 * s, 0);
            r[B("Head")] = Euler(-10, 0, 0);
            return r;
        }, t => new Vector3(0, 0.95f + (float)(0.05 * Math.Abs(Math.Sin(t / 0.66 * 2 * Math.PI))), 0),
            """{ "loop": true, "events": [ { "name": "Step", "time": 0 }, { "name": "Step", "time": 0.33 } ] }"""),
        new("roll", 0.8, 25, t =>
        {
            var r = new Dictionary<int, Quaternion>();
            var tuck = Math.Sin(Math.Min(t / 0.8, 1) * Math.PI);
            r[B("Hips")] = Euler(360 * Math.Min(t / 0.8, 1), 0, 0);
            Arms(r, 70, -60 * tuck, -60 * tuck, 90 * tuck, 90 * tuck);
            Legs(r, 90 * tuck, 90 * tuck, 120 * tuck, 120 * tuck);
            r[B("Spine")] = Euler(30 * tuck, 0, 0);
            return r;
        }, t => new Vector3(0, 1.0f - (float)(0.5 * Math.Sin(Math.Min(t / 0.8, 1) * Math.PI)), (float)(0.8 * t)), "{}"),
        new("swing", 0.8, 25, t =>
        {
            var r = new Dictionary<int, Quaternion>();
            // Raise the right arm over the head (0 → 0.3 s), bring it down hard (→ 0.4 s: the hit), recover.
            var raise = t < 0.3 ? t / 0.3 : t < 0.4 ? 1 - (t - 0.3) / 0.1 * 1.4 : -0.4 + (t - 0.4) / 0.4 * 0.4;
            Arms(r, 75, 0, -150 * raise - 20, 10, 30 + 30 * Math.Max(raise, 0));
            r[B("Spine")] = Euler(-10 * raise + 10, -15 * raise, 0);
            Legs(r, 10, -10, 10, 10);
            return r;
        }, null, $$"""{ "events": { "HammerFist": {{SwingEventTime.ToString(System.Globalization.CultureInfo.InvariantCulture)}} } }"""),
        new("craft", 1.0, 21, t =>
        {
            var r = new Dictionary<int, Quaternion>();
            var tap = Math.Abs(Math.Sin(t * 2 * Math.PI));
            Arms(r, 60, -55 - 15 * tap, -55, 70, 70 + 20 * tap);
            r[B("Spine")] = Euler(20, 0, 0);
            r[B("Head")] = Euler(20, 0, 0);
            Legs(r, 20, 20, 40, 40);
            return r;
        }, t => new Vector3(0, 0.92f, 0), "{}"),
        new("hit", 0.5, 16, t =>
        {
            var r = new Dictionary<int, Quaternion>();
            var k = Math.Sin(Math.Min(t / 0.5, 1) * Math.PI);
            r[B("Spine")] = Euler(-20 * k, 0, 0);
            r[B("Head")] = Euler(-25 * k, 0, 0);
            Arms(r, 60 - 30 * k, 20 * k, 20 * k, 30, 30);
            Legs(r, -10 * k, 0, 10 * k, 0);
            return r;
        }, null, "{}"),
        new("down", 1.0, 21, t =>
        {
            var r = new Dictionary<int, Quaternion>();
            var k = Math.Min(t / 0.7, 1);
            r[B("Hips")] = Euler(-80 * k, 0, 0);
            Arms(r, 75 - 45 * k, -20 * k, -30 * k, 20, 10);
            Legs(r, 10 * k, -5 * k, 10 * k, 20 * k);
            return r;
        }, t => new Vector3(0, 1.0f - (float)(0.82 * Math.Min(t / 0.7, 1)), (float)(-0.3 * Math.Min(t / 0.7, 1))), "{}"),
    ];

    // ── glTF ─────────────────────────────────────────────────────────────────

    private static float Quantize(double value) => (float)(Math.Round(value * 4096.0) / 4096.0);

    private static byte[] BuildGlb()
    {
        var bin = new List<byte>();
        var views = new List<(int Offset, int Length, int? Target)>();
        var accessors = new List<Action<Utf8JsonWriter>>();

        int AddView(byte[] data, int? target)
        {
            while (bin.Count % 4 != 0)
                bin.Add(0);
            views.Add((bin.Count, data.Length, target));
            bin.AddRange(data);
            return views.Count - 1;
        }

        byte[] Floats(IEnumerable<float> values)
        {
            var list = new List<byte>();
            foreach (var v in values)
                list.AddRange(BitConverter.GetBytes(v));
            return [.. list];
        }

        int Accessor(byte[] data, int? target, int componentType, int count, string type, float[]? min = null, float[]? max = null)
        {
            var view = AddView(data, target);
            accessors.Add(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("bufferView", view);
                w.WriteNumber("componentType", componentType);
                w.WriteNumber("count", count);
                w.WriteString("type", type);
                if (min is not null)
                {
                    w.WriteStartArray("min");
                    foreach (var m in min)
                        w.WriteNumberValue(m);
                    w.WriteEndArray();
                    w.WriteStartArray("max");
                    foreach (var m in max!)
                        w.WriteNumberValue(m);
                    w.WriteEndArray();
                }

                w.WriteEndObject();
            });
            return accessors.Count - 1;
        }

        int Vec3(IReadOnlyList<Vector3> values, int? target, bool bounds)
        {
            var q = values.Select(v => new Vector3(Quantize(v.X), Quantize(v.Y), Quantize(v.Z))).ToArray();
            return Accessor(Floats(q.SelectMany(v => new[] { v.X, v.Y, v.Z })), target, 5126, q.Length, "VEC3",
                bounds ? [q.Min(v => v.X), q.Min(v => v.Y), q.Min(v => v.Z)] : null,
                bounds ? [q.Max(v => v.X), q.Max(v => v.Y), q.Max(v => v.Z)] : null);
        }

        // Primitives.
        var (suit, skin) = BuildMesh();
        var primitives = new List<(int Position, int Normal, int Uv, int Joints, int Weights, int Indices, int Material)>();
        foreach (var (primitive, material) in new[] { (suit, 0), (skin, 1) })
        {
            var position = Vec3(primitive.Positions, 34962, bounds: true);
            var normal = Vec3(primitive.Normals, 34962, bounds: false);
            var uv = Accessor(Floats(primitive.UVs.SelectMany(t => new[] { Quantize(t.X), Quantize(t.Y) })), 34962, 5126, primitive.UVs.Count, "VEC2");
            var joints = Accessor([.. primitive.Influences.SelectMany(i => new[] { (byte)i.J0, (byte)i.J1, (byte)0, (byte)0 })], 34962, 5121,
                primitive.Influences.Count, "VEC4");
            var weights = Accessor(Floats(primitive.Influences.SelectMany(i => new[] { Quantize(i.W0), Quantize(i.W1), 0f, 0f })), 34962, 5126,
                primitive.Influences.Count, "VEC4");
            var indices = Accessor([.. primitive.Indices.SelectMany(i => BitConverter.GetBytes((ushort)i))], 34963, 5123, primitive.Indices.Count, "SCALAR");
            primitives.Add((position, normal, uv, joints, weights, indices, material));
        }

        // Inverse bind matrices: the inverse of each bone's global rest (translations only), column-major.
        var ibm = Accessor(Floats(Enumerable.Range(0, Bones.Length).SelectMany(b =>
        {
            var p = GlobalRest(b);
            return new[] { 1f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, Quantize(-p.X), Quantize(-p.Y), Quantize(-p.Z), 1 };
        })), null, 5126, Bones.Length, "MAT4");

        // Animations: per clip one time accessor shared by its channels, a rotation output per animated bone and a hips
        // translation output.
        var clipChannels = new List<(Clip Clip, int Input, List<(int Bone, string Path, int Output)> Channels)>();
        foreach (var clip in Animations)
        {
            var times = Enumerable.Range(0, clip.Keys).Select(k => Quantize(clip.Length * k / (clip.Keys - 1))).ToArray();
            var input = Accessor(Floats(times), null, 5126, times.Length, "SCALAR", [times[0]], [times[^1]]);
            var samples = times.Select(t => clip.Rotations(t)).ToArray();
            var channels = new List<(int, string, int)>();
            foreach (var bone in samples[0].Keys.Order())
            {
                var quats = samples.Select(s => s[bone]).ToArray();
                var output = Accessor(Floats(quats.SelectMany(q => new[] { Quantize(q.X), Quantize(q.Y), Quantize(q.Z), Quantize(q.W) })), null, 5126,
                    quats.Length, "VEC4");
                channels.Add((bone, "rotation", output));
            }

            if (clip.HipsTranslation is { } hips)
            {
                var output = Vec3(times.Select(t => hips(t)).ToArray(), null, bounds: false);
                channels.Add((0, "translation", output));
            }

            clipChannels.Add((clip, input, channels));
        }

        while (bin.Count % 4 != 0)
            bin.Add(0);

        // JSON. Nodes: 0 Armature, 1..31 bones, 32 Body.
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteStartObject("asset");
            w.WriteString("version", "2.0");
            w.WriteString("generator", "MainframeEngine.Tests SkinnedCharacter (CC0)");
            w.WriteEndObject();
            w.WriteNumber("scene", 0);
            w.WriteStartArray("scenes");
            w.WriteStartObject();
            w.WriteStartArray("nodes");
            w.WriteNumberValue(0);
            w.WriteNumberValue(Bones.Length + 1);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteStartArray("nodes");
            w.WriteStartObject();
            w.WriteString("name", "Armature");
            w.WriteStartArray("children");
            w.WriteNumberValue(1);
            w.WriteEndArray();
            w.WriteEndObject();
            for (var b = 0; b < Bones.Length; b++)
            {
                w.WriteStartObject();
                w.WriteString("name", Bones[b].Name);
                w.WriteStartArray("translation");
                w.WriteNumberValue(Quantize(Bones[b].Offset.X));
                w.WriteNumberValue(Quantize(Bones[b].Offset.Y));
                w.WriteNumberValue(Quantize(Bones[b].Offset.Z));
                w.WriteEndArray();
                var children = Enumerable.Range(0, Bones.Length).Where(c => Bones[c].Parent == b).ToArray();
                if (children.Length > 0)
                {
                    w.WriteStartArray("children");
                    foreach (var c in children)
                        w.WriteNumberValue(c + 1);
                    w.WriteEndArray();
                }

                w.WriteEndObject();
            }

            w.WriteStartObject();
            w.WriteString("name", "Body");
            w.WriteNumber("mesh", 0);
            w.WriteNumber("skin", 0);
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteStartArray("skins");
            w.WriteStartObject();
            w.WriteString("name", "Armature");
            w.WriteNumber("inverseBindMatrices", ibm);
            w.WriteNumber("skeleton", 1);
            w.WriteStartArray("joints");
            for (var b = 0; b < Bones.Length; b++)
                w.WriteNumberValue(b + 1);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteStartArray("meshes");
            w.WriteStartObject();
            w.WriteString("name", "Body");
            w.WriteStartArray("primitives");
            foreach (var p in primitives)
            {
                w.WriteStartObject();
                w.WriteStartObject("attributes");
                w.WriteNumber("POSITION", p.Position);
                w.WriteNumber("NORMAL", p.Normal);
                w.WriteNumber("TEXCOORD_0", p.Uv);
                w.WriteNumber("JOINTS_0", p.Joints);
                w.WriteNumber("WEIGHTS_0", p.Weights);
                w.WriteEndObject();
                w.WriteNumber("indices", p.Indices);
                w.WriteNumber("material", p.Material);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteStartArray("materials");
            foreach (var (name, color) in new[] { ("Suit", new[] { 0.10f, 0.22f, 0.45f }), ("Skin", new[] { 0.80f, 0.52f, 0.36f }) })
            {
                w.WriteStartObject();
                w.WriteString("name", name);
                w.WriteStartObject("pbrMetallicRoughness");
                w.WriteStartArray("baseColorFactor");
                foreach (var c in color)
                    w.WriteNumberValue(c);
                w.WriteNumberValue(1);
                w.WriteEndArray();
                w.WriteNumber("metallicFactor", 0);
                w.WriteNumber("roughnessFactor", 0.8);
                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("animations");
            foreach (var (clip, input, channels) in clipChannels)
            {
                w.WriteStartObject();
                w.WriteString("name", clip.Name);
                w.WriteStartArray("samplers");
                foreach (var channel in channels)
                {
                    w.WriteStartObject();
                    w.WriteNumber("input", input);
                    w.WriteNumber("output", channel.Output);
                    w.WriteString("interpolation", "LINEAR");
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteStartArray("channels");
                for (var c = 0; c < channels.Count; c++)
                {
                    w.WriteStartObject();
                    w.WriteNumber("sampler", c);
                    w.WriteStartObject("target");
                    w.WriteNumber("node", channels[c].Bone + 1);
                    w.WriteString("path", channels[c].Path);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WritePropertyName("extras");
                w.WriteRawValue(clip.Extras);
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("accessors");
            foreach (var accessor in accessors)
                accessor(w);
            w.WriteEndArray();

            w.WriteStartArray("bufferViews");
            foreach (var (offset, length, target) in views)
            {
                w.WriteStartObject();
                w.WriteNumber("buffer", 0);
                w.WriteNumber("byteOffset", offset);
                w.WriteNumber("byteLength", length);
                if (target is { } t)
                    w.WriteNumber("target", t);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("buffers");
            w.WriteStartObject();
            w.WriteNumber("byteLength", bin.Count);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }

        var json = new List<byte>(stream.ToArray());
        while (json.Count % 4 != 0)
            json.Add((byte)' ');

        var glb = new List<byte>();
        void U32(int value) => glb.AddRange(BitConverter.GetBytes((uint)value));
        U32(0x46546C67);
        U32(2);
        U32(12 + 8 + json.Count + 8 + bin.Count);
        U32(json.Count);
        U32(0x4E4F534A);
        glb.AddRange(json);
        U32(bin.Count);
        U32(0x004E4942);
        glb.AddRange(bin);
        return [.. glb];
    }

    /// <summary>The JSON chunk of a generated or committed <c>.glb</c> (for diagnostics).</summary>
    public static string ReadJson(byte[] glb) => Encoding.UTF8.GetString(glb, 20, BitConverter.ToInt32(glb, 12));
}
