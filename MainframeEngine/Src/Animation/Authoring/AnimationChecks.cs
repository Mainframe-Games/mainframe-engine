using System.Globalization;
using System.Numerics;
using System.Text;

namespace MainframeEngine;

/// <summary>How bad an <see cref="AnimationCheckFinding"/> is: errors fail a check run, warnings do not.</summary>
public enum CheckSeverity : byte
{
    Warning,
    Error,
}

/// <summary>
/// One problem <see cref="AnimationChecks"/> found: the check (<c>ground</c>, <c>footSlide</c>, …), the time range it holds
/// over, what it is about (a bone, a foot, the hips), the worst value measured and a suggested fix.
/// </summary>
public sealed record AnimationCheckFinding(string Check, CheckSeverity Severity, float From, float To, string Subject, float Value,
    string Message, string Fix)
{
    public override string ToString() =>
        $"{(Severity == CheckSeverity.Error ? "error" : "warning")} {Check} {AnimationCheckReport.Range(From, To)}: {Message} Fix: {Fix}";
}

/// <summary>What <see cref="AnimationChecks.Run"/> found.</summary>
public sealed class AnimationCheckReport
{
    public required IReadOnlyList<AnimationCheckFinding> Findings { get; init; }

    /// <summary>Checks that did not run and why (no root mode given, no named start pose, …).</summary>
    public required IReadOnlyList<string> Skipped { get; init; }

    public int Errors => Findings.Count(f => f.Severity == CheckSeverity.Error);

    public int Warnings => Findings.Count(f => f.Severity == CheckSeverity.Warning);

    /// <summary>True when a finding is an error (<c>mfe anim check</c> exits 1).</summary>
    public bool HasErrors => Errors > 0;

    /// <summary>The report as text: a summary line, then one line per finding (errors first), then the skipped checks.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Checks: {Errors} error(s), {Warnings} warning(s)\n");
        foreach (var finding in Findings.OrderByDescending(f => f.Severity).ThenBy(f => f.From))
            text.Append("  ").Append(finding).Append('\n');
        foreach (var skipped in Skipped)
            text.Append("  skipped: ").Append(skipped).Append('\n');
        return text.ToString();
    }

    internal static string Range(float from, float to) =>
        MathF.Abs(to - from) < 1e-4f
            ? $"t = {from.ToString("0.###", CultureInfo.InvariantCulture)} s"
            : $"{from.ToString("0.###", CultureInfo.InvariantCulture)}–{to.ToString("0.###", CultureInfo.InvariantCulture)} s";
}

/// <summary>
/// The thresholds of <see cref="AnimationChecks"/> (metres, degrees, seconds). <see cref="With"/> sets one by its name, as
/// <c>mfe anim check --tolerance footSlide=0.03</c> does.
/// </summary>
public sealed record AnimationCheckTolerances
{
    /// <summary>How far below the ground a foot, toe, hand or the hips may go (m) before it is an error.</summary>
    public float Ground { get; init; } = 0.01f;

    /// <summary>How far a foot in contact may slide (m) before a warning.</summary>
    public float FootSlide { get; init; } = 0.02f;

    /// <summary>A foot this close to the ground (m), not moving up or down, is in contact.</summary>
    public float ContactHeight { get; init; } = 0.02f;

    /// <summary>Degrees past a <see cref="HumanoidLimits"/> range before it is an error.</summary>
    public float Range { get; init; } = 5f;

    /// <summary>Angular speed of a bone (°/s) above which it is a spike.</summary>
    public float AngularSpeed { get; init; } = 1500f;

    /// <summary>Pose and per-frame motion difference (°) across a loop's wrap.</summary>
    public float LoopAngle { get; init; } = 2f;

    /// <summary>Hips difference (m) across a loop's wrap.</summary>
    public float LoopDistance { get; init; } = 0.01f;

    /// <summary>Degrees the first or last pose may differ from its declared named pose.</summary>
    public float StartEnd { get; init; } = 2f;

