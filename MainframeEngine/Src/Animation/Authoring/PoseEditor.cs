using System.Globalization;
using System.Numerics;

namespace MainframeEngine;

/// <summary>What <see cref="PoseEditor.Apply"/> made.</summary>
public sealed class PoseEditResult
{
    /// <summary>The edited clip, a copy of the base named after the script (the base is not changed).</summary>
    public required Animation Animation { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>Existing keys whose value or ease changed.</summary>
    public int ChangedKeys { get; init; }

    /// <summary>Keys added to tracks at edited times (inserts, a bone keyed where its track had no key, new tracks).</summary>
    public int AddedKeys { get; init; }

    /// <summary>Keys added between keys so pinned effectors hold.</summary>
    public int RefinementKeys { get; init; }
}

/// <summary>
/// Applies an edit script on top of a clip's current keys (G1c, "keys are the truth"): values at a key, over a range or at an
/// inserted key, a rescaled arc, IK pins that hold planted limbs. Key times, counts and eases of everything not named stay
/// as they were, hand-placed keys included. A clip that was hand-edited (or hand-made) stays marked so
/// (<see cref="Animation.BakedHash"/>); an untouched bake stays untouched.
/// </summary>
public static class PoseEditor
{
    /// <summary>
    /// Applies <paramref name="script"/> to a copy of <paramref name="baseClip"/>. Throws <see cref="PoseScriptException"/>
    /// with every problem found (an edit at a time with no key, an unmapped bone, an arc with nothing to scale, …).
    /// </summary>
    public static PoseEditResult Apply(PoseEditScript script, Animation baseClip, HumanoidRig rig, NodePath skeletonPath,
        float tolerance = PoseBaker.DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(baseClip);
        ArgumentNullException.ThrowIfNull(rig);
        var clip = (Animation)baseClip.Duplicate(deep: true);
        clip.ResourceName = script.Name;
        var editing = new Editing(clip, rig, skeletonPath, tolerance);
        var saved = ClipDescriber.SavePose(rig.Skeleton);
        try
        {
            foreach (var edit in script.Edits)
                editing.Apply(edit);
        }
        finally
        {
            ClipDescriber.RestorePose(rig.Skeleton, saved);
        }

        if (editing.Errors.Count > 0)
            throw new PoseScriptException(editing.Errors);

        clip.NotifyChanged();
        clip.BakedHash = PoseLibrary.IsUntouchedBake(baseClip) ? AnimationHash.Compute(clip) : baseClip.BakedHash;
        if (baseClip.BakedHash.Length > 0 && !PoseLibrary.IsUntouchedBake(baseClip))
            editing.Notes.Add("The base clip has hand edits; they are kept, and the clip stays marked as changed since its bake.");
        return new PoseEditResult
        {
            Animation = clip,
            Notes = editing.Notes,
            ChangedKeys = editing.Changed,
            AddedKeys = editing.Added,
            RefinementKeys = editing.Refinements,
        };
    }

    private static string S(double t) => t.ToString("0.###", CultureInfo.InvariantCulture);

    private readonly record struct KeyValue(int Bone, bool Position, float Time, Quaternion Rotation, Vector3 Translation);

    private sealed class Editing(Animation clip, HumanoidRig rig, NodePath skeletonPath, float tolerance)
    {
        private readonly Skeleton3D _skeleton = rig.Skeleton;
        private readonly int _hips = rig.GetRigBone(HumanoidBone.Hips);

        public List<PoseScriptError> Errors { get; } = [];

        public List<string> Notes { get; } = [];

        public int Changed { get; private set; }

        public int Added { get; private set; }

        public int Refinements { get; private set; }

