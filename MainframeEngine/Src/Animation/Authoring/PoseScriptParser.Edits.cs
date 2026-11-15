using System.Globalization;
using System.Text.Json;

namespace MainframeEngine;

public static partial class PoseScriptParser
{
    private static readonly string[] EditTopLevel = ["model", "library", "base", "name", "edits"];
    private static readonly string[] EditFields = ["t", "range", "insert", "hips", "joints", "mirror", "ik", "ease", "arc"];

    /// <summary>True when <paramref name="json"/> is an edit script (it has <c>base</c> or <c>edits</c>) rather than a pose script.</summary>
    public static bool IsEditScript(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   (document.RootElement.TryGetProperty("edits", out _) || document.RootElement.TryGetProperty("base", out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Reads an edit script; throws <see cref="PoseScriptException"/> with every problem found.</summary>
    public static PoseEditScript ParseEdit(string json)
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
            var script = ReadEditScript(document.RootElement, errors);
            if (errors.Count > 0 || script is null)
                throw new PoseScriptException(errors.Count > 0 ? errors : [new PoseScriptError("", "empty script")]);
            return script;
        }
    }

    private static PoseEditScript? ReadEditScript(JsonElement root, List<PoseScriptError> errors)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("", "an edit script is a JSON object"));
            return null;
        }

        Unknown(root, "", EditTopLevel, errors);
        var model = RequiredString(root, "model", "", errors);
        var library = RequiredString(root, "library", "", errors);
        var baseName = RequiredString(root, "base", "", errors);
        var name = root.TryGetProperty("name", out _) ? RequiredString(root, "name", "", errors) : baseName;

        var edits = new List<PoseEdit>();
        if (!root.TryGetProperty("edits", out var editsElement) || editsElement.ValueKind != JsonValueKind.Array || editsElement.GetArrayLength() == 0)
        {
            errors.Add(new("edits", "a non-empty array of edits is required"));
        }
        else
        {
            var index = 0;
            foreach (var element in editsElement.EnumerateArray())
            {
                if (ReadEdit(element, $"edits[{index}]", errors) is { } edit)
                    edits.Add(edit);
                index++;
            }
        }

        if (errors.Count > 0 || model is null || library is null || baseName is null || name is null)
            return null;
        return new PoseEditScript { Model = model, Library = library, Base = baseName, Name = name, Edits = edits };
    }

    private static PoseEdit? ReadEdit(JsonElement element, string path, List<PoseScriptError> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new(path, "an edit is an object { t or range, insert, hips, joints, mirror, ik, ease, arc }"));
            return null;
        }

        var before = errors.Count;
        Unknown(element, path, EditFields, errors);

        float? time = element.TryGetProperty("t", out var t) ? Number(t, path + ".t", errors) : null;
        (float, float)? range = null;
        if (element.TryGetProperty("range", out var rangeElement))
        {
            if (rangeElement.ValueKind != JsonValueKind.Array || rangeElement.GetArrayLength() != 2 ||
                rangeElement.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.Number))
                errors.Add(new(path + ".range", "must be [from, to] in seconds"));
            else if (rangeElement[0].GetSingle() is var from && rangeElement[1].GetSingle() is var to && to < from)
                errors.Add(new(path + ".range", "to must not be before from"));
            else
                range = (from, to);
        }

        if (time is null && range is null && !element.TryGetProperty("t", out _) && !element.TryGetProperty("range", out _))
            errors.Add(new(path, "needs \"t\" (one key) or \"range\" (every key in it)"));
        if (element.TryGetProperty("t", out _) && element.TryGetProperty("range", out _))
            errors.Add(new(path, "takes \"t\" or \"range\", not both"));

        var insert = false;
        if (element.TryGetProperty("insert", out var insertElement))
        {
            if (insertElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                insert = insertElement.GetBoolean();
            else
                errors.Add(new(path + ".insert", "must be true or false"));
            if (insert && element.TryGetProperty("range", out _))
                errors.Add(new(path + ".insert", "inserts one key: use \"t\", not \"range\""));
        }

        EditHips? hips = null;
        if (element.TryGetProperty("hips", out var hipsElement))
        {
            if (hipsElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new(path + ".hips", "must be an object { x, y, z, yaw, pitch, roll }"));
            }
            else
            {
                Unknown(hipsElement, path + ".hips", HipsFields, errors);
                EditValue? Field(string field) =>
                    hipsElement.TryGetProperty(field, out var v) ? ReadEditValue(v, $"{path}.hips.{field}", errors) : null;
                hips = new EditHips(Field("x"), Field("y"), Field("z"), Field("yaw"), Field("pitch"), Field("roll"));
            }
        }

        var joints = new Dictionary<HumanoidBone, EditAngles>();
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
                    EditValue? Angle(string field) =>
                        joint.Value.TryGetProperty(field, out var v) ? ReadEditValue(v, $"{jointPath}.{field}", errors) : null;
                    var angles = new EditAngles(Angle("flex"), Angle("abduct"), Angle("twist"));
                    if (HumanoidBones.IsHinge(bone) && (angles.Abduct is not null || angles.Twist is not null))
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

                    if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() == "release")
                    {
                        errors.Add(new(ikPath, "edits pin (hold where the clip has it) or reach { \"pos\": [x, y, z] }; there is nothing to release"));
                        continue;
                    }

                    if (ReadIk(entry.Value, ikPath, errors) is { } command)
                    {
                        if (command.Pin)
                            errors.Add(new(ikPath + ".pin", "an edit reaches a target at the edited keys; it does not pin from there (pin with \"pin\" alone)"));
                        else
                            ik[effector] = command;
                    }
                }
            }
        }

        var ease = element.TryGetProperty("ease", out var easeElement) ? ReadEase(easeElement, path + ".ease", errors) : null;

        EditValue? arcHeight = null;
        if (element.TryGetProperty("arc", out var arcElement))
        {
            if (arcElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new(path + ".arc", "must be an object { height }"));
            }
            else
            {
                Unknown(arcElement, path + ".arc", ["height"], errors);
                if (arcElement.TryGetProperty("height", out var heightElement))
                    arcHeight = ReadEditValue(heightElement, path + ".arc.height", errors);
                else
                    errors.Add(new(path + ".arc.height", "is required"));
                if (arcHeight is { Operation: EditOperation.Set, Amount: <= 0f })
                    errors.Add(new(path + ".arc.height", "must be more than 0 metres"));
                if (!element.TryGetProperty("range", out _))
                    errors.Add(new(path + ".arc", "an arc edit needs \"range\": [take-off, landing]"));
            }
        }

        if (errors.Count == before && hips is null && joints.Count == 0 && ik.Count == 0 && ease is null && arcHeight is null)
            errors.Add(new(path, "changes nothing (give hips, joints, ik, ease or arc)"));
        if (errors.Count > before)
            return null;
        return new PoseEdit
        {
            Path = path,
            Time = time,
            Range = range,
            Insert = insert,
            Hips = hips,
            Joints = joints,
            Mirror = mirror,
            Ik = ik,
            Ease = ease,
            ArcHeight = arcHeight,
        };
    }

    private static EditValue? ReadEditValue(JsonElement value, string path, List<PoseScriptError> errors)
    {
        const string Expected = "must be a number, \"+=n\", \"-=n\" or \"*n\"";
        if (value.ValueKind == JsonValueKind.Number)
            return Number(value, path, errors) is { } number ? new EditValue(EditOperation.Set, number) : null;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 1 } text)
            return Error<EditValue?>(null, path, Expected, errors);

        var (operation, sign, digits) = text switch
        {
            _ when text.StartsWith("+=", StringComparison.Ordinal) => (EditOperation.Add, 1f, text[2..]),
            _ when text.StartsWith("-=", StringComparison.Ordinal) => (EditOperation.Add, -1f, text[2..]),
            _ when text.StartsWith('*') => (EditOperation.Scale, 1f, text[1..]),
            _ => ((EditOperation)255, 0f, ""),
        };
        if ((byte)operation == 255 || !float.TryParse(digits.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) ||
            !float.IsFinite(amount))
            return Error<EditValue?>(null, path, Expected, errors);
        return new EditValue(operation, sign * amount);
    }
}
