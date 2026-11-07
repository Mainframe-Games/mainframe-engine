using System.Globalization;
using System.Numerics;

namespace MainframeEngine;

/// <summary>What <see cref="PoseBaker.Bake"/> made, and what it had to say about it.</summary>
public sealed class PoseBakeResult
{
    /// <summary>The clip (named after the script, <see cref="Animation.BakedHash"/> set).</summary>
    public required Animation Animation { get; init; }

    /// <summary>Things the author should know: ignored arcs, unreachable targets, unmapped bones, refinement keys.</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>Keys added between the script's keys so pins and arcs hold.</summary>
    public int RefinementKeys { get; init; }

    /// <summary>The rig's hips bone: the <see cref="AnimationPlayer.RootMotionBone"/> for a <see cref="PoseRootMode.Motion"/> clip.</summary>
    public required string HipsBone { get; init; }
}

/// <summary>
/// Bakes a <see cref="PoseScript"/> on a <see cref="HumanoidRig"/> into an ordinary <see cref="Animation"/> (G1c, ADR 0281):
/// cubic keys at the script's key times on every bone that moves, then refinement keys only where interpolation lets a pinned
/// effector slide, or an arc miss, by more than the tolerance.
/// </summary>
public static class PoseBaker
{
    /// <summary>How far a pin may slide or an arc miss before a refinement key is added (metres).</summary>
    public const float DefaultTolerance = 0.01f;

    /// <summary>The refinement pass's sample rate (Hz).</summary>
    public const int SampleRate = 60;

    private readonly record struct Pin(Vector3 Position, Quaternion EndRotation);

    private sealed record Frame(float Time, Quaternion[] Rotations, Vector3 HipsPosition, float Ease);

    /// <summary>
    /// Bakes <paramref name="script"/>. <paramref name="skeletonPath"/> is the skeleton's path from the model's root (the
    /// tracks' <see cref="AnimationTrack.Path"/>); <paramref name="gravity"/> (m/s²) shapes arcs without a height. The rig's
    /// skeleton pose is restored afterwards.
    /// </summary>
    public static PoseBakeResult Bake(PoseScript script, HumanoidRig rig, NodePath skeletonPath, float gravity = 9.8f,
        float tolerance = DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(rig);
        var baker = new Baking(script, rig, skeletonPath, gravity, tolerance);
        var saved = baker.SavePose();
        try
        {
            return baker.Run();
        }
        finally
        {
            baker.RestorePose(saved);
        }
    }

    private static (HumanoidBone Upper, HumanoidBone Lower, HumanoidBone End, Vector3 Pole) Chain(IkEffector effector) => effector switch
    {
        IkEffector.LeftFoot => (HumanoidBone.LeftUpperLeg, HumanoidBone.LeftLowerLeg, HumanoidBone.LeftFoot, Vector3.UnitZ),
        IkEffector.RightFoot => (HumanoidBone.RightUpperLeg, HumanoidBone.RightLowerLeg, HumanoidBone.RightFoot, Vector3.UnitZ),
        IkEffector.LeftHand => (HumanoidBone.LeftUpperArm, HumanoidBone.LeftLowerArm, HumanoidBone.LeftHand, -Vector3.UnitZ),
        _ => (HumanoidBone.RightUpperArm, HumanoidBone.RightLowerArm, HumanoidBone.RightHand, -Vector3.UnitZ),
    };