        public void Apply(PoseEdit edit)
        {
            var before = Errors.Count;
            CheckBones(edit);
            var times = Times(edit);
            if (Errors.Count > before || times is null)
                return;

            if (edit.ArcHeight is { } height && edit.Range is { } range)
            {
                ScaleArc(edit.Path, range.From, range.To, height);
                if (Errors.Count > before)
                    return;
            }

            if (edit.Hips is null && edit.Joints.Count == 0 && edit.Ik.Count == 0 && edit.Ease is null)
                return;

            var original = (Animation)clip.Duplicate(deep: true);
            var keyTimesBefore = ClipDescriber.KeyTimes(original, skeletonPath);
            var values = new List<KeyValue>();
            var pins = new Dictionary<IkEffector, Dictionary<float, (Vector3 Position, Quaternion Rotation)>>();
            foreach (var time in times)
                Solve(edit, original, time, values, pins);

            Write(values, original, keyTimesBefore, edit);
            if (edit.Ease is { } ease)
                SetEase(times, ease);
            var refinedBefore = Refinements;
            foreach (var (effector, pinned) in pins)
                RefinePin(edit, original, effector, pinned);
            if (Refinements > refinedBefore)
                Notes.Add($"{edit.Path}: added {Refinements - refinedBefore} refinement key(s) so pinned limbs hold within {tolerance * 100f:0.#} cm.");
            clip.NotifyChanged();
        }

        // ── Validation ──────────────────────────────────────────────────────

        private void CheckBones(PoseEdit edit)
        {
            foreach (var bone in edit.Joints.Keys)
            {
                if (rig.GetRigBone(bone) < 0)
                    Errors.Add(new($"{edit.Path}.joints.{bone}", "not mapped on this rig (mfe anim rig shows the map)"));
                if (edit.Mirror && HumanoidBones.GetSide(bone) != HumanoidSide.Centre && !edit.Joints.ContainsKey(HumanoidBones.Mirror(bone)) &&
                    rig.GetRigBone(HumanoidBones.Mirror(bone)) < 0)
                    Errors.Add(new($"{edit.Path}.mirror", $"{HumanoidBones.Mirror(bone)} is not mapped on this rig"));
            }

            foreach (var effector in edit.Ik.Keys)
            {
                var (upper, lower, end, _) = PoseBaker.Chain(effector);
                if (rig.GetRigBone(upper) < 0 || rig.GetRigBone(lower) < 0 || rig.GetRigBone(end) < 0)
                    Errors.Add(new($"{edit.Path}.ik.{ClipDescriber.EffectorName(effector)}", "this rig has no mapped chain for it"));
            }
        }

        private List<float>? Times(PoseEdit edit)
        {
            var keyTimes = ClipDescriber.KeyTimes(clip, skeletonPath);
            var listed = string.Join(", ", keyTimes.Select(t => S(t)));
            if (edit.Time is { } time)
            {
                if (time < -TrackKeys.TimeTolerance || time > clip.Length + TrackKeys.TimeTolerance)
                {
                    Errors.Add(new($"{edit.Path}.t", $"{S(time)} is outside the clip (0 to {S(clip.Length)})"));
                    return null;
                }

                var at = TrackKeys.Find([.. keyTimes], time);
                if (at >= 0)
                    return [keyTimes[at]];
                if (edit.Insert)
                    return [Math.Clamp(time, 0f, clip.Length)];
                Errors.Add(new($"{edit.Path}.t", $"no key at {S(time)} s (key times: {listed}); add one with \"insert\": true"));
                return null;
            }

            var (from, to) = edit.Range!.Value;
            if (from < -TrackKeys.TimeTolerance || to > clip.Length + TrackKeys.TimeTolerance)
            {
                Errors.Add(new($"{edit.Path}.range", $"[{S(from)}, {S(to)}] is outside the clip (0 to {S(clip.Length)})"));
                return null;
            }

            var inRange = keyTimes.Where(t => t >= from - TrackKeys.TimeTolerance && t <= to + TrackKeys.TimeTolerance).ToList();
            if (inRange.Count == 0 && edit.ArcHeight is null)
            {
                Errors.Add(new($"{edit.Path}.range", $"no keys in [{S(from)}, {S(to)}] (key times: {listed})"));
                return null;
            }

            return inRange;
        }

        // ── Solving the edited keys (on the clip as it was before this edit) ──