    /// <summary>Hips horizontal drift (m) an in-place clip may have.</summary>
    public float RootDrift { get; init; } = 0.01f;

    /// <summary>Root yaw speed (°/s) a root-motion clip may have.</summary>
    public float RootYawRate { get; init; } = 180f;

    /// <summary>How far (a fraction) a flight's apex may be off what gravity gives.</summary>
    public float Physics { get; init; } = 0.25f;

    /// <summary>The names <see cref="With"/> takes.</summary>
    public static IReadOnlyList<string> Names { get; } =
        ["ground", "footSlide", "contactHeight", "range", "angularSpeed", "loopAngle", "loopDistance", "startEnd", "rootDrift", "rootYawRate", "physics"];

    /// <summary>A copy with the tolerance <paramref name="name"/> set; throws <see cref="ArgumentException"/> for an unknown name or a negative value.</summary>
    public AnimationCheckTolerances With(string name, float value)
    {
        if (!(value >= 0f) || !float.IsFinite(value))
            throw new ArgumentException($"{name}: a tolerance is a number of at least 0.", nameof(value));
        return name switch
        {
            "ground" => this with { Ground = value },
            "footSlide" => this with { FootSlide = value },
            "contactHeight" => this with { ContactHeight = value },
            "range" => this with { Range = value },
            "angularSpeed" => this with { AngularSpeed = value },
            "loopAngle" => this with { LoopAngle = value },
            "loopDistance" => this with { LoopDistance = value },
            "startEnd" => this with { StartEnd = value },
            "rootDrift" => this with { RootDrift = value },
            "rootYawRate" => this with { RootYawRate = value },
            "physics" => this with { Physics = value },
            _ => throw new ArgumentException($"unknown tolerance '{name}' (known: {string.Join(", ", Names)})", nameof(name)),
        };
    }