    private static string Seconds(float t) => t.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed class Baking(PoseScript script, HumanoidRig rig, NodePath skeletonPath, float gravity, float tolerance)
    {
        private readonly Skeleton3D _skeleton = rig.Skeleton;
        private readonly List<string> _notes = [];
        private readonly HumanoidBone[] _bones = [.. HumanoidBones.All.Where(b => rig.GetRigBone(b) >= 0)];
        private readonly int _hips = rig.GetRigBone(HumanoidBone.Hips);
        private readonly List<Dictionary<IkEffector, Pin>> _pinsAfter = [];
        private readonly List<(float From, float To, float K)> _arcs = [];
        private readonly HashSet<IkEffector> _unreachable = [];
        private Position3DTrack? _keyedHips;

        public PoseBakeResult Run()
        {
            var poses = ResolvePoses();
            PrepareArcs(poses);
            var frames = SolveKeys(poses);
            var animation = BuildAnimation(frames);
            var refinements = Refine(animation);
            if (refinements > 0)
                _notes.Add($"Added {refinements} refinement key(s) so pins and arcs hold within {tolerance * 100f:0.#} cm.");
            animation.BakedHash = AnimationHash.Compute(animation);
            return new PoseBakeResult
            {
                Animation = animation,
                Notes = _notes,
                RefinementKeys = refinements,
                HipsBone = _skeleton.GetBoneName(_hips),
            };
        }

        // ── Key poses ───────────────────────────────────────────────────────

        private List<HumanoidPose> ResolvePoses()
        {
            var poses = new List<HumanoidPose>();
            var folded = new HashSet<HumanoidBone>();
            HumanoidPose? previous = null;
            foreach (var key in script.Keys)
            {
                var pose = key.Pose is { } name
                    ? HumanoidPoses.Get(name, rig) ?? throw new InvalidOperationException($"Unknown pose '{name}'.")
                    : previous!.Clone();
                if (key.Hips is { } hips)
                    pose.Hips = hips.ApplyTo(pose.Hips);
                foreach (var (bone, angles) in key.Joints)
                {
                    pose[bone] = angles.ApplyTo(pose[bone]);
                    if (rig.GetRigBone(bone) < 0 && folded.Add(bone))
                        _notes.Add($"{bone} is not mapped on this rig; its angles turn the next mapped bone below it.");
                }

                if (key.Mirror)
                    foreach (var bone in key.Joints.Keys)
                        if (HumanoidBones.GetSide(bone) != HumanoidSide.Centre && !key.Joints.ContainsKey(HumanoidBones.Mirror(bone)))
                            pose[HumanoidBones.Mirror(bone)] = pose[bone];
                poses.Add(pose);
                previous = pose;
            }

            return poses;
        }

        // ── Arcs ────────────────────────────────────────────────────────────

        private void PrepareArcs(List<HumanoidPose> poses)
        {
            if (script.Arcs.Count == 0)
                return;
            if (script.Root == PoseRootMode.InPlace)
            {
                _notes.Add("Arcs are ignored with root \"inPlace\" (the game's CharacterBody3D does the jumping); use root \"motion\" to keep them.");
                return;
            }

            // The keyed hips offsets as the clip will play them (cubic, eased), in canonical metres.
            _keyedHips = new Position3DTrack
            {
                Interpolation = AnimationInterpolation.Cubic,
                Times = [.. script.Keys.Select(k => k.Time)],
                Values = [.. poses.Select(p => p.Hips.Offset)],
                Transitions = [.. script.Keys.Select(k => k.Ease ?? 1f)],
            };

            foreach (var arc in script.Arcs)
            {
                var flight = arc.To - arc.From;
                var gravityK = gravity * flight * flight / 8f;
                var k = gravityK;
                if (arc.Height is { } height)
                {
                    k = SolveArcK(arc, height);
                    var gravityApex = ArcApex(arc, gravityK);
                    if (gravityApex > 1e-4f && MathF.Abs(height - gravityApex) / gravityApex > 0.25f)
                        _notes.Add($"The arc {Seconds(arc.From)}–{Seconds(arc.To)} s rises {height:0.##} m; gravity ({gravity:0.##} m/s²) " +
                                   $"over that flight gives {gravityApex:0.##} m. Fine for game feel; a physics jump will differ.");
                }

                _arcs.Add((arc.From, arc.To, k));
            }
        }

        private float KeyedHipsY(double time)
        {
            var cursor = -1;
            return _keyedHips!.Sample(time, ref cursor, script.Length, false).Y;
        }

        // The apex of an arc above its take-off: the keyed height plus 4k·u(1−u), sampled.
        private float ArcApex(PoseArc arc, float k)
        {
            var start = KeyedHipsY(arc.From);
            var apex = float.NegativeInfinity;
            for (var i = 0; i <= 64; i++)
            {
                var u = i / 64f;
                apex = MathF.Max(apex, KeyedHipsY(arc.From + u * (arc.To - arc.From)) - start + 4f * k * u * (1f - u));
            }

            return apex;
        }

        private float SolveArcK(PoseArc arc, float height)
        {
            float low = 0f, high = Math.Max(height, 0.01f) * 4f + 10f;
            for (var i = 0; i < 48; i++)
            {
                var mid = (low + high) * 0.5f;
                if (ArcApex(arc, mid) < height)
                    low = mid;
                else
                    high = mid;
            }

            return (low + high) * 0.5f;
        }

        private float ArcOffset(double time)
        {
            var offset = 0f;
            foreach (var (from, to, k) in _arcs)
            {
                if (time < from || time > to)
                    continue;
                var u = (float)((time - from) / (to - from));
                offset += 4f * k * u * (1f - u);
            }

            return offset;
        }

        private bool InArc(double time) => _arcs.Any(a => time > a.From && time < a.To);

        // ── Solving the keys ────────────────────────────────────────────────

        private List<Frame> SolveKeys(List<HumanoidPose> poses)
        {
            var frames = new List<Frame>();
            var pins = new Dictionary<IkEffector, Pin>();
            for (var k = 0; k < script.Keys.Count; k++)
            {
                var key = script.Keys[k];
                var pose = poses[k].Clone();
                if (_arcs.Count > 0)
                    pose.Hips = pose.Hips with { Offset = pose.Hips.Offset with { Y = pose.Hips.Offset.Y + ArcOffset(key.Time) } };
                _skeleton.ResetBonePoses();
                rig.ApplyPose(pose);

                foreach (var (effector, command) in key.Ik)
                    if (command.Kind == IkCommandKind.Release)
                        pins.Remove(effector);

                foreach (var (effector, pin) in pins)
                {
                    var end = Chain(effector).End;
                    Reach(effector, pin.Position, key.Joints.ContainsKey(end) ? null : pin.EndRotation, $"keys[{k}] (t = {Seconds(key.Time)} s)");
                }

                foreach (var (effector, command) in key.Ik)
                    if (command.Kind == IkCommandKind.Target)
                        Reach(effector, rig.ToSkeletonSpace(command.Target), null, $"keys[{k}] (t = {Seconds(key.Time)} s)");

                foreach (var (effector, command) in key.Ik)
                {
                    if (command.Kind != IkCommandKind.Pin)
                        continue;
                    var end = _skeleton.GetBoneGlobalPose(rig.GetRigBone(Chain(effector).End));
                    pins[effector] = new Pin(end.Origin, end.Basis.GetRotation());
                }

                _pinsAfter.Add(new Dictionary<IkEffector, Pin>(pins));
                frames.Add(new Frame(key.Time, [.. _bones.Select(b => _skeleton.GetBonePoseRotation(rig.GetRigBone(b)))],
                    _skeleton.GetBonePosePosition(_hips), key.Ease ?? 1f));
            }

            return frames;
        }

        // Reaches for `target`; returns how far it stays short (metres). Notes a miss at a key (`where` names it).
        private float Reach(IkEffector effector, Vector3 target, Quaternion? endRotation, string where)
        {
            var (upper, lower, end, pole) = Chain(effector);
            if (rig.GetRigBone(upper) < 0 || rig.GetRigBone(lower) < 0 || rig.GetRigBone(end) < 0)
            {
                _notes.Add($"{where}: {effector} has no mapped chain on this rig; ignored.");
                return 0f;
            }

            var miss = TwoBoneIk.Apply(_skeleton, rig.GetRigBone(upper), rig.GetRigBone(lower), rig.GetRigBone(end), target,
                rig.CurrentDirection(upper, pole), endRotation) * rig.MetresPerUnit;
            if (where.Length > 0 && miss > tolerance)
                _notes.Add($"{where}: {effector} cannot reach its target ({miss:0.###} m short); the limb straightens towards it.");
            return miss;
        }

        // ── Tracks ──────────────────────────────────────────────────────────

        private Animation BuildAnimation(List<Frame> frames)
        {
            var animation = new Animation { ResourceName = script.Name, Length = script.Length, LoopMode = script.Loop };
            var times = frames.Select(f => f.Time).ToArray();
            var transitions = frames.Select(f => f.Ease).ToArray();
            var ikBones = script.Keys.SelectMany(k => k.Ik.Keys).Distinct()
                .SelectMany(e => new[] { Chain(e).Upper, Chain(e).Lower, Chain(e).End }).ToHashSet();

            for (var j = 0; j < _bones.Length; j++)
            {
                var index = rig.GetRigBone(_bones[j]);
                var rest = _skeleton.GetBoneRest(index).Basis.GetRotation();
                var values = frames.Select(f => f.Rotations[j]).ToArray();
                if (!ikBones.Contains(_bones[j]) && values.All(q => MathF.Abs(Quaternion.Dot(q, rest)) > 0.9999999f))
                    continue; // never moves
                for (var i = 1; i < values.Length; i++)
                    if (Quaternion.Dot(values[i], values[i - 1]) < 0f)
                        values[i] = -values[i];
                animation.Tracks.Add(new Rotation3DTrack
                {
                    Path = skeletonPath,
                    Bone = _skeleton.GetBoneName(index),
                    Interpolation = AnimationInterpolation.Cubic,
                    Times = [.. times],
                    Values = values,
                    Transitions = [.. transitions],
                });
            }

            var restPosition = _skeleton.GetBoneRest(_hips).Origin;
            var positions = frames.Select(f => f.HipsPosition).ToArray();
            if (_arcs.Count > 0 || positions.Any(p => Vector3.DistanceSquared(p, restPosition) > 1e-12f))
            {
                animation.Tracks.Add(new Position3DTrack
                {
                    Path = skeletonPath,
                    Bone = _skeleton.GetBoneName(_hips),
                    Interpolation = AnimationInterpolation.Cubic,
                    Times = [.. times],
                    Values = positions,
                    Transitions = [.. transitions],
                });
            }

            animation.NotifyChanged();
            return animation;
        }

        // ── Refinement ──────────────────────────────────────────────────────

        private enum Source : byte
        {
            None,
            Pin,
            Arc,
        }

        private int Refine(Animation animation)
        {
            var samples = (int)MathF.Ceiling(script.Length * SampleRate);
            var added = 0;
            for (var iteration = 0; iteration <= samples * 4; iteration++)
            {
                var worst = 0f;
                var worstTime = 0.0;
                var worstSource = Source.None;
                var worstEffector = IkEffector.LeftFoot;
                for (var i = 0; i <= samples; i++)
                {
                    var time = Math.Min((double)i / SampleRate, script.Length);
                    PosePlayed(animation, time);
                    foreach (var (effector, pin) in PinsAt(time))
                    {
                        var at = _skeleton.GetBoneGlobalPose(rig.GetRigBone(Chain(effector).End)).Origin;
                        var error = Vector3.Distance(at, pin.Position) * rig.MetresPerUnit;
                        if (error > worst && !HasKey(animation, _skeleton.GetBoneName(rig.GetRigBone(Chain(effector).Upper)), time))
                            (worst, worstTime, worstSource, worstEffector) = (error, time, Source.Pin, effector);
                    }

                    if (InArc(time))
                    {
                        var error = Vector3.Distance(_skeleton.GetBoneGlobalPose(_hips).Origin, IntendedHips(time)) * rig.MetresPerUnit;
                        if (error > worst && !HasKey(animation, _skeleton.GetBoneName(_hips), time))
                            (worst, worstTime, worstSource) = (error, time, Source.Arc);
                    }
                }

                if (worst <= tolerance || worstSource == Source.None)
                    return added;

                // The intended pose at the worst sample: the clip as played, the arc's hips, the pins reached.
                PosePlayed(animation, worstTime);
                if (InArc(worstTime))
                    SetHipsGlobal(IntendedHips(worstTime));
                foreach (var (effector, pin) in PinsAt(worstTime))
                {
                    var miss = Reach(effector, pin.Position, pin.EndRotation, "");
                    if (miss > tolerance && _unreachable.Add(effector))
                        _notes.Add($"t = {Seconds((float)worstTime)} s: the pinned {effector} is out of reach ({miss:0.###} m) and slides; " +
                                   "lower the hips or release the pin earlier.");
                }

                if (worstSource == Source.Arc)
                {
                    Insert(animation, _hips, worstTime, position: true);
                    foreach (var (effector, _) in PinsAt(worstTime))
                        InsertChain(animation, effector, worstTime);
                }
                else
                {
                    InsertChain(animation, worstEffector, worstTime);
                }

                added++;
            }

            _notes.Add("Refinement stopped at its key limit; a pin or arc may still miss.");
            return added;
        }

        private void PosePlayed(Animation animation, double time)
        {
            _skeleton.ResetBonePoses();
            ClipSampler.Apply(animation, _skeleton, skeletonPath, time);
        }

        // The pins held during the segment that contains `time` (from a pin key up to its release key).
        private Dictionary<IkEffector, Pin> PinsAt(double time)
        {
            var segment = 0;
            for (var k = 0; k < script.Keys.Count; k++)
                if (script.Keys[k].Time <= time)
                    segment = k;
            return _pinsAfter[segment];
        }

        private Vector3 IntendedHips(double time)
        {
            var offset = _keyedHips!.Sample(time);
            offset.Y += ArcOffset(time);
            var restGlobal = _skeleton.GetBoneGlobalRest(_hips).Origin;
            return restGlobal + rig.ToSkeletonSpace(offset);
        }

        private void SetHipsGlobal(Vector3 position)
        {
            var parent = _skeleton.GetBoneParent(_hips);
            if (parent >= 0)
                position = _skeleton.GetBoneGlobalPose(parent).AffineInverse().TransformPoint(position);
            _skeleton.SetBonePosePosition(_hips, position);
        }

        // True when the bone's track already has a key at `time` (a key there cannot be refined further).
        private static bool HasKey(Animation animation, string bone, double time)
        {
            foreach (var track in animation.Tracks)
            {
                var name = track switch { Rotation3DTrack r => r.Bone, Position3DTrack p => p.Bone, _ => null };
                if (name == bone && track.Times.Any(t => Math.Abs(t - time) < 1e-4))
                    return true;
            }

            return false;
        }

        private void InsertChain(Animation animation, IkEffector effector, double time)
        {
            var (upper, lower, end, _) = Chain(effector);
            foreach (var bone in new[] { upper, lower, end })
                Insert(animation, rig.GetRigBone(bone), time, position: false);
        }

        // Inserts the skeleton's current local value of `bone` at `time` into its track (every track of the clip gets the
        // key time only where needed; tracks stay sorted and in one quaternion hemisphere).
        private void Insert(Animation animation, int bone, double time, bool position)
        {
            var name = _skeleton.GetBoneName(bone);
            var t = (float)time;
            foreach (var track in animation.Tracks)
            {
                if (track is Rotation3DTrack rotation && !position && rotation.Bone == name)
                {
                    var at = InsertIndex(rotation.Times, t);
                    var value = _skeleton.GetBonePoseRotation(bone);
                    if (at > 0 && Quaternion.Dot(value, rotation.Values[at - 1]) < 0f)
                        value = -value;
                    rotation.Times = [.. rotation.Times[..at], t, .. rotation.Times[at..]];
                    rotation.Values = [.. rotation.Values[..at], value, .. rotation.Values[at..]];
                    rotation.Transitions = InsertTransition(rotation.Transitions, at);
                    // Keep the following key in the same hemisphere as the new one.
                    if (at + 1 < rotation.Values.Length && Quaternion.Dot(rotation.Values[at + 1], value) < 0f)
                        for (var i = at + 1; i < rotation.Values.Length; i++)
                            rotation.Values[i] = -rotation.Values[i];
                }
                else if (track is Position3DTrack positionTrack && position && positionTrack.Bone == name)
                {
                    var at = InsertIndex(positionTrack.Times, t);
                    positionTrack.Times = [.. positionTrack.Times[..at], t, .. positionTrack.Times[at..]];
                    positionTrack.Values = [.. positionTrack.Values[..at], _skeleton.GetBonePosePosition(bone), .. positionTrack.Values[at..]];
                    positionTrack.Transitions = InsertTransition(positionTrack.Transitions, at);
                }
            }

            animation.NotifyChanged();
        }

        private static int InsertIndex(float[] times, float t)
        {
            var at = 0;
            while (at < times.Length && times[at] < t)
                at++;
            return at;
        }

        // The new key splits the segment before it: it takes that segment's ease.
        private static float[] InsertTransition(float[] transitions, int at)
        {
            var ease = at > 0 && at - 1 < transitions.Length ? transitions[at - 1] : 1f;
            return [.. transitions[..Math.Min(at, transitions.Length)], ease, .. transitions[Math.Min(at, transitions.Length)..]];
        }

        public (Vector3, Quaternion, Vector3)[] SavePose()
        {
            var count = _skeleton.GetBoneCount();
            var saved = new (Vector3, Quaternion, Vector3)[count];
            for (var i = 0; i < count; i++)
                saved[i] = (_skeleton.GetBonePosePosition(i), _skeleton.GetBonePoseRotation(i), _skeleton.GetBonePoseScale(i));
            return saved;
        }

        public void RestorePose((Vector3 Position, Quaternion Rotation, Vector3 Scale)[] saved)
        {
            for (var i = 0; i < saved.Length; i++)
            {
                _skeleton.SetBonePosePosition(i, saved[i].Position);
                _skeleton.SetBonePoseRotation(i, saved[i].Rotation);
                _skeleton.SetBonePoseScale(i, saved[i].Scale);
            }
        }
    }
}