        private void Solve(PoseEdit edit, Animation original, float time, List<KeyValue> values,
            Dictionary<IkEffector, Dictionary<float, (Vector3, Quaternion)>> pins)
        {
            Pose(original, time);
            var ends = new Dictionary<IkEffector, (Vector3 Position, Quaternion Rotation)>();
            foreach (var effector in edit.Ik.Keys)
            {
                var end = _skeleton.GetBoneGlobalPose(rig.GetRigBone(PoseBaker.Chain(effector).End));
                ends[effector] = (end.Origin, end.Basis.GetRotation());
            }

            var pose = rig.ReadPose();
            if (edit.Hips is { } hips)
                pose.Hips = hips.ApplyTo(pose.Hips);
            var written = new HashSet<HumanoidBone>();
            foreach (var (bone, angles) in edit.Joints)
            {
                pose[bone] = angles.ApplyTo(pose[bone]);
                written.Add(bone);
            }

            if (edit.Mirror)
            {
                foreach (var (bone, angles) in edit.Joints)
                {
                    var mirror = HumanoidBones.Mirror(bone);
                    if (HumanoidBones.GetSide(bone) == HumanoidSide.Centre || edit.Joints.ContainsKey(mirror))
                        continue;
                    pose[mirror] = angles.ApplyTo(pose[mirror]);
                    written.Add(mirror);
                }
            }

            rig.ApplyPose(pose);

            foreach (var (effector, command) in edit.Ik)
            {
                var (upper, lower, end, pole) = PoseBaker.Chain(effector);
                Vector3 target;
                Quaternion? endRotation = null;
                if (command.Kind == IkCommandKind.Pin)
                {
                    target = ends[effector].Position;
                    if (!edit.Joints.ContainsKey(end) && !(edit.Mirror && edit.Joints.ContainsKey(HumanoidBones.Mirror(end))))
                        endRotation = ends[effector].Rotation;
                    if (!pins.TryGetValue(effector, out var pinned))
                        pins[effector] = pinned = [];
                    pinned[time] = ends[effector];
                }
                else
                {
                    target = rig.ToSkeletonSpace(command.Target);
                }

                var miss = TwoBoneIk.Apply(_skeleton, rig.GetRigBone(upper), rig.GetRigBone(lower), rig.GetRigBone(end), target,
                    rig.CurrentDirection(upper, pole), endRotation) * rig.MetresPerUnit;
                if (miss > tolerance)
                    Notes.Add($"{edit.Path} (t = {S(time)} s): {ClipDescriber.EffectorName(effector)} cannot reach ({miss:0.###} m short); the limb straightens towards it.");
                written.Add(upper);
                written.Add(lower);
                written.Add(end);
            }

            foreach (var bone in written)
            {
                var index = rig.GetRigBone(bone);
                values.Add(new KeyValue(index, false, time, _skeleton.GetBonePoseRotation(index), default));
            }

            if (edit.Hips is { } h)
            {
                if (h.Turns)
                    values.Add(new KeyValue(_hips, false, time, _skeleton.GetBonePoseRotation(_hips), default));
                if (h.MovesPosition)
                    values.Add(new KeyValue(_hips, true, time, default, _skeleton.GetBonePosePosition(_hips)));
            }
        }

        // ── Writing keys ────────────────────────────────────────────────────

        private void Write(List<KeyValue> values, Animation original, IReadOnlyList<float> keyTimesBefore, PoseEdit edit)
        {
            foreach (var group in values.GroupBy(v => (v.Bone, v.Position)))
            {
                var name = _skeleton.GetBoneName(group.Key.Bone);
                InterpolatedTrack? track = group.Key.Position
                    ? clip.Tracks.OfType<Position3DTrack>().FirstOrDefault(t => t.Path == skeletonPath && t.Bone == name)
                    : clip.Tracks.OfType<Rotation3DTrack>().FirstOrDefault(t => t.Path == skeletonPath && t.Bone == name);
                if (track is null)
                {
                    track = NewTrack(group.Key.Bone, group.Key.Position, group.Select(v => v.Time).ToList(), original, keyTimesBefore);
                    Notes.Add($"{edit.Path}: {name} had no {(group.Key.Position ? "position" : "rotation")} track; added one keyed around the edit.");
                }

                foreach (var value in group)
                {
                    var existed = TrackKeys.Find(track.Times, value.Time) >= 0;
                    if (track is Rotation3DTrack rotation)
                        TrackKeys.SetRotation(rotation, value.Time, value.Rotation);
                    else
                        TrackKeys.SetPosition((Position3DTrack)track, value.Time, value.Translation);
                    if (existed)
                        Changed++;
                    else
                        Added++;
                }
            }
        }