    /// <summary>A copy with <c>name=value</c> pairs (comma-separated) applied.</summary>
    public AnimationCheckTolerances With(string assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        var result = this;
        foreach (var assignment in assignments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = assignment.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || !float.TryParse(assignment[(equals + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new ArgumentException($"'{assignment}' is not name=value", nameof(assignments));
            result = result.With(assignment[..equals].Trim(), value);
        }

        return result;
    }
}

/// <summary>What a check run knows about the clip beyond its keys (scripts are not kept, so the baker passes these on).</summary>
public sealed record AnimationCheckOptions
{
    public AnimationCheckTolerances Tolerances { get; init; } = new();

    /// <summary>The clip's root mode; null skips the root and physics checks.</summary>
    public PoseRootMode? Root { get; init; }

    /// <summary>The named pose the clip should start in (<see cref="HumanoidPoses"/>); null skips that check.</summary>
    public string? StartPose { get; init; }

    /// <summary>The named pose the clip should end in; null skips that check.</summary>
    public string? EndPose { get; init; }

    /// <summary>Gravity for the physics check (m/s²).</summary>
    public float Gravity { get; init; } = 9.8f;

    /// <summary>The options a pose script declares: its root mode and its first and last keys' named poses.</summary>
    public static AnimationCheckOptions ForScript(PoseScript script, float gravity = 9.8f, AnimationCheckTolerances? tolerances = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        return new AnimationCheckOptions
        {
            Root = script.Root,
            StartPose = script.Keys.Count > 0 ? script.Keys[0].Pose : null,
            EndPose = script.Keys.Count > 1 ? script.Keys[^1].Pose : null,
            Gravity = gravity,
            Tolerances = tolerances ?? new AnimationCheckTolerances(),
        };
    }
}

/// <summary>
/// Measures a clip on its <see cref="HumanoidRig"/> (G1c, ADR 0281) at 60 Hz and at every key, and reports what looks wrong:
/// feet, hands or hips under the ground, feet sliding while in contact, joints past their range of motion, quaternion flips
/// and spikes, loops that do not close, a start or end away from its declared pose, root drift, and jumps that disagree with
/// gravity. Claude cannot see the editor; these numbers are how it corrects itself. The rig's pose is restored afterwards.
/// </summary>
public static class AnimationChecks
{
    /// <summary>The sample rate (Hz); every key time is sampled too.</summary>
    public const int SampleRate = 60;

    /// <summary>A foot moving up or down slower than this (m/s) can be in contact.</summary>
    public const float ContactSpeed = 0.1f;

    /// <summary>A root-motion hips moving faster than this (m/s) between samples has jumped.</summary>
    public const float RootJumpSpeed = 30f;

    private static readonly HumanoidBone[] GroundBones =
    [
        HumanoidBone.LeftFoot, HumanoidBone.RightFoot, HumanoidBone.LeftToes, HumanoidBone.RightToes, HumanoidBone.LeftHand,
        HumanoidBone.RightHand, HumanoidBone.Hips,
    ];

    private sealed class Sample
    {
        public required float Time { get; init; }

        public required HumanoidPose Pose { get; init; }

        public required Quaternion[] Locals { get; init; }

        public required Vector3[] Points { get; init; } // by HumanoidBone, character space; NaN when unmapped
    }

    /// <summary>Runs every check on <paramref name="clip"/> (its tracks on <paramref name="skeletonPath"/>).</summary>
    public static AnimationCheckReport Run(Animation clip, HumanoidRig rig, NodePath skeletonPath, AnimationCheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(rig);
        options ??= new AnimationCheckOptions();
        var saved = ClipDescriber.SavePose(rig.Skeleton);
        try
        {
            var run = new Checking(clip, rig, skeletonPath, options);
            return run.Run();
        }
        finally
        {
            ClipDescriber.RestorePose(rig.Skeleton, saved);
        }
    }

    private static string Cm(float metres) => (metres * 100f).ToString("0.#", CultureInfo.InvariantCulture) + " cm";

    private static string Deg(float degrees) => degrees.ToString("0.#", CultureInfo.InvariantCulture) + "°";

    private static string S(float t) => t.ToString("0.###", CultureInfo.InvariantCulture) + " s";

    private static float AngleDelta(float a, float b)
    {
        var d = (a - b) % 360f;
        return d > 180f ? d - 360f : d < -180f ? d + 360f : d;
    }

    private sealed class Checking(Animation clip, HumanoidRig rig, NodePath skeletonPath, AnimationCheckOptions options)
    {
        private readonly Skeleton3D _skeleton = rig.Skeleton;
        private readonly AnimationCheckTolerances _tol = options.Tolerances;
        private readonly HumanoidBone[] _mapped = [.. HumanoidBones.All.Where(b => rig.GetRigBone(b) >= 0)];
        private readonly List<AnimationCheckFinding> _findings = [];
        private readonly List<string> _skipped = [];
        private List<Sample> _samples = [];
        private float _ground;

        public AnimationCheckReport Run()
        {
            _ground = ClipDescriber.Ground(rig);
            _samples = TakeSamples();
            Ground();
            FootSlide();
            RangeOfMotion();
            Flips();
            Spikes();
            Loop();
            StartAndEnd();
            Root();
            Physics();
            return new AnimationCheckReport { Findings = _findings, Skipped = _skipped };
        }

        // ── Sampling ────────────────────────────────────────────────────────

        private List<Sample> TakeSamples()
        {
            var times = new List<float>();
            var count = (int)MathF.Ceiling(clip.Length * SampleRate);
            foreach (var time in Enumerable.Range(0, count + 1).Select(i => MathF.Min(i / (float)SampleRate, clip.Length))
                         .Concat(ClipDescriber.KeyTimes(clip, skeletonPath).Where(t => t >= 0f && t <= clip.Length)).Order())
                if (times.Count == 0 || time - times[^1] > TrackKeys.TimeTolerance)
                    times.Add(time);

            var samples = new List<Sample>(times.Count);
            foreach (var time in times)
            {
                _skeleton.ResetBonePoses();
                ClipSampler.Apply(clip, _skeleton, skeletonPath, time);
                var points = new Vector3[HumanoidBones.Count];
                Array.Fill(points, new Vector3(float.NaN));
                foreach (var bone in GroundBones)
                    if (rig.GetRigBone(bone) is var index and >= 0)
                        points[(int)bone] = rig.ToCharacterSpace(_skeleton.GetBoneGlobalPose(index).Origin);
                samples.Add(new Sample
                {
                    Time = time,
                    Pose = rig.ReadPose(),
                    Locals = [.. _mapped.Select(b => _skeleton.GetBonePoseRotation(rig.GetRigBone(b)))],
                    Points = points,
                });
            }

            return samples;
        }

        private static float Low(Sample sample, HumanoidBone foot, HumanoidBone toes)
        {
            var y = sample.Points[(int)foot].Y;
            var toe = sample.Points[(int)toes].Y;
            return float.IsNaN(toe) ? y : MathF.Min(y, toe);
        }

        // ── Runs of failing samples → findings ──────────────────────────────

        private sealed class Runs
        {
            private readonly Dictionary<string, (int Last, float From, float To, float Worst, float WorstTime)> _open = [];
            private readonly List<(string Subject, float From, float To, float Worst, float WorstTime)> _closed = [];

            public void Add(string subject, int index, float time, float value)
            {
                if (_open.TryGetValue(subject, out var run) && run.Last == index - 1)
                {
                    _open[subject] = (index, run.From, time, MathF.Max(run.Worst, value), value > run.Worst ? time : run.WorstTime);
                    return;
                }

                if (_open.TryGetValue(subject, out run))
                    _closed.Add((subject, run.From, run.To, run.Worst, run.WorstTime));
                _open[subject] = (index, time, time, value, time);
            }

            public IEnumerable<(string Subject, float From, float To, float Worst, float WorstTime)> All() =>
                _closed.Concat(_open.Select(o => (o.Key, o.Value.From, o.Value.To, o.Value.Worst, o.Value.WorstTime)));
        }

        // ── Ground ──────────────────────────────────────────────────────────

        private void Ground()
        {
            var runs = new Runs();
            for (var i = 0; i < _samples.Count; i++)
            {
                foreach (var bone in GroundBones)
                {
                    var y = _samples[i].Points[(int)bone].Y;
                    if (!float.IsNaN(y) && y < _ground - _tol.Ground)
                        runs.Add(bone.ToString(), i, _samples[i].Time, _ground - y);
                }
            }

            foreach (var (subject, from, to, worst, at) in runs.All())
                _findings.Add(new AnimationCheckFinding("ground", CheckSeverity.Error, from, to, subject, worst,
                    $"{subject} goes {Cm(worst)} below the ground (worst at {S(at)}).",
                    subject.EndsWith("Foot", StringComparison.Ordinal) || subject.EndsWith("Toes", StringComparison.Ordinal)
                        ? "pin the foot (ik \"pin\") or raise the hips there; flex the ankle less if the toes dig in."
                        : "raise the hips or bring the limb up at these times."));
        }

        // ── Foot slide ──────────────────────────────────────────────────────

        private void FootSlide()
        {
            foreach (var (foot, toes, name) in new[]
                     {
                         (HumanoidBone.LeftFoot, HumanoidBone.LeftToes, "leftFoot"), (HumanoidBone.RightFoot, HumanoidBone.RightToes, "rightFoot"),
                     })
            {
                if (rig.GetRigBone(foot) < 0)
                    continue;
                var start = -1;
                for (var i = 0; i <= _samples.Count; i++)
                {
                    var contact = i < _samples.Count && InContact(i, foot, toes);
                    if (contact && start < 0)
                        start = i;
                    if (contact || start < 0)
                        continue;

                    var anchor = Horizontal(_samples[start].Points[(int)foot]);
                    var worst = 0f;
                    var worstTime = _samples[start].Time;
                    for (var j = start; j < i; j++)
                    {
                        var slide = Vector2.Distance(anchor, Horizontal(_samples[j].Points[(int)foot]));
                        if (slide > worst)
                            (worst, worstTime) = (slide, _samples[j].Time);
                    }

                    if (worst > _tol.FootSlide)
                        _findings.Add(new AnimationCheckFinding("footSlide", CheckSeverity.Warning, _samples[start].Time, _samples[i - 1].Time, name,
                            worst, $"{name} slides {Cm(worst)} while on the ground (worst at {S(worstTime)}).",
                            "pin it over this contact (ik \"pin\"), or lift it clear of the ground if it should step."));
                    start = -1;
                }
            }
        }

        private bool InContact(int i, HumanoidBone foot, HumanoidBone toes)
        {
            var height = Low(_samples[i], foot, toes) - _ground;
            if (height > _tol.ContactHeight)
                return false;
            var a = Math.Max(0, i - 1);
            var b = Math.Min(_samples.Count - 1, i + 1);
            var dt = _samples[b].Time - _samples[a].Time;
            if (dt <= 0f)
                return true;
            var speed = (Low(_samples[b], foot, toes) - Low(_samples[a], foot, toes)) / dt;
            return MathF.Abs(speed) < ContactSpeed;
        }

        private static Vector2 Horizontal(Vector3 v) => new(v.X, v.Z);

        // ── Range of motion ─────────────────────────────────────────────────

        private void RangeOfMotion()
        {
            var runs = new Runs();
            var limits = new Dictionary<string, AngleRange>();
            for (var i = 0; i < _samples.Count; i++)
            {
                var pose = _samples[i].Pose;
                foreach (var bone in _mapped)
                {
                    if (bone == HumanoidBone.Hips)
                        continue;
                    foreach (var component in new[] { JointComponent.Flex, JointComponent.Abduct, JointComponent.Twist })
                    {
                        var range = HumanoidLimits.Get(bone, component);
                        var angle = pose[bone][component];
                        if (range.Contains(angle, _tol.Range))
                            continue;
                        var subject = $"{bone}.{component.ToString().ToLowerInvariant()}";
                        limits[subject] = range;
                        runs.Add(subject, i, _samples[i].Time, MathF.Abs(angle - range.Clamp(angle)));
                    }
                }

                foreach (var (component, angle) in new[] { ("pitch", pose.Hips.Pitch), ("roll", pose.Hips.Roll) })
                {
                    if (HumanoidLimits.HipsTilt.Contains(angle, _tol.Range))
                        continue;
                    limits["Hips." + component] = HumanoidLimits.HipsTilt;
                    runs.Add("Hips." + component, i, _samples[i].Time, MathF.Abs(angle - HumanoidLimits.HipsTilt.Clamp(angle)));
                }
            }

            foreach (var (subject, from, to, worst, at) in runs.All())
            {
                var range = limits[subject];
                var hinge = range is { Min: 0f, Max: 0f };
                _findings.Add(new AnimationCheckFinding("range", CheckSeverity.Error, from, to, subject, worst,
                    hinge
                        ? $"{subject} moves {Deg(worst)} on a hinge (worst at {S(at)}); a hinge only flexes."
                        : $"{subject} goes {Deg(worst)} past its range {Deg(range.Min)}…{Deg(range.Max)} (worst at {S(at)}).",
                    hinge ? "keep the hinge to flex; turn the bone above it instead." : $"keep it within {Deg(range.Min)}…{Deg(range.Max)}."));
            }
        }

        // ── Flips and spikes ────────────────────────────────────────────────

        private void Flips()
        {
            foreach (var track in clip.Tracks.OfType<Rotation3DTrack>().Where(t => t.Enabled && t.Path == skeletonPath))
            {
                var subject = _skeleton.FindBone(track.Bone) is var index and >= 0 && rig.GetCanonicalBone(index) is { } canonical
                    ? canonical.ToString()
                    : track.Bone;
                for (var i = 1; i < track.Values.Length && i < track.Times.Length; i++)
                {
                    var dot = Quaternion.Dot(track.Values[i - 1], track.Values[i]);
                    if (dot < 0f)
                        _findings.Add(new AnimationCheckFinding("flip", CheckSeverity.Error, track.Times[i - 1], track.Times[i], subject, dot,
                            $"{subject}'s keys at {S(track.Times[i - 1])} and {S(track.Times[i])} are in opposite quaternion hemispheres; " +
                            "the bone can spin the long way round.",
                            "negate the later key (and the ones after it), or re-bake."));
                }
            }
        }

        private void Spikes()
        {
            var runs = new Runs();
            for (var i = 1; i < _samples.Count; i++)
            {
                var dt = _samples[i].Time - _samples[i - 1].Time;
                if (dt < 2f * TrackKeys.TimeTolerance)
                    continue;
                for (var b = 0; b < _mapped.Length; b++)
                {
                    var dot = MathF.Min(1f, MathF.Abs(Quaternion.Dot(_samples[i - 1].Locals[b], _samples[i].Locals[b])));
                    var speed = 2f * MathF.Acos(dot) * (180f / MathF.PI) / dt;
                    if (speed > _tol.AngularSpeed)
                        runs.Add(_mapped[b].ToString(), i, _samples[i].Time, speed);
                }
            }

            foreach (var (subject, from, to, worst, at) in runs.All())
                _findings.Add(new AnimationCheckFinding("spike", CheckSeverity.Error, from, to, subject, worst,
                    $"{subject} turns at {worst.ToString("0", CultureInfo.InvariantCulture)}°/s (worst at {S(at)}; over {_tol.AngularSpeed.ToString("0", CultureInfo.InvariantCulture)}°/s).",
                    "spread the motion over more time, or add a key between."));
        }

        // ── Loop ────────────────────────────────────────────────────────────

        private void Loop()
        {
            if (clip.LoopMode == AnimationLoopMode.None)
                return;
            var step = 1f / SampleRate;
            if (clip.Length <= 2f * step)
            {
                _skipped.Add("loop: the clip is too short to measure its wrap.");
                return;
            }

            var first = Pose(0f);
            var second = Pose(step);
            var beforeLast = Pose(clip.Length - step);
            var last = Pose(clip.Length);
            if (clip.LoopMode == AnimationLoopMode.Linear)
            {
                LoopDifference("pose", first, last, _tol.LoopAngle, _tol.LoopDistance,
                    "the last pose is not the first: the loop pops at the wrap.", "make the last key the same pose as the first.");
                LoopDifference("motion", Delta(first, second), Delta(beforeLast, last), _tol.LoopAngle, _tol.LoopDistance,
                    "the motion changes speed across the wrap: the loop hitches.",
                    "match the keys next to the ends (or their eases) so the motion runs on through the wrap.");
            }
            else
            {
                LoopDifference("start", new HumanoidPose(), Delta(first, second), _tol.LoopAngle, _tol.LoopDistance,
                    "the clip is still moving at its start, where ping-pong turns back.", "ease into the first key.");
                LoopDifference("end", new HumanoidPose(), Delta(beforeLast, last), _tol.LoopAngle, _tol.LoopDistance,
                    "the clip is still moving at its end, where ping-pong turns back.", "ease into the last key.");
            }
        }

        private void LoopDifference(string what, HumanoidPose a, HumanoidPose b, float angle, float distance, string message, string fix)
        {
            var worst = 0f;
            var subject = "";
            foreach (var bone in _mapped)
            {
                if (bone == HumanoidBone.Hips)
                    continue;
                foreach (var component in new[] { JointComponent.Flex, JointComponent.Abduct, JointComponent.Twist })
                {
                    var d = MathF.Abs(AngleDelta(a[bone][component], b[bone][component]));
                    if (d > worst)
                        (worst, subject) = (d, $"{bone}.{component.ToString().ToLowerInvariant()}");
                }
            }

            var hips = Vector3.Distance(a.Hips.Offset, b.Hips.Offset);
            if (worst > angle)
                _findings.Add(new AnimationCheckFinding("loop", CheckSeverity.Error, clip.Length, clip.Length, subject, worst,
                    $"{what}: {message} ({subject} by {Deg(worst)})", fix));
            if (hips > distance)
                _findings.Add(new AnimationCheckFinding("loop", CheckSeverity.Error, clip.Length, clip.Length, "Hips", hips,
                    $"{what}: {message} (the hips by {Cm(hips)})", fix));
        }

        private HumanoidPose Pose(float time)
        {
            _skeleton.ResetBonePoses();
            ClipSampler.Apply(clip, _skeleton, skeletonPath, time);
            return rig.ReadPose();
        }

        private HumanoidPose Delta(HumanoidPose from, HumanoidPose to)
        {
            var delta = new HumanoidPose
            {
                Hips = new HipsPose(to.Hips.Offset - from.Hips.Offset, AngleDelta(to.Hips.Yaw, from.Hips.Yaw),
                    AngleDelta(to.Hips.Pitch, from.Hips.Pitch), AngleDelta(to.Hips.Roll, from.Hips.Roll)),
            };
            foreach (var bone in _mapped)
                delta[bone] = new JointAngles(AngleDelta(to[bone].Flex, from[bone].Flex), AngleDelta(to[bone].Abduct, from[bone].Abduct),
                    AngleDelta(to[bone].Twist, from[bone].Twist));
            return delta;
        }

        // ── Start and end ───────────────────────────────────────────────────

        private void StartAndEnd()
        {
            Declared("start", options.StartPose, _samples[0]);
            Declared("end", options.EndPose, _samples[^1]);
        }

        private void Declared(string which, string? name, Sample sample)
        {
            if (name is null)
            {
                _skipped.Add($"{which}: no named {which} pose given.");
                return;
            }

            if (HumanoidPoses.Get(name, rig) is not { } expected)
            {
                _skipped.Add($"{which}: unknown pose '{name}'.");
                return;
            }

            var differences = new List<(string Subject, float Degrees)>();
            foreach (var bone in _mapped)
            {
                if (bone == HumanoidBone.Hips)
                    continue;
                foreach (var component in new[] { JointComponent.Flex, JointComponent.Abduct, JointComponent.Twist })
                {
                    var d = MathF.Abs(AngleDelta(sample.Pose[bone][component], expected[bone][component]));
                    if (d > _tol.StartEnd)
                        differences.Add(($"{bone}.{component.ToString().ToLowerInvariant()}", d));
                }
            }

            if (differences.Count == 0)
                return;
            var top = differences.OrderByDescending(d => d.Degrees).ToList();
            _findings.Add(new AnimationCheckFinding(which, CheckSeverity.Warning, sample.Time, sample.Time, top[0].Subject, top[0].Degrees,
                $"the {which} pose is {Deg(top[0].Degrees)} away from '{name}' " +
                $"({string.Join(", ", top.Take(3).Select(d => $"{d.Subject} {Deg(d.Degrees)}"))}{(top.Count > 3 ? $", {top.Count - 3} more" : "")}).",
                $"key '{name}' at the {which}, or ignore this if the difference is intended (IK pins move the legs)."));
        }

        // ── Root ────────────────────────────────────────────────────────────

        private void Root()
        {
            if (options.Root is not { } root)
            {
                _skipped.Add("root: no root mode given.");
                return;
            }

            if (rig.GetRigBone(HumanoidBone.Hips) < 0)
                return;
            var hips = (int)HumanoidBone.Hips;
            if (root == PoseRootMode.InPlace)
            {
                var start = Horizontal(_samples[0].Points[hips]);
                var runs = new Runs();
                for (var i = 0; i < _samples.Count; i++)
                {
                    var drift = Vector2.Distance(start, Horizontal(_samples[i].Points[hips]));
                    if (drift > _tol.RootDrift)
                        runs.Add("Hips", i, _samples[i].Time, drift);
                }

                foreach (var (subject, from, to, worst, at) in runs.All())
                    _findings.Add(new AnimationCheckFinding("root", CheckSeverity.Warning, from, to, subject, worst,
                        $"the hips drift {Cm(worst)} sideways or forward in an in-place clip (worst at {S(at)}).",
                        "keep the hips' x and z near 0, or bake with root \"motion\" if the clip should travel."));
                return;
            }

            var jumps = new Runs();
            var yaw = new Runs();
            for (var i = 1; i < _samples.Count; i++)
            {
                var dt = _samples[i].Time - _samples[i - 1].Time;
                if (dt < 2f * TrackKeys.TimeTolerance)
                    continue;
                var speed = Vector3.Distance(_samples[i].Points[hips], _samples[i - 1].Points[hips]) / dt;
                if (speed > RootJumpSpeed)
                    jumps.Add("Hips", i, _samples[i].Time, speed);
                var yawRate = MathF.Abs(AngleDelta(_samples[i].Pose.Hips.Yaw, _samples[i - 1].Pose.Hips.Yaw)) / dt;
                if (yawRate > _tol.RootYawRate)
                    yaw.Add("Hips", i, _samples[i].Time, yawRate);
            }

            foreach (var (subject, from, to, worst, at) in jumps.All())
                _findings.Add(new AnimationCheckFinding("root", CheckSeverity.Error, from, to, subject, worst,
                    $"the root jumps (the hips move at {worst.ToString("0", CultureInfo.InvariantCulture)} m/s at {S(at)}).",
                    "remove the jump in the hips position keys; root motion has to be continuous."));
            foreach (var (subject, from, to, worst, at) in yaw.All())
                _findings.Add(new AnimationCheckFinding("root", CheckSeverity.Warning, from, to, subject, worst,
                    $"the root turns at {worst.ToString("0", CultureInfo.InvariantCulture)}°/s (worst at {S(at)}; over {_tol.RootYawRate.ToString("0", CultureInfo.InvariantCulture)}°/s).",
                    "turn over more time; fast yaw in root motion spins the character body."));
        }

        // ── Physics ─────────────────────────────────────────────────────────

        private void Physics()
        {
            if (options.Root != PoseRootMode.Motion)
            {
                _skipped.Add("physics: only root \"motion\" clips carry their jump.");
                return;
            }

            if (rig.GetRigBone(HumanoidBone.LeftFoot) < 0 || rig.GetRigBone(HumanoidBone.RightFoot) < 0)
                return;
            var hips = (int)HumanoidBone.Hips;
            var start = -1;
            for (var i = 0; i <= _samples.Count; i++)
            {
                var flying = i < _samples.Count &&
                             Low(_samples[i], HumanoidBone.LeftFoot, HumanoidBone.LeftToes) - _ground > _tol.ContactHeight &&
                             Low(_samples[i], HumanoidBone.RightFoot, HumanoidBone.RightToes) - _ground > _tol.ContactHeight;
                if (flying && start < 0)
                    start = Math.Max(0, i - 1);
                if (flying || start < 0)
                    continue;

                var end = Math.Min(i, _samples.Count - 1);
                var flight = _samples[end].Time - _samples[start].Time;
                var takeOff = _samples[start].Points[hips].Y;
                var apex = 0f;
                for (var j = start; j <= end; j++)
                    apex = MathF.Max(apex, _samples[j].Points[hips].Y - takeOff);
                var expected = options.Gravity * flight * flight / 8f;
                if (flight >= 0.1f && expected > 1e-3f && MathF.Abs(apex - expected) / expected > _tol.Physics)
                    _findings.Add(new AnimationCheckFinding("physics", CheckSeverity.Warning, _samples[start].Time, _samples[end].Time, "Hips", apex,
                        $"a {S(flight)} flight rises {Cm(apex)}; gravity ({options.Gravity.ToString("0.##", CultureInfo.InvariantCulture)} m/s²) gives {Cm(expected)}.",
                        "fine for game feel; for a physical jump change the arc's height or the flight time."));
                start = -1;
            }
        }
    }
}
