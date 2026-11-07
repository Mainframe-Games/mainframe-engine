using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace MainframeEngine;

/// <summary>A problem in a pose script: where (a JSON path such as <c>keys[3].joints.LeftKnee</c>) and what.</summary>
public readonly record struct PoseScriptError(string Path, string Message)
{
    public override string ToString() => Path.Length == 0 ? Message : $"{Path}: {Message}";
}

/// <summary>A pose script that could not be read; <see cref="Errors"/> lists every problem found.</summary>
public sealed class PoseScriptException(IReadOnlyList<PoseScriptError> errors)
    : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<PoseScriptError> Errors { get; } = errors;
}

/// <summary>
/// Reads pose scripts (G1c). Every error carries its JSON path; all errors are collected and nothing is returned when there
/// is one. Unknown properties are errors, so a typo does not silently do nothing.
/// </summary>
public static class PoseScriptParser
{
    /// <summary>Reads <paramref name="json"/>; throws <see cref="PoseScriptException"/> with every problem found.</summary>
    public static PoseScript Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException e)
        {
            throw new PoseScriptException([new PoseScriptError("", $"not valid JSON: {e.Message}")]);
        }

        using (document)
        {
            var errors = new List<PoseScriptError>();
            var script = Read(document.RootElement, errors);
            if (errors.Count > 0 || script is null)
                throw new PoseScriptException(errors.Count > 0 ? errors : [new PoseScriptError("", "empty script")]);
            return script;
        }
    }

    private static readonly string[] TopLevel = ["model", "library", "name", "length", "loop", "root", "keys", "arcs"];
    private static readonly string[] KeyFields = ["t", "pose", "hips", "joints", "mirror", "ik", "ease"];
    private static readonly string[] HipsFields = ["x", "y", "z", "yaw", "pitch", "roll"];
    private static readonly string[] AngleFields = ["flex", "abduct", "twist"];
    private static readonly string[] ArcFields = ["from", "to", "height"];

    private static PoseScript? Read(JsonElement root, List<PoseScriptError> errors)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("", "a pose script is a JSON object"));
            return null;
        }

        Unknown(root, "", TopLevel, errors);
        var model = RequiredString(root, "model", "", errors);
        var library = RequiredString(root, "library", "", errors);
        var name = RequiredString(root, "name", "", errors);
        var length = root.TryGetProperty("length", out var lengthElement) ? Number(lengthElement, "length", errors) : Missing("length", errors);
        if (length is <= 0f)
            errors.Add(new("length", "must be more than 0 seconds"));

        var loop = AnimationLoopMode.None;
        if (root.TryGetProperty("loop", out var loopElement))
        {
            loop = loopElement.ValueKind switch
            {
                JsonValueKind.True => AnimationLoopMode.Linear,
                JsonValueKind.False => AnimationLoopMode.None,
                JsonValueKind.String when Enum.TryParse<AnimationLoopMode>(loopElement.GetString(), true, out var mode) && Enum.IsDefined(mode) => mode,
                _ => Error(AnimationLoopMode.None, "loop", "must be \"None\", \"Linear\", \"PingPong\" or a boolean", errors),
            };
        }

        var rootMode = PoseRootMode.InPlace;
        if (root.TryGetProperty("root", out var rootElement))
        {
            rootMode = rootElement.GetString() switch
            {
                "inPlace" => PoseRootMode.InPlace,
                "motion" => PoseRootMode.Motion,
                _ => Error(PoseRootMode.InPlace, "root", "must be \"inPlace\" or \"motion\"", errors),
            };
        }

        var keys = new List<PoseKey>();
        if (!root.TryGetProperty("keys", out var keysElement) || keysElement.ValueKind != JsonValueKind.Array || keysElement.GetArrayLength() == 0)
        {
            errors.Add(new("keys", "a non-empty array of keys is required"));
        }
        else
        {
            var index = 0;
            foreach (var element in keysElement.EnumerateArray())
            {
                if (ReadKey(element, $"keys[{index}]", errors) is { } key)
                    keys.Add(key);
                index++;
            }
        }

        for (var i = 0; i < keys.Count; i++)
        {
            if (keys[i].Time < 0f || (length is { } l && keys[i].Time > l + 1e-4f))
                errors.Add(new($"keys[{i}].t", $"{Format(keys[i].Time)} is outside the clip (0 to {Format(length ?? 0f)})"));
            if (i > 0 && keys[i].Time <= keys[i - 1].Time)
                errors.Add(new($"keys[{i}].t", $"{Format(keys[i].Time)} is not after the previous key ({Format(keys[i - 1].Time)})"));
        }

        if (keys.Count > 0 && keys[0].Pose is null)
            errors.Add(new("keys[0].pose", $"the first key needs a named pose ({string.Join(", ", HumanoidPoses.Names)})"));

        var arcs = new List<PoseArc>();
        if (root.TryGetProperty("arcs", out var arcsElement))
        {
            if (arcsElement.ValueKind != JsonValueKind.Array)
            {
                errors.Add(new("arcs", "must be an array"));
            }
            else
            {
                var index = 0;
                foreach (var element in arcsElement.EnumerateArray())
                {
                    var path = $"arcs[{index++}]";
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        errors.Add(new(path, "an arc is an object { from, to, height }"));
                        continue;
                    }

                    Unknown(element, path, ArcFields, errors);
                    var from = element.TryGetProperty("from", out var f) ? Number(f, path + ".from", errors) : Missing(path + ".from", errors);
                    var to = element.TryGetProperty("to", out var t) ? Number(t, path + ".to", errors) : Missing(path + ".to", errors);
                    float? height = element.TryGetProperty("height", out var h) ? Number(h, path + ".height", errors) : null;
                    if (from is { } a && to is { } b)
                    {
                        if (b <= a)
                            errors.Add(new(path, "to must be after from"));
                        else if (a < 0f || (length is { } l && b > l + 1e-4f))
                            errors.Add(new(path, "the arc must lie inside the clip"));
                        else if (height is <= 0f)
                            errors.Add(new(path + ".height", "must be more than 0 metres"));
                        else
                            arcs.Add(new PoseArc(a, b, height));
                    }
                }
            }
        }

        if (errors.Count > 0 || model is null || library is null || name is null || length is null)
            return null;
        return new PoseScript
        {
            Model = model,
            Library = library,
            Name = name,
            Length = length.Value,
            Loop = loop,
            Root = rootMode,
            Keys = keys,
            Arcs = arcs,
        };
    }

    private static PoseKey? ReadKey(JsonElement element, string path, List<PoseScriptError> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new(path, "a key is an object { t, pose, hips, joints, mirror, ik, ease }"));
            return null;
        }

        Unknown(element, path, KeyFields, errors);
        var time = element.TryGetProperty("t", out var t) ? Number(t, path + ".t", errors) : Missing(path + ".t", errors);

        string? pose = null;
        if (element.TryGetProperty("pose", out var poseElement))
        {
            pose = poseElement.ValueKind == JsonValueKind.String ? poseElement.GetString() : null;
            if (pose is null || !HumanoidPoses.Names.Contains(pose))
                errors.Add(new(path + ".pose", $"unknown pose (named poses: {string.Join(", ", HumanoidPoses.Names)})"));
        }

        PartialHips? hips = null;
        if (element.TryGetProperty("hips", out var hipsElement))
        {
            if (hipsElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new(path + ".hips", "must be an object { x, y, z, yaw, pitch, roll }"));
            }
            else
            {
                Unknown(hipsElement, path + ".hips", HipsFields, errors);
                float? Field(string field) =>
                    hipsElement.TryGetProperty(field, out var v) ? Number(v, $"{path}.hips.{field}", errors) : null;
                hips = new PartialHips(Field("x"), Field("y"), Field("z"), Field("yaw"), Field("pitch"), Field("roll"));
            }
        }

        var joints = new Dictionary<HumanoidBone, PartialAngles>();
        if (element.TryGetProperty("joints", out var jointsElement))
        {
            if (jointsElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new(path + ".joints", "must be an object of bone names"));
            }
            else
            {
                foreach (var joint in jointsElement.EnumerateObject())
                {
                    var jointPath = $"{path}.joints.{joint.Name}";
                    if (!HumanoidBones.TryParse(joint.Name, out var bone) || bone == HumanoidBone.Hips)
                    {
                        var suggestion = HumanoidBones.Suggest(joint.Name);
                        errors.Add(new(jointPath, bone == HumanoidBone.Hips && HumanoidBones.TryParse(joint.Name, out _)
                            ? "the hips go in \"hips\", not \"joints\""
                            : suggestion is { } s ? $"unknown bone (did you mean {s}?)" : "unknown bone (mfe anim poses lists them)"));
                        continue;
                    }

                    if (joint.Value.ValueKind != JsonValueKind.Object)
                    {
                        errors.Add(new(jointPath, "must be an object { flex, abduct, twist }"));
                        continue;
                    }

                    Unknown(joint.Value, jointPath, AngleFields, errors);
                    float? Angle(string field) =>
                        joint.Value.TryGetProperty(field, out var v) ? Number(v, $"{jointPath}.{field}", errors) : null;
                    var angles = new PartialAngles(Angle("flex"), Angle("abduct"), Angle("twist"));
                    if (HumanoidBones.IsHinge(bone) && (angles.Abduct is not (null or 0f) || angles.Twist is not (null or 0f)))
                        errors.Add(new(jointPath, $"{bone} is a hinge: flex only"));
                    joints[bone] = angles;
                }
            }
        }

        var mirror = false;
        if (element.TryGetProperty("mirror", out var mirrorElement))
        {
            if (mirrorElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                mirror = mirrorElement.GetBoolean();
            else
                errors.Add(new(path + ".mirror", "must be true or false"));
        }

        var ik = new Dictionary<IkEffector, IkCommand>();
        if (element.TryGetProperty("ik", out var ikElement))
        {
            if (ikElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new(path + ".ik", "must be an object of effectors (leftFoot, rightFoot, leftHand, rightHand)"));
            }
            else
            {
                foreach (var entry in ikElement.EnumerateObject())
                {
                    var ikPath = $"{path}.ik.{entry.Name}";
                    if (!TryEffector(entry.Name, out var effector))
                    {
                        errors.Add(new(ikPath, "unknown effector (leftFoot, rightFoot, leftHand, rightHand)"));
                        continue;
                    }

                    if (ReadIk(entry.Value, ikPath, errors) is { } command)
                        ik[effector] = command;
                }
            }
        }

        float? ease = null;
        if (element.TryGetProperty("ease", out var easeElement))
        {
            ease = easeElement.ValueKind switch
            {
                JsonValueKind.Number => easeElement.GetSingle(),
                JsonValueKind.String => easeElement.GetString() switch
                {
                    "linear" => 1f,
                    "in" => 2f,
                    "out" => 0.5f,
                    "inOut" => -2f,
                    _ => Error<float?>(null, path + ".ease", "must be \"linear\", \"in\", \"out\", \"inOut\" or a number", errors),
                },
                _ => Error<float?>(null, path + ".ease", "must be \"linear\", \"in\", \"out\", \"inOut\" or a number", errors),
            };
            if (ease is 0f)
                errors.Add(new(path + ".ease", "0 holds the value forever; use a positive or negative ease"));
        }

        return time is null ? null : new PoseKey
        {
            Time = time.Value,
            Pose = pose,
            Hips = hips,
            Joints = joints,
            Mirror = mirror,
            Ik = ik,
            Ease = ease,
        };
    }

    private static IkCommand? ReadIk(JsonElement value, string path, List<PoseScriptError> errors)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String when value.GetString() == "pin":
                return new IkCommand(IkCommandKind.Pin);
            case JsonValueKind.String when value.GetString() == "release":
                return new IkCommand(IkCommandKind.Release);
            case JsonValueKind.Object:
                if (value.TryGetProperty("space", out _))
                {
                    errors.Add(new(path + ".space", "only character-space targets are supported"));
                    return null;
                }

                Unknown(value, path, ["pos"], errors);
                if (!value.TryGetProperty("pos", out var pos) || pos.ValueKind != JsonValueKind.Array || pos.GetArrayLength() != 3 ||
                    pos.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.Number))
                {
                    errors.Add(new(path + ".pos", "must be [x, y, z] in metres"));
                    return null;
                }

                var p = pos.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                return new IkCommand(IkCommandKind.Target, new Vector3(p[0], p[1], p[2]));
            default:
                errors.Add(new(path, "must be \"pin\", \"release\" or { \"pos\": [x, y, z] }"));
                return null;
        }
    }

    internal static bool TryEffector(string name, out IkEffector effector)
    {
        effector = name switch
        {
            "leftFoot" => IkEffector.LeftFoot,
            "rightFoot" => IkEffector.RightFoot,
            "leftHand" => IkEffector.LeftHand,
            "rightHand" => IkEffector.RightHand,
            _ => (IkEffector)255,
        };
        return (byte)effector != 255;
    }

    private static void Unknown(JsonElement element, string path, string[] known, List<PoseScriptError> errors)
    {
        foreach (var property in element.EnumerateObject())
            if (!known.Contains(property.Name))
                errors.Add(new(Join(path, property.Name), $"unknown property (known: {string.Join(", ", known)})"));
    }

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    private static string? RequiredString(JsonElement root, string name, string path, List<PoseScriptError> errors)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
            return text;
        errors.Add(new(Join(path, name), "a non-empty string is required"));
        return null;
    }

    private static float? Number(JsonElement value, string path, List<PoseScriptError> errors)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var number) && float.IsFinite(number))
            return number;
        errors.Add(new(path, "must be a number"));
        return null;
    }

    private static float? Missing(string path, List<PoseScriptError> errors)
    {
        errors.Add(new(path, "is required"));
        return null;
    }

    private static T Error<T>(T fallback, string path, string message, List<PoseScriptError> errors)
    {
        errors.Add(new(path, message));
        return fallback;
    }

    private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