        // A track for a bone the clip never keyed: its current value at the clip key times just outside the edited ones, so
        // the change stays between them (the edited keys themselves are set afterwards).
        private InterpolatedTrack NewTrack(int bone, bool position, List<float> edited, Animation original, IReadOnlyList<float> keyTimes)
        {
            var first = edited.Min();
            var last = edited.Max();
            var times = new List<float>();
            if (keyTimes.LastOrDefault(t => t < first - TrackKeys.TimeTolerance, float.NaN) is var before && !float.IsNaN(before))
                times.Add(before);
            if (keyTimes.FirstOrDefault(t => t > last + TrackKeys.TimeTolerance, float.NaN) is var after && !float.IsNaN(after))
                times.Add(after);
            var rotations = new List<Quaternion>();
            var positions = new List<Vector3>();
            foreach (var time in times)
            {
                Pose(original, time);
                rotations.Add(_skeleton.GetBonePoseRotation(bone));
                positions.Add(_skeleton.GetBonePosePosition(bone));
            }

            var transitions = times.Select(t => EaseAt(t)).ToArray();
            InterpolatedTrack track = position
                ? new Position3DTrack { Path = skeletonPath, Bone = _skeleton.GetBoneName(bone), Times = [.. times], Values = [.. positions] }
                : new Rotation3DTrack { Path = skeletonPath, Bone = _skeleton.GetBoneName(bone), Times = [.. times], Values = [.. rotations] };
            track.Interpolation = AnimationInterpolation.Cubic;
            track.Transitions = transitions;
            Added += times.Count;
            clip.Tracks.Add(track);
            return track;
        }

        // The ease most tracks keyed at `time` give the segment after it (1 when none is keyed there).
        private float EaseAt(float time)
        {
            var eases = ClipDescriber.SkeletonTracks(clip, skeletonPath)
                .Select(t => (Track: t, Index: TrackKeys.Find(t.Times, time)))
                .Where(x => x.Index >= 0)
                .Select(x => x.Index < x.Track.Transitions.Length ? x.Track.Transitions[x.Index] : 1f)
                .ToList();
            return eases.Count == 0 ? 1f : eases.GroupBy(e => e).OrderByDescending(g => g.Count()).First().Key;
        }

        private void SetEase(List<float> times, float ease)
        {
            foreach (var track in ClipDescriber.SkeletonTracks(clip, skeletonPath))
            {
                foreach (var time in times)
                {
                    var at = TrackKeys.Find(track.Times, time);
                    if (at < 0)
                        continue;
                    if (track.Transitions.Length < track.Times.Length)
                        track.Transitions = [.. track.Transitions, .. Enumerable.Repeat(1f, track.Times.Length - track.Transitions.Length)];
                    if (track.Transitions[at] != ease)
                    {
                        track.Transitions[at] = ease;
                        Changed++;
                    }
                }
            }
        }

        // ── Pins between keys ───────────────────────────────────────────────

