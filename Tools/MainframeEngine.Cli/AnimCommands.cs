using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MainframeEngine.Cli;

/// <summary>
/// <c>mfe anim …</c> (G1c animation authoring, docs/design/animation.md#the-mfe-cli): rig a model, list the canonical bones
/// and named poses, bake pose and edit scripts into library clips, read clips back, check them, list a library.
/// </summary>
internal static class AnimCommands
{
    public const string Usage = """
        mfe anim — animation authoring on rigged humanoids (docs/design/animation.md#the-mfe-cli)

          rig <model> [--write-meta]
                Load the model and print its bone tree, the humanoid map (each bone's source and confidence), unmapped
                and missing bones, the rest pose (T/A) and proportions. --write-meta stores the map in the model's .mfmeta.
          poses
                The named poses, the canonical bones (side, hinge, required) and their range-of-motion limits.
          bake <script.json> [--replace] [--no-link] [--tolerance name=value,…]
                Bake a pose script, or apply an edit script (one with "base" and "edits"), into its library .mfres, then
                run the checks. A clip changed by hand since its bake is only replaced with --replace. The first bake
                links the library to the model's .mfmeta (--no-link skips it). Exit 1 when the checks find errors.
          describe <library> <clip> --model <model> [--times 0.2,0.4]
                The clip in canonical terms per key time (or the given times): hips, joint angles, feet and hands.
          check <library> <clip> --model <model> [--root inPlace|motion] [--start <pose>] [--end <pose>]
                [--tolerance name=value,…]
                Run the checks; exit 1 on errors (warnings do not fail).
          list <library>
                The library's clips: length, loop, tracks, keys, baked state.

        Paths to models and libraries are project paths (Assets/…); the script is a file path. Every command takes
        --project <dir> and --json.
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            output.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        var command = new CommandLine(args[1..], MfeCli.Flags);
        var json = command.Flag("--json");
        var projectOption = command.Option("--project");
        return args[0] switch
        {
            "rig" => WithProject(projectOption, project => Rig(project, command, json, output)),
            "poses" => Poses(command, json, output),
            "bake" => WithProject(projectOption, project => Bake(project, command, json, output)),
            "describe" => WithProject(projectOption, project => Describe(project, command, json, output)),
            "check" => WithProject(projectOption, project => Check(project, command, json, output)),
            "list" => WithProject(projectOption, project => List(project, command, json, output)),
            _ => throw new UsageException($"unknown anim command '{args[0]}' (rig, poses, bake, describe, check, list)"),
        };
    }

    private static int WithProject(string? directory, Func<MfeProject, int> run)
    {
        using var project = MfeProject.Open(directory);
        return run(project);
    }

    private static string Positional(CommandLine command, int index, string what) =>
        index < command.Positionals.Count ? command.Positionals[index] : throw new UsageException($"{what} is required");

    private static void NoMorePositionals(CommandLine command, int count)
    {
        if (command.Positionals.Count > count)
            throw new UsageException($"unexpected argument '{command.Positionals[count]}'");
    }

    private static string F(float value, string format = "0.###") => value.ToString(format, CultureInfo.InvariantCulture);

    // ── Models ──────────────────────────────────────────────────────────────

    private sealed record Model(string Path, Node Root, Skeleton3D Skeleton, NodePath SkeletonPath, HumanoidMap Map, HumanoidMapReport Report,
        IReadOnlyList<string> ImportedClips, ModelImportSettings Settings)
    {
        public HumanoidRig Rig()
        {
            if (!Report.IsComplete)
                throw new InputException($"{Path}: the humanoid map is missing {string.Join(", ", Report.MissingRequired)}; " +
                                         "run `mfe anim rig` and map them in the .mfmeta \"humanoid\" setting.");
            return new HumanoidRig(Skeleton, Map);
        }
    }

    private static Model LoadModel(MfeProject project, string modelPath)
    {
        var path = MfeProject.ProjectPath(modelPath);
        var absolute = MfeProject.Absolute(path);
        if (!File.Exists(absolute))
            throw new InputException($"{path} does not exist.");
        var meta = AssetDatabase.Current.ReadOrCreateMeta(path, create: false);
        var settings = ModelImportSettings.FromMeta(meta);
        // The model's own clips only: linked libraries are what the tools write.
        var root = ModelImporter.ImportNodes(absolute, settings with { Libraries = [] });
        var skeleton = FindSkeleton(root) ?? throw new InputException($"{path} has no skeleton.");
        var map = HumanoidMap.Detect(skeleton, settings.Humanoid, out var report);
        var imported = FindPlayer(root)?.Libraries.SelectMany(l => l.Animations).Select(a => a.ResourceName ?? "").Where(n => n.Length > 0).ToList() ?? [];
        return new Model(path, root, skeleton, root.GetPathTo(skeleton), map, report, imported, settings);
    }

    private static Skeleton3D? FindSkeleton(Node node)
    {
        if (node is Skeleton3D skeleton)
            return skeleton;
        foreach (var child in node.Children)
            if (FindSkeleton(child) is { } found)
                return found;
        return null;
    }

    private static AnimationPlayer? FindPlayer(Node node)
    {
        if (node is AnimationPlayer player)
            return player;
        foreach (var child in node.Children)
            if (FindPlayer(child) is { } found)
                return found;
        return null;
    }

    // ── rig ─────────────────────────────────────────────────────────────────

    private static int Rig(MfeProject project, CommandLine command, bool json, TextWriter output)
    {
        var modelPath = Positional(command, 0, "<model>");
        NoMorePositionals(command, 1);
        var writeMeta = command.Flag("--write-meta");
        command.EnsureAllUsed();

        var model = LoadModel(project, modelPath);
        HumanoidRig? rig = model.Report.IsComplete ? new HumanoidRig(model.Skeleton, model.Map) : null;
        var wrote = writeMeta && model.Report.IsComplete && model.Map.Store(model.Path);
        if (writeMeta && !model.Report.IsComplete)
            throw new InputException($"{model.Path}: the map is incomplete (missing {string.Join(", ", model.Report.MissingRequired)}); nothing was written.");

        if (json)
        {
            WriteJson(output, w =>
            {
                w.WriteString("model", model.Path);
                w.WriteStartArray("bones");
                for (var i = 0; i < model.Skeleton.GetBoneCount(); i++)
                {
                    w.WriteStartObject();
                    w.WriteString("name", model.Skeleton.GetBoneName(i));
                    var parent = model.Skeleton.GetBoneParent(i);
                    if (parent >= 0)
                        w.WriteString("parent", model.Skeleton.GetBoneName(parent));
                    else
                        w.WriteNull("parent");
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteStartArray("map");
                foreach (var entry in model.Report.Entries)
                {
                    w.WriteStartObject();
                    w.WriteString("bone", entry.Bone.ToString());
                    w.WriteString("rigBone", entry.RigBone);
                    w.WriteString("source", entry.Source.ToString().ToLowerInvariant());
                    w.WriteNumber("confidence", entry.Confidence);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                WriteStrings(w, "unmappedRigBones", model.Report.UnmappedRigBones);
                WriteStrings(w, "missingRequired", model.Report.MissingRequired.Select(b => b.ToString()));
                WriteStrings(w, "notes", model.Report.Notes);
                if (rig is not null)
                {
                    w.WriteString("restPose", rig.RestKind.ToString());
                    w.WriteNumber("restArmAngle", rig.RestArmAngle);
                    w.WriteStartObject("proportions");
                    w.WriteNumber("height", rig.Proportions.Height);
                    w.WriteNumber("hipHeight", rig.Proportions.HipHeight);
                    w.WriteNumber("legLength", rig.Proportions.LegLength);
                    w.WriteNumber("armLength", rig.Proportions.ArmLength);
                    w.WriteNumber("shoulderWidth", rig.Proportions.ShoulderWidth);
                    w.WriteEndObject();
                }

                w.WriteBoolean("wroteMeta", wrote);
            });
            return 0;
        }

        output.WriteLine($"{model.Path}: {model.Skeleton.GetBoneCount()} bones, skeleton {model.SkeletonPath}");
        if (rig is not null)
        {
            var p = rig.Proportions;
            output.WriteLine($"Rest pose: {rig.RestKind} (arms {F(rig.RestArmAngle, "0.#")}° from horizontal)");
            output.WriteLine($"Proportions: height {F(p.Height, "0.00")} m, hips {F(p.HipHeight, "0.00")} m, legs {F(p.LegLength, "0.00")} m, " +
                             $"arms {F(p.ArmLength, "0.00")} m, shoulders {F(p.ShoulderWidth, "0.00")} m");
        }

        output.WriteLine("Bones:");
        for (var i = 0; i < model.Skeleton.GetBoneCount(); i++)
        {
            var depth = 0;
            for (var at = model.Skeleton.GetBoneParent(i); at >= 0; at = model.Skeleton.GetBoneParent(at))
                depth++;
            output.WriteLine($"  {new string(' ', depth * 2)}{model.Skeleton.GetBoneName(i)}");
        }

        output.WriteLine($"Humanoid map ({model.Report.Entries.Count} of {HumanoidBones.Count} canonical bones):");
        foreach (var entry in model.Report.Entries)
            output.WriteLine($"  {entry.Bone,-24} {entry.RigBone,-20} {entry.Source.ToString().ToLowerInvariant()} {F(entry.Confidence, "0.00")}");
        output.WriteLine(model.Report.MissingRequired.Count == 0
            ? "Missing required bones: none"
            : $"Missing required bones: {string.Join(", ", model.Report.MissingRequired)} (map them in the .mfmeta \"humanoid\" setting)");
        if (model.Report.UnmappedRigBones.Count > 0)
            output.WriteLine($"Unmapped rig bones: {string.Join(", ", model.Report.UnmappedRigBones)}");
        foreach (var note in model.Report.Notes)
            output.WriteLine($"Note: {note}");
        if (writeMeta)
            output.WriteLine(wrote ? $"Wrote the map to {model.Path}.mfmeta (\"humanoid\")." : $"{model.Path}.mfmeta already holds this map.");
        return 0;
    }

    // ── poses ───────────────────────────────────────────────────────────────

    private static int Poses(CommandLine command, bool json, TextWriter output)
    {
        NoMorePositionals(command, 0);
        command.Option("--project"); // accepted, not needed
        command.EnsureAllUsed();
        var components = new[] { JointComponent.Flex, JointComponent.Abduct, JointComponent.Twist };
        if (json)
        {
            WriteJson(output, w =>
            {
                WriteStrings(w, "poses", HumanoidPoses.Names);
                w.WriteStartArray("bones");
                foreach (var bone in HumanoidBones.All)
                {
                    w.WriteStartObject();
                    w.WriteString("name", bone.ToString());
                    if (HumanoidBones.GetParent(bone) is { } parent)
                        w.WriteString("parent", parent.ToString());
                    else
                        w.WriteNull("parent");
                    w.WriteString("side", HumanoidBones.GetSide(bone).ToString().ToLowerInvariant());
                    w.WriteBoolean("required", HumanoidBones.IsRequired(bone));
                    w.WriteBoolean("hinge", HumanoidBones.IsHinge(bone));
                    if (bone != HumanoidBone.Hips)
                    {
                        w.WriteStartObject("limits");
                        foreach (var component in components)
                        {
                            var range = HumanoidLimits.Get(bone, component);
                            w.WriteStartArray(component.ToString().ToLowerInvariant());
                            w.WriteNumberValue(range.Min);
                            w.WriteNumberValue(range.Max);
                            w.WriteEndArray();
                        }

                        w.WriteEndObject();
                    }

                    w.WriteEndObject();
                }

                w.WriteEndArray();
            });
            return 0;
        }

        output.WriteLine($"Named poses: {string.Join(", ", HumanoidPoses.Names)} (tpose: all zeros; stand: arms down, shoulder abduct -80, elbow flex 10; rest: the rig's own rest)");
        output.WriteLine("Angles (degrees) from the canonical T-pose: flex (sagittal: hips/shoulders forward, knees/elbows bend, toes up),");
        output.WriteLine("abduct (away from the midline; arms up), twist (about the bone; internal). Mirrored: the same numbers mean the");
        output.WriteLine("same motion on both sides. Hips: x/y/z metres from rest, yaw (turn left)/pitch (lean forward)/roll (lean left).");
        output.WriteLine("Bones (* required, hinge = flex only), limits flex / abduct / twist:");
        foreach (var bone in HumanoidBones.All)
        {
            if (bone == HumanoidBone.Hips)
            {
                output.WriteLine($"  Hips*{new string(' ', 21)}pitch and roll {F(HumanoidLimits.HipsTilt.Min)}…{F(HumanoidLimits.HipsTilt.Max)}, yaw free");
                continue;
            }

            var name = bone + (HumanoidBones.IsRequired(bone) ? "*" : "") + (HumanoidBones.IsHinge(bone) ? " (hinge)" : "");
            var limits = string.Join(" / ", components.Select(c => HumanoidLimits.Get(bone, c)).Select(r => $"{F(r.Min)}…{F(r.Max)}"));
            output.WriteLine($"  {name,-26}{limits}");
        }

        return 0;
    }

    // ── bake ────────────────────────────────────────────────────────────────

    private static int Bake(MfeProject project, CommandLine command, bool json, TextWriter output)
    {
        var scriptPath = Positional(command, 0, "<script.json>");
        NoMorePositionals(command, 1);
        var replace = command.Flag("--replace");
        var noLink = command.Flag("--no-link");
        var tolerances = Tolerances(command);
        command.EnsureAllUsed();

        var text = File.ReadAllText(Path.GetFullPath(scriptPath));
        Animation clip;
        PoseLibraryWriteResult written;
        IReadOnlyList<string> notes;
        AnimationCheckOptions options;
        string library;
        string modelPath;
        int refinements;
        HumanoidRig rig;
        Model model;
        var edit = PoseScriptParser.IsEditScript(text);
        if (edit)
        {
            if (noLink)
                throw new UsageException("--no-link is for pose scripts; an edit never links");
            var script = PoseScriptParser.ParseEdit(text);
            model = LoadModel(project, script.Model);
            rig = model.Rig();
            library = MfeProject.ProjectPath(script.Library);
            var result = PoseEditor.Apply(script, PoseLibrary.Read(library, script.Base), rig, model.SkeletonPath);
            written = PoseLibrary.WriteEdit(result.Animation, library, script.Base, replace);
            (clip, notes, refinements) = (result.Animation, result.Notes, result.RefinementKeys);
            options = new AnimationCheckOptions { Tolerances = tolerances, Gravity = project.Gravity };
        }
        else
        {
            var script = PoseScriptParser.Parse(text);
            model = LoadModel(project, script.Model);
            rig = model.Rig();
            library = MfeProject.ProjectPath(script.Library);
            var result = PoseBaker.Bake(script, rig, model.SkeletonPath, project.Gravity);
            written = PoseLibrary.Write(result.Animation, library, model.Path, replace, link: !noLink, importedClips: model.ImportedClips);
            (clip, notes, refinements) = (result.Animation, result.Notes, result.RefinementKeys);
            options = AnimationCheckOptions.ForScript(script, project.Gravity, tolerances);
        }

        modelPath = model.Path;
        var report = AnimationChecks.Run(clip, rig, model.SkeletonPath, options);
        var allNotes = notes.Concat(written.Notes).ToList();
        if (json)
        {
            WriteJson(output, w =>
            {
                w.WriteString("clip", clip.ResourceName);
                w.WriteString("library", library);
                w.WriteString("model", modelPath);
                w.WriteBoolean("edit", edit);
                w.WriteBoolean("createdLibrary", written.CreatedLibrary);
                w.WriteBoolean("replacedClip", written.ReplacedClip);
                w.WriteBoolean("linked", written.Linked);
                w.WriteNumber("refinementKeys", refinements);
                WriteTracks(w, clip);
                WriteStrings(w, "notes", allNotes);
                w.WritePropertyName("checks");
                WriteReport(w, report);
            });
            return report.HasErrors ? 1 : 0;
        }

        var what = edit ? "Edited" : "Baked";
        var where = written.CreatedLibrary ? "a new library" : "";
        output.WriteLine($"{what} '{clip.ResourceName}' ({F(clip.Length)} s) into {library}{(where.Length > 0 ? $" ({where})" : "")}" +
                         $"{(written.ReplacedClip ? ", replacing the previous clip" : "")}{(written.Linked ? $"; linked to {modelPath}" : "")}.");
        output.WriteLine($"Tracks ({clip.Tracks.Count}, {clip.Tracks.Sum(t => t.Times.Length)} keys{(refinements > 0 ? $", {refinements} refinement" : "")}):");
        foreach (var track in clip.Tracks)
            output.WriteLine($"  {TrackName(track),-28} {track.Times.Length} keys");
        foreach (var note in allNotes)
            output.WriteLine($"Note: {note}");
        output.Write(report.ToText());
        output.WriteLine($"Play it: AnimationPlayer.Play(\"{clip.ResourceName}\") on {modelPath}'s player (its .mfmeta links {library}).");
        return report.HasErrors ? 1 : 0;
    }

    private static string TrackName(AnimationTrack track) => track switch
    {
        Rotation3DTrack r => $"{r.Bone} rotation",
        Position3DTrack p => $"{p.Bone} position",
        Scale3DTrack s => $"{s.Bone} scale",
        _ => $"{track.Path} {track.GetType().Name}",
    };

    private static AnimationCheckTolerances Tolerances(CommandLine command)
    {
        var tolerances = new AnimationCheckTolerances();
        foreach (var spec in command.Many("--tolerance"))
        {
            try
            {
                tolerances = tolerances.With(spec);
            }
            catch (ArgumentException e)
            {
                throw new UsageException($"--tolerance {e.Message.Split(" (Parameter", StringSplitOptions.None)[0]}");
            }
        }

        return tolerances;
    }

    // ── describe / check / list ─────────────────────────────────────────────

    private static (Model Model, HumanoidRig Rig, Animation Clip, string Library) LoadClip(MfeProject project, CommandLine command)
    {
        var library = MfeProject.ProjectPath(Positional(command, 0, "<library>"));
        var name = Positional(command, 1, "<clip>");
        NoMorePositionals(command, 2);
        var model = LoadModel(project, command.Option("--model") ?? throw new UsageException("--model <model> is required"));
        return (model, model.Rig(), PoseLibrary.Read(library, name), library);
    }

    private static int Describe(MfeProject project, CommandLine command, bool json, TextWriter output)
    {
        var timesOption = command.Many("--times");
        var (model, rig, clip, _) = LoadClip(project, command);
        command.EnsureAllUsed();
        List<float>? times = null;
        if (timesOption.Count > 0)
        {
            times = [];
            foreach (var value in timesOption)
                times.Add(float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t >= 0f
                    ? t
                    : throw new UsageException($"--times: '{value}' is not a time in seconds"));
        }

        var description = ClipDescriber.Describe(clip, rig, model.SkeletonPath, times);
        if (json)
        {
            WriteJson(output, w => WriteDescription(w, description));
            return 0;
        }

        output.Write(description.ToText());
        return 0;
    }

    private static int Check(MfeProject project, CommandLine command, bool json, TextWriter output)
    {
        PoseRootMode? root = command.Option("--root") switch
        {
            null => null,
            "inPlace" => PoseRootMode.InPlace,
            "motion" => PoseRootMode.Motion,
            var other => throw new UsageException($"--root must be inPlace or motion, not '{other}'"),
        };
        var start = PoseName(command.Option("--start"), "--start");
        var end = PoseName(command.Option("--end"), "--end");
        var tolerances = Tolerances(command);
        var (model, rig, clip, _) = LoadClip(project, command);
        command.EnsureAllUsed();

        var report = AnimationChecks.Run(clip, rig, model.SkeletonPath, new AnimationCheckOptions
        {
            Root = root,
            StartPose = start,
            EndPose = end,
            Tolerances = tolerances,
            Gravity = project.Gravity,
        });
        if (json)
            WriteJson(output, w => WriteReportFields(w, report));
        else
            output.Write(report.ToText());
        return report.HasErrors ? 1 : 0;
    }

    private static string? PoseName(string? name, string option) =>
        name is null || HumanoidPoses.Names.Contains(name)
            ? name
            : throw new UsageException($"{option}: unknown pose '{name}' ({string.Join(", ", HumanoidPoses.Names)})");

    private static int List(MfeProject project, CommandLine command, bool json, TextWriter output)
    {
        var library = MfeProject.ProjectPath(Positional(command, 0, "<library>"));
        NoMorePositionals(command, 1);
        command.EnsureAllUsed();
        if (!ResourceLoader.Exists(library))
            throw new InputException($"{library} does not exist.");
        var clips = ResourceLoader.Load<AnimationLibrary>(library).Animations;

        static string State(Animation clip) =>
            PoseLibrary.IsUntouchedBake(clip) ? "baked" : clip.BakedHash.Length > 0 ? "baked, changed since" : "not baked";

        if (json)
        {
            WriteJson(output, w =>
            {
                w.WriteString("library", library);
                w.WriteStartArray("clips");
                foreach (var clip in clips)
                {
                    w.WriteStartObject();
                    w.WriteString("name", clip.ResourceName);
                    w.WriteNumber("length", clip.Length);
                    w.WriteString("loop", clip.LoopMode.ToString());
                    w.WriteNumber("tracks", clip.Tracks.Count);
                    w.WriteNumber("keys", clip.Tracks.Sum(t => t.Times.Length));
                    w.WriteString("state", State(clip));
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            });
            return 0;
        }

        output.WriteLine($"{library}: {clips.Count} clip(s)");
        foreach (var clip in clips)
            output.WriteLine($"  {clip.ResourceName,-20} {F(clip.Length),6} s  loop {clip.LoopMode,-8} {clip.Tracks.Count,3} tracks " +
                             $"{clip.Tracks.Sum(t => t.Times.Length),5} keys  {State(clip)}");
        return 0;
    }

    // ── JSON ────────────────────────────────────────────────────────────────

    private static void WriteJson(TextWriter output, Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        output.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void WriteStrings(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartArray(name);
        foreach (var value in values)
            w.WriteStringValue(value);
        w.WriteEndArray();
    }

    private static void WriteTracks(Utf8JsonWriter w, Animation clip)
    {
        w.WriteStartArray("tracks");
        foreach (var track in clip.Tracks)
        {
            w.WriteStartObject();
            w.WriteString("track", TrackName(track));
            w.WriteNumber("keys", track.Times.Length);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteReport(Utf8JsonWriter w, AnimationCheckReport report)
    {
        w.WriteStartObject();
        WriteReportFields(w, report);
        w.WriteEndObject();
    }

    private static void WriteReportFields(Utf8JsonWriter w, AnimationCheckReport report)
    {
        w.WriteNumber("errors", report.Errors);
        w.WriteNumber("warnings", report.Warnings);
        w.WriteStartArray("findings");
        foreach (var f in report.Findings)
        {
            w.WriteStartObject();
            w.WriteString("check", f.Check);
            w.WriteString("severity", f.Severity == CheckSeverity.Error ? "error" : "warning");
            w.WriteNumber("from", f.From);
            w.WriteNumber("to", f.To);
            w.WriteString("subject", f.Subject);
            w.WriteNumber("value", f.Value);
            w.WriteString("message", f.Message);
            w.WriteString("fix", f.Fix);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        WriteStrings(w, "skipped", report.Skipped);
    }

    private static void WriteDescription(Utf8JsonWriter w, ClipDescription description)
    {
        w.WriteString("clip", description.Name);
        w.WriteNumber("length", description.Length);
        w.WriteString("loop", description.Loop.ToString());
        w.WriteBoolean("baked", description.Baked);
        w.WriteBoolean("untouchedBake", description.UntouchedBake);
        WriteStrings(w, "unmappedTracks", description.UnmappedTracks);
        w.WriteStartArray("times");
        foreach (var time in description.Times)
        {
            w.WriteStartObject();
            w.WriteNumber("t", time.Time);
            w.WriteBoolean("key", time.IsKey);
            WriteStrings(w, "partialKeys", time.PartialKeys.Select(b => b.ToString()));
            if (time.Ease is { } ease)
                w.WriteNumber("ease", ease);
            else
                w.WriteNull("ease");
            var hips = time.Pose.Hips;
            w.WriteStartObject("hips");
            w.WriteNumber("x", hips.Offset.X);
            w.WriteNumber("y", hips.Offset.Y);
            w.WriteNumber("z", hips.Offset.Z);
            w.WriteNumber("yaw", hips.Yaw);
            w.WriteNumber("pitch", hips.Pitch);
            w.WriteNumber("roll", hips.Roll);
            w.WriteEndObject();
            w.WriteStartObject("joints");
            foreach (var bone in description.MappedBones)
            {
                if (bone == HumanoidBone.Hips)
                    continue;
                var a = time.Pose[bone];
                w.WriteStartObject(bone.ToString());
                w.WriteNumber("flex", a.Flex);
                w.WriteNumber("abduct", a.Abduct);
                w.WriteNumber("twist", a.Twist);
                w.WriteEndObject();
            }

            w.WriteEndObject();
            w.WriteStartObject("effectors");
            foreach (var (effector, state) in time.Effectors)
            {
                w.WriteStartObject(ClipDescriber.EffectorName(effector));
                w.WriteStartArray("position");
                w.WriteNumberValue(state.Position.X);
                w.WriteNumberValue(state.Position.Y);
                w.WriteNumberValue(state.Position.Z);
                w.WriteEndArray();
                w.WriteNumber("height", state.Height);
                w.WriteBoolean("contact", state.Contact);
                w.WriteEndObject();
            }

            w.WriteEndObject();
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }
}