        // Every span of key times around an edited time where the effector was planted (pre-edit) at the same spot is sampled
        // at 60 Hz, with the chain's keys inside it; chain keys are re-solved or added where it slides more than the tolerance.
        private void RefinePin(PoseEdit edit, Animation original, IkEffector effector, Dictionary<float, (Vector3 Position, Quaternion Rotation)> pinned)
        {
            var (upper, lower, end, pole) = PoseBaker.Chain(effector);
            var endIndex = rig.GetRigBone(end);
            var keepRotation = !edit.Joints.ContainsKey(end);
            var keyTimes = ClipDescriber.KeyTimes(clip, skeletonPath);
            // From each edited time, out across the adjacent key times while the effector was planted there before the edit.
            var spans = new List<(float From, float To, Vector3 Position, Quaternion Rotation)>();
            foreach (var (time, at) in pinned.OrderBy(p => p.Key))
            {
                var index = TrackKeys.Find([.. keyTimes], time);
                if (index < 0 || spans.Any(s => time >= s.From - TrackKeys.TimeTolerance && time <= s.To + TrackKeys.TimeTolerance))
                    continue;
                var first = index;
                while (first > 0 && PlantedAt(original, keyTimes[first - 1], endIndex, at.Position))
                    first--;
                var last = index;
                while (last + 1 < keyTimes.Count && PlantedAt(original, keyTimes[last + 1], endIndex, at.Position))
                    last++;
                if (last > first)
                    spans.Add((keyTimes[first], keyTimes[last], at.Position, at.Rotation));
            }

            var upperName = _skeleton.GetBoneName(rig.GetRigBone(upper));
            var unreachable = new HashSet<float>();
            foreach (var (from, to, position, rotation) in spans)
            {
                var samples = (int)MathF.Ceiling((to - from) * PoseBaker.SampleRate) + 1;
                for (var iteration = 0; iteration <= samples * 2; iteration++)
                {
                    var worst = tolerance;
                    var worstTime = float.NaN;
                    foreach (var time in Candidates(from, to, upperName))
                    {
                        if (unreachable.Contains(time))
                            continue;
                        Pose(clip, time);
                        var error = Vector3.Distance(_skeleton.GetBoneGlobalPose(endIndex).Origin, position) * rig.MetresPerUnit;
                        if (error > worst)
                            (worst, worstTime) = (error, time);
                    }

                    if (float.IsNaN(worstTime))
                        break;
                    Pose(clip, worstTime);
                    var miss = TwoBoneIk.Apply(_skeleton, rig.GetRigBone(upper), rig.GetRigBone(lower), endIndex, position,
                        rig.CurrentDirection(upper, pole), keepRotation ? rotation : null) * rig.MetresPerUnit;
                    if (miss > tolerance)
                    {
                        unreachable.Add(worstTime);
                        Notes.Add($"{edit.Path}: at t = {S(worstTime)} s the pinned {ClipDescriber.EffectorName(effector)} is out of reach " +
                                  $"({miss:0.###} m) and slides; lower the hips less or edit the leg too.");
                    }

                    var isNew = TrackKeys.Find(clip.Tracks.OfType<Rotation3DTrack>().First(t => t.Path == skeletonPath && t.Bone == upperName).Times, worstTime) < 0;
                    foreach (var bone in new[] { upper, lower, end })
                    {
                        var index = rig.GetRigBone(bone);
                        var name = _skeleton.GetBoneName(index);
                        var track = clip.Tracks.OfType<Rotation3DTrack>().First(t => t.Path == skeletonPath && t.Bone == name);
                        TrackKeys.SetRotation(track, worstTime, _skeleton.GetBonePoseRotation(index));
                    }

                    clip.NotifyChanged();
                    if (isNew)
                        Refinements++;
                    else
                        Changed += 3;
                }
            }
        }

        // The 60 Hz samples strictly inside a span, plus the upper bone's keys there (hand-placed or refinement keys).
        private IEnumerable<float> Candidates(float from, float to, string upperName)
        {
            var track = clip.Tracks.OfType<Rotation3DTrack>().First(t => t.Path == skeletonPath && t.Bone == upperName);
            foreach (var time in track.Times)
                if (time > from + TrackKeys.TimeTolerance && time < to - TrackKeys.TimeTolerance)
                    yield return time;
            for (var i = (int)MathF.Ceiling(from * PoseBaker.SampleRate); i / (float)PoseBaker.SampleRate < to; i++)
            {
                var time = i / (float)PoseBaker.SampleRate;
                if (time > from + TrackKeys.TimeTolerance && time < to - TrackKeys.TimeTolerance && TrackKeys.Find(track.Times, time) < 0)
                    yield return time;
            }
        }

        private bool PlantedAt(Animation original, float time, int endIndex, Vector3 position)
        {
            Pose(original, time);
            return Vector3.Distance(_skeleton.GetBoneGlobalPose(endIndex).Origin, position) * rig.MetresPerUnit <= tolerance;
        }

        // ── Arcs ────────────────────────────────────────────────────────────

        private void ScaleArc(string path, float from, float to, EditValue height)
        {
            var hipsName = _skeleton.GetBoneName(_hips);
            var track = clip.Tracks.OfType<Position3DTrack>().FirstOrDefault(t => t.Path == skeletonPath && t.Bone == hipsName);
            if (track is null)
            {
                Errors.Add(new(path + ".arc", "the clip does not move the hips (no hips position track)"));
                return;
            }

            var inside = Enumerable.Range(0, track.Times.Length)
                .Where(i => track.Times[i] > from + TrackKeys.TimeTolerance && track.Times[i] < to - TrackKeys.TimeTolerance).ToArray();
            if (inside.Length == 0)
            {
                Errors.Add(new(path + ".range", $"no hips keys inside ({S(from)}, {S(to)}) to raise; insert one first"));
                return;
            }

            var current = Apex(from, to);
            if (current < 0.001f)
            {
                Errors.Add(new(path + ".arc", $"the hips do not rise between {S(from)} and {S(to)} s; there is no arc to scale"));
                return;
            }

            var target = height.ApplyTo(current);
            if (target <= 0f)
            {
                Errors.Add(new(path + ".arc.height", $"gives {target:0.###} m; the apex must stay above take-off"));
                return;
            }

            // The hips' up axis in their parent's space (the parent at rest), and the take-off-to-landing line.
            var parent = _skeleton.GetBoneParent(_hips);
            var parentRotation = parent >= 0 ? _skeleton.GetBoneGlobalRest(parent).Basis.GetRotation() : Quaternion.Identity;
            var up = Vector3.Normalize(Vector3.Transform(Vector3.Transform(Vector3.UnitY, rig.CharacterRotation), Quaternion.Conjugate(parentRotation)));
            var start = track.Sample(from);
            var end = track.Sample(to);
            var originals = inside.Select(i => track.Values[i]).ToArray();
            var rises = inside.Select((i, n) => Vector3.Dot(originals[n] - Vector3.Lerp(start, end, (track.Times[i] - from) / (to - from)), up)).ToArray();

            void Scale(float s)
            {
                for (var n = 0; n < inside.Length; n++)
                    track.Values[inside[n]] = originals[n] + up * (rises[n] * (s - 1f));
                clip.NotifyChanged();
            }

            float low = 0f, high = 1f;
            while (high < 64f)
            {
                Scale(high);
                if (Apex(from, to) >= target)
                    break;
                low = high;
                high *= 2f;
            }

            for (var i = 0; i < 40; i++)
            {
                var mid = (low + high) * 0.5f;
                Scale(mid);
                if (Apex(from, to) < target)
                    low = mid;
                else
                    high = mid;
            }

            Scale((low + high) * 0.5f);
            Changed += inside.Length;
            var reached = Apex(from, to);
            if (MathF.Abs(reached - target) > tolerance)
                Notes.Add($"{path}: the arc reaches {reached:0.###} m, not {target:0.###} m (the hips keys inside the range cannot shape it further).");
            else
                Notes.Add($"{path}: the arc's apex went from {current:0.###} m to {reached:0.###} m above take-off.");
        }

        // The hips' highest rise above take-off between `from` and `to` (60 Hz), in metres.
        private float Apex(float from, float to)
        {
            var startY = HipsHeight(from);
            var apex = 0f;
            var samples = Math.Max(2, (int)MathF.Ceiling((to - from) * PoseBaker.SampleRate));
            for (var i = 0; i <= samples; i++)
                apex = MathF.Max(apex, HipsHeight(from + (to - from) * i / samples) - startY);
            return apex;
        }

        private float HipsHeight(float time)
        {
            Pose(clip, time);
            return rig.ToCharacterSpace(_skeleton.GetBoneGlobalPose(_hips).Origin).Y;
        }

        private void Pose(Animation animation, double time)
        {
            _skeleton.ResetBonePoses();
            ClipSampler.Apply(animation, _skeleton, skeletonPath, time);
        }
    }
}
