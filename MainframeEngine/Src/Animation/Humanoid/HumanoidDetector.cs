using System.Numerics;
using System.Text;

namespace MainframeEngine;

/// <summary>
/// <see cref="HumanoidMap.Detect"/>'s work (ADR 0281): a name pass over every bone, a structural pass from the hips, then
/// overrides. Runs on demand (the CLI, the editor), never per frame, so it allocates freely.
/// </summary>
internal static class HumanoidDetector
{
    private const float NameConfidence = 1f;
    private const float StructureConfidence = 0.6f;

    private enum Kind : byte
    {
        None,
        Hips,
        SpineSeries,
        Chest,
        UpperChest,
        Neck,
        Head,
        Eye,
        Jaw,
        UpperLeg,
        LowerLeg,
        Foot,
        Toes,
        Shoulder,
        UpperArm,
        LowerArm,
        Hand,
        Finger,
    }

    private enum Segment : byte
    {
        Numbered,
        Metacarpal,
        Proximal,
        Intermediate,
        Distal,
    }

    private readonly record struct Candidate(Kind Kind, HumanoidSide Side, int Number, int Finger, Segment Segment);

    private sealed class Skeleton
    {
        public required string[] Names;
        public required int[] Parents;
        public required Vector3[] Positions;
        public required List<int>[] Children;
        public required int[] Depth;
        public required int[] SubtreeSize;

        public int Count => Names.Length;
    }

    public static HumanoidMap Detect(Skeleton3D skeleton3D, IReadOnlyDictionary<string, string>? overrides,
        out HumanoidMapReport report)
    {
        var skeleton = Read(skeleton3D);
        var notes = new List<string>();
        var map = new int[HumanoidBones.Count];
        var sources = new HumanoidMapSource[HumanoidBones.Count];
        Array.Fill(map, -1);

        var structure = MapByStructure(skeleton);
        MapByName(skeleton, structure, map, sources, notes);

        // Fill what names missed from the structure; note where the two disagree.
        foreach (var bone in HumanoidBones.All)
        {
            var fromStructure = structure[(int)bone];
            if (fromStructure < 0)
                continue;
            if (map[(int)bone] < 0)
            {
                if (Array.IndexOf(map, fromStructure) >= 0)
                    continue; // the rig bone is already another canonical bone by name
                map[(int)bone] = fromStructure;
                sources[(int)bone] = HumanoidMapSource.Structure;
            }
            else if (map[(int)bone] != fromStructure && HumanoidBones.IsRequired(bone))
            {
                notes.Add($"{bone}: the name says '{skeleton.Names[map[(int)bone]]}', the structure '{skeleton.Names[fromStructure]}'; using the name.");
            }
        }

        CheckSides(skeleton, map, notes);
        ApplyOverrides(skeleton, overrides, map, sources, notes);

        var entries = new List<HumanoidMapEntry>();
        var bones = new Dictionary<HumanoidBone, string>();
        var used = new bool[skeleton.Count];
        foreach (var bone in HumanoidBones.All)
        {
            var index = map[(int)bone];
            if (index < 0)
                continue;
            used[index] = true;
            bones[bone] = skeleton.Names[index];
            var confidence = sources[(int)bone] == HumanoidMapSource.Structure ? StructureConfidence : NameConfidence;
            entries.Add(new HumanoidMapEntry(bone, skeleton.Names[index], sources[(int)bone], confidence));
        }

        var missing = HumanoidBones.All.Where(b => HumanoidBones.IsRequired(b) && map[(int)b] < 0).ToList();
        foreach (var expected in new[] { HumanoidBone.Chest, HumanoidBone.Neck })
            if (map[(int)expected] < 0)
                notes.Add($"{expected} is not mapped; its angles fold into the next mapped bone.");

        report = new HumanoidMapReport
        {
            Entries = entries,
            UnmappedRigBones = [.. Enumerable.Range(0, skeleton.Count).Where(i => !used[i]).Select(i => skeleton.Names[i])],
            MissingRequired = missing,
            Notes = notes,
        };
        return new HumanoidMap(bones);
    }

    private static Skeleton Read(Skeleton3D skeleton3D)
    {
        var count = skeleton3D.GetBoneCount();
        var names = new string[count];
        var parents = new int[count];
        var positions = new Vector3[count];
        var children = new List<int>[count];
        for (var i = 0; i < count; i++)
        {
            names[i] = skeleton3D.GetBoneName(i);
            parents[i] = skeleton3D.GetBoneParent(i);
            positions[i] = skeleton3D.GetBoneGlobalRest(i).Origin;
            children[i] = [];
        }

        var depth = new int[count];
        for (var i = 0; i < count; i++)
        {
            if (parents[i] >= 0)
                children[parents[i]].Add(i);
            for (var p = parents[i]; p >= 0; p = parents[p])
                depth[i]++;
        }

        var subtree = new int[count];
        foreach (var i in Enumerable.Range(0, count).OrderByDescending(i => depth[i]))
        {
            subtree[i] += 1;
            if (parents[i] >= 0)
                subtree[parents[i]] += subtree[i];
        }

        return new Skeleton
        {
            Names = names,
            Parents = parents,
            Positions = positions,
            Children = children,
            Depth = depth,
            SubtreeSize = subtree,
        };
    }

    // ── Names ───────────────────────────────────────────────────────────────

    private static readonly string[] Prefixes = ["def-", "org-", "mch-", "j_bip_", "bip001", "bip01", "cc_base_", "armature_", "armature|"];

    private static readonly HashSet<string> Excluded =
    [
        "twist", "roll", "ik", "fk", "ctrl", "control", "pole", "target", "end", "nub", "tip", "helper", "corrective",
        "adjust", "mch", "jiggle", "bend", "bendy", "tweak", "top", "socket", "weapon", "prop", "attach", "offset", "null",
    ];

    private static readonly Dictionary<string, Kind> Bases = new(StringComparer.Ordinal)
    {
        ["hips"] = Kind.Hips,
        ["hip"] = Kind.Hips,
        ["pelvis"] = Kind.Hips,
        ["spine"] = Kind.SpineSeries,
        ["chest"] = Kind.Chest,
        ["upperchest"] = Kind.UpperChest,
        ["neck"] = Kind.Neck,
        ["head"] = Kind.Head,
        ["eye"] = Kind.Eye,
        ["jaw"] = Kind.Jaw,
        ["upleg"] = Kind.UpperLeg,
        ["upperleg"] = Kind.UpperLeg,
        ["thigh"] = Kind.UpperLeg,
        ["femur"] = Kind.UpperLeg,
        ["leg"] = Kind.LowerLeg,
        ["lowerleg"] = Kind.LowerLeg,
        ["loleg"] = Kind.LowerLeg,
        ["calf"] = Kind.LowerLeg,
        ["shin"] = Kind.LowerLeg,
        ["knee"] = Kind.LowerLeg,
        ["foot"] = Kind.Foot,
        ["ankle"] = Kind.Foot,
        ["toe"] = Kind.Toes,
        ["toes"] = Kind.Toes,
        ["toebase"] = Kind.Toes,
        ["ball"] = Kind.Toes,
        ["shoulder"] = Kind.Shoulder,
        ["clavicle"] = Kind.Shoulder,
        ["collar"] = Kind.Shoulder,
        ["collarbone"] = Kind.Shoulder,
        ["arm"] = Kind.UpperArm,
        ["upperarm"] = Kind.UpperArm,
        ["uparm"] = Kind.UpperArm,
        ["humerus"] = Kind.UpperArm,
        ["forearm"] = Kind.LowerArm,
        ["lowerarm"] = Kind.LowerArm,
        ["loarm"] = Kind.LowerArm,
        ["elbow"] = Kind.LowerArm,
        ["hand"] = Kind.Hand,
        ["wrist"] = Kind.Hand,
    };

    private static readonly string[] FingerWords = ["thumb", "index", "middle", "ring", "little", "pinky", "pinkie"];

    /// <summary>Splits a rig bone name into lower-case tokens (separators, case changes and digits split).</summary>
    internal static List<string> Tokenize(string name)
    {
        var colon = name.LastIndexOf(':');
        if (colon >= 0)
            name = name[(colon + 1)..];
        var lower = name.ToLowerInvariant();
        foreach (var prefix in Prefixes)
        {
            if (lower.StartsWith(prefix, StringComparison.Ordinal))
            {
                name = name[prefix.Length..];
                break;
            }
        }

        var tokens = new List<string>();
        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length > 0)
                tokens.Add(current.ToString().ToLowerInvariant());
            current.Clear();
        }

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (current.Length > 0)
            {
                var previous = current[^1];
                var caseBreak = char.IsLower(previous) && char.IsUpper(c);
                var digitBreak = char.IsDigit(previous) != char.IsDigit(c);
                if (caseBreak || digitBreak)
                    Flush();
            }

            current.Append(c);
        }

        Flush();
        return tokens;
    }

    private static Candidate Classify(string name)
    {
        var tokens = Tokenize(name);
        if (tokens.Count == 0 || tokens.Any(Excluded.Contains))
            return default;

        var side = HumanoidSide.Centre;
        bool TakeSide(int index)
        {
            var token = tokens[index];
            HumanoidSide? found = token switch
            {
                "l" or "left" or "lf" => HumanoidSide.Left,
                "r" or "right" or "rt" => HumanoidSide.Right,
                "c" or "center" or "centre" or "mid" => HumanoidSide.Centre,
                _ => null,
            };
            if (found is not { } value)
                return false;
            side = value;
            tokens.RemoveAt(index);
            return true;
        }

        if (!TakeSide(0) && tokens.Count > 1)
            TakeSide(tokens.Count - 1);
        if (tokens.Count > 1 && tokens[0] == "f")
            tokens.RemoveAt(0); // Rigify's f_index.01.L

        var number = -1;
        var words = new List<string>();
        foreach (var token in tokens)
        {
            if (char.IsDigit(token[0]))
                number = int.Parse(token, System.Globalization.CultureInfo.InvariantCulture);
            else
                words.Add(token);
        }

        var joined = string.Concat(words);
        if (joined.Length == 0)
            return default;

        var finger = FingerOf(joined, out var rest);
        if (finger >= 0)
        {
            var segment = rest switch
            {
                "" => Segment.Numbered,
                "metacarpal" or "meta" => Segment.Metacarpal,
                "proximal" or "prox" => Segment.Proximal,
                "intermediate" or "middle" or "medial" => Segment.Intermediate,
                "distal" or "dist" => Segment.Distal,
                _ => (Segment)255,
            };
            if ((byte)segment == 255 || (segment == Segment.Numbered && number < 0))
                return default;
            return new Candidate(Kind.Finger, side, number, finger, segment);
        }

        if (!Bases.TryGetValue(joined, out var kind))
            return default;
        return new Candidate(kind, side, Math.Max(number, 0), -1, Segment.Numbered);
    }

    // "handindex" (Mixamo), "indexproximal" (VRM), "index" (Unreal, numbered), "indexmetacarpal" (UE5).
    private static int FingerOf(string joined, out string rest)
    {
        var text = joined.StartsWith("hand", StringComparison.Ordinal) && joined.Length > 4 ? joined[4..] : joined;
        for (var i = 0; i < FingerWords.Length; i++)
        {
            if (!text.StartsWith(FingerWords[i], StringComparison.Ordinal))
                continue;
            rest = text[FingerWords[i].Length..];
            return Math.Min(i, 4);
        }

        rest = "";
        return -1;
    }

    private static void MapByName(Skeleton skeleton, int[] structure, int[] map, HumanoidMapSource[] sources, List<string> notes)
    {
        var candidates = new Candidate[skeleton.Count];
        for (var i = 0; i < skeleton.Count; i++)
            candidates[i] = Classify(skeleton.Names[i]);

        // Bones named without a side (a lone "Thigh") take the side of their position.
        var hips = structure[(int)HumanoidBone.Hips];
        var lateral = LateralAxis(skeleton, structure);
        for (var i = 0; i < skeleton.Count; i++)
        {
            var c = candidates[i];
            if (c.Kind is Kind.None or Kind.Hips or Kind.SpineSeries or Kind.Chest or Kind.UpperChest or Kind.Neck or Kind.Head
                or Kind.Jaw || c.Side != HumanoidSide.Centre)
                continue;
            if (hips < 0 || lateral == Vector3.Zero)
            {
                candidates[i] = default;
                continue;
            }

            var x = Vector3.Dot(skeleton.Positions[i] - skeleton.Positions[hips], lateral);
            candidates[i] = c with { Side = x >= 0 ? HumanoidSide.Left : HumanoidSide.Right };
        }

        void Assign(HumanoidBone bone, int rigBone)
        {
            if (map[(int)bone] >= 0 && map[(int)bone] != rigBone)
            {
                var keep = skeleton.Depth[map[(int)bone]] <= skeleton.Depth[rigBone] ? map[(int)bone] : rigBone;
                notes.Add($"{bone}: both '{skeleton.Names[map[(int)bone]]}' and '{skeleton.Names[rigBone]}' match by name; using '{skeleton.Names[keep]}'.");
                rigBone = keep;
            }

            map[(int)bone] = rigBone;
            sources[(int)bone] = HumanoidMapSource.Name;
        }

        var spineSeries = new List<int>();
        var necks = new List<int>();
        var fingers = new Dictionary<(HumanoidSide Side, int Finger), List<(int Bone, int Number, Segment Segment)>>();
        for (var i = 0; i < skeleton.Count; i++)
        {
            var c = candidates[i];
            switch (c.Kind)
            {
                case Kind.None:
                    break;
                case Kind.SpineSeries:
                    if (c.Side == HumanoidSide.Centre)
                        spineSeries.Add(i);
                    break;
                case Kind.Neck:
                    necks.Add(i);
                    break;
                case Kind.Finger:
                    if (!fingers.TryGetValue((c.Side, c.Finger), out var list))
                        fingers[(c.Side, c.Finger)] = list = [];
                    list.Add((i, c.Number, c.Segment));
                    break;
                default:
                    if (BoneOf(c.Kind, c.Side) is { } bone)
                        Assign(bone, i);
                    break;
            }
        }

        // Spine, Spine1, Spine2 (Mixamo), spine_01…05 (Unreal): ordered by depth, the first is Spine, the last UpperChest,
        // the middle one Chest; explicitly named Chest/UpperChest bones win.
        spineSeries.Sort((a, b) => skeleton.Depth[a].CompareTo(skeleton.Depth[b]));
        if (spineSeries.Count > 0)
        {
            Assign(HumanoidBone.Spine, spineSeries[0]);
            if (spineSeries.Count == 2 && map[(int)HumanoidBone.Chest] < 0)
                Assign(HumanoidBone.Chest, spineSeries[1]);
            if (spineSeries.Count >= 3)
            {
                if (map[(int)HumanoidBone.Chest] < 0)
                    Assign(HumanoidBone.Chest, spineSeries[spineSeries.Count / 2]);
                if (map[(int)HumanoidBone.UpperChest] < 0)
                    Assign(HumanoidBone.UpperChest, spineSeries[^1]);
            }
        }

        if (necks.Count > 0)
            Assign(HumanoidBone.Neck, necks.MinBy(n => skeleton.Depth[n]));

        foreach (var ((side, finger), list) in fingers)
        {
            if (side == HumanoidSide.Centre)
                continue;
            var named = list.Where(f => f.Segment != Segment.Numbered).ToList();
            var numbered = list.Where(f => f.Segment == Segment.Numbered).OrderBy(f => f.Number).ToList();
            foreach (var (bone, _, segment) in named)
                if (FingerBone(side, finger, segment) is { } canonical)
                    Assign(canonical, bone);
            // Numbered segments by rank: thumbs are metacarpal, proximal, distal; fingers proximal, intermediate, distal.
            Segment[] order = finger == 0
                ? [Segment.Metacarpal, Segment.Proximal, Segment.Distal]
                : [Segment.Proximal, Segment.Intermediate, Segment.Distal];
            for (var rank = 0; rank < numbered.Count && rank < order.Length; rank++)
                if (FingerBone(side, finger, order[rank]) is { } canonical && map[(int)canonical] < 0)
                    Assign(canonical, numbered[rank].Bone);
        }
    }

    private static HumanoidBone? BoneOf(Kind kind, HumanoidSide side)
    {
        HumanoidBone? left = kind switch
        {
            Kind.Hips => HumanoidBone.Hips,
            Kind.Chest => HumanoidBone.Chest,
            Kind.UpperChest => HumanoidBone.UpperChest,
            Kind.Head => HumanoidBone.Head,
            Kind.Jaw => HumanoidBone.Jaw,
            Kind.Eye => HumanoidBone.LeftEye,
            Kind.UpperLeg => HumanoidBone.LeftUpperLeg,
            Kind.LowerLeg => HumanoidBone.LeftLowerLeg,
            Kind.Foot => HumanoidBone.LeftFoot,
            Kind.Toes => HumanoidBone.LeftToes,
            Kind.Shoulder => HumanoidBone.LeftShoulder,
            Kind.UpperArm => HumanoidBone.LeftUpperArm,
            Kind.LowerArm => HumanoidBone.LeftLowerArm,
            Kind.Hand => HumanoidBone.LeftHand,
            _ => null,
        };
        if (left is not { } bone)
            return null;
        if (HumanoidBones.GetSide(bone) == HumanoidSide.Centre)
            return side == HumanoidSide.Centre ? bone : null; // "hip.L" is not the hips
        return side switch
        {
            HumanoidSide.Left => bone,
            HumanoidSide.Right => HumanoidBones.Mirror(bone),
            _ => null,
        };
    }

    private static HumanoidBone? FingerBone(HumanoidSide side, int finger, Segment segment)
    {
        HumanoidBone? left = (finger, segment) switch
        {
            (0, Segment.Metacarpal) => HumanoidBone.LeftThumbMetacarpal,
            (0, Segment.Proximal) => HumanoidBone.LeftThumbProximal,
            (0, Segment.Distal) => HumanoidBone.LeftThumbDistal,
            (0, Segment.Intermediate) => HumanoidBone.LeftThumbProximal, // a thumb "intermediate" is VRM 0's proximal
            (> 0, Segment.Proximal) => (HumanoidBone)((int)HumanoidBone.LeftIndexProximal + (finger - 1) * 3),
            (> 0, Segment.Intermediate) => (HumanoidBone)((int)HumanoidBone.LeftIndexIntermediate + (finger - 1) * 3),
            (> 0, Segment.Distal) => (HumanoidBone)((int)HumanoidBone.LeftIndexDistal + (finger - 1) * 3),
            _ => null,
        };
        return left is { } bone ? side == HumanoidSide.Right ? HumanoidBones.Mirror(bone) : bone : null;
    }

    // ── Structure ───────────────────────────────────────────────────────────

    // Rig bone index per canonical bone, from the skeleton's shape alone (−1 where it cannot tell).
    private static int[] MapByStructure(Skeleton skeleton)
    {
        var map = new int[HumanoidBones.Count];
        Array.Fill(map, -1);
        var hips = FindHips(skeleton, out var legs, out var spineChild);
        if (hips < 0)
            return map;
        map[(int)HumanoidBone.Hips] = hips;

        var legEnds = legs.Select(l => skeleton.Positions[Chain(skeleton, l)[^1]]).ToArray();
        var up = Vector3.Normalize(skeleton.Positions[hips] - (legEnds[0] + legEnds[1]) * 0.5f);

        // Sides: forward from the feet's toes (or +Z), left = up × forward.
        var lateral = Side(skeleton, up, legs);
        foreach (var leg in legs)
        {
            var chain = Chain(skeleton, leg);
            var left = Vector3.Dot(skeleton.Positions[leg] - skeleton.Positions[hips], lateral) >= 0;
            HumanoidBone[] bones = left
                ? [HumanoidBone.LeftUpperLeg, HumanoidBone.LeftLowerLeg, HumanoidBone.LeftFoot, HumanoidBone.LeftToes]
                : [HumanoidBone.RightUpperLeg, HumanoidBone.RightLowerLeg, HumanoidBone.RightFoot, HumanoidBone.RightToes];
            for (var i = 0; i < bones.Length && i < chain.Count; i++)
                map[(int)bones[i]] = chain[i];
        }

        // Spine: follow the largest subtree to the bone where the arms branch off.
        var spine = new List<int>();
        var at = spineChild;
        var armBranch = -1;
        int[] arms = [];
        while (at >= 0)
        {
            spine.Add(at);
            if (FindPair(skeleton, at, up, out var pair, out var rest))
            {
                armBranch = at;
                arms = pair;
                at = rest;
                break;
            }

            at = LargestChild(skeleton, at);
        }

        if (armBranch < 0)
            return map;
        map[(int)HumanoidBone.Spine] = spine[0];
        if (spine.Count == 2)
            map[(int)HumanoidBone.Chest] = spine[1];
        if (spine.Count >= 3)
        {
            map[(int)HumanoidBone.Chest] = spine[spine.Count / 2];
            map[(int)HumanoidBone.UpperChest] = spine[^1];
        }

        // Neck and head: the chain above the arm branch; a trailing leaf is the head's end.
        if (at >= 0)
        {
            var neckChain = Chain(skeleton, at);
            if (neckChain.Count >= 3 && skeleton.Children[neckChain[^1]].Count == 0)
                neckChain.RemoveAt(neckChain.Count - 1);
            map[(int)HumanoidBone.Head] = neckChain[^1];
            if (neckChain.Count >= 2)
                map[(int)HumanoidBone.Neck] = neckChain[0];
        }

        foreach (var arm in arms)
        {
            var chain = Chain(skeleton, arm, stopAtBranch: true);
            var left = Vector3.Dot(skeleton.Positions[chain[^1]] - skeleton.Positions[hips], lateral) >= 0;
            HumanoidBone[] bones = left
                ? [HumanoidBone.LeftShoulder, HumanoidBone.LeftUpperArm, HumanoidBone.LeftLowerArm, HumanoidBone.LeftHand]
                : [HumanoidBone.RightShoulder, HumanoidBone.RightUpperArm, HumanoidBone.RightLowerArm, HumanoidBone.RightHand];
            // The chain ends at the hand; count back from it (no shoulder when the chain has three bones).
            for (var i = 0; i < 4; i++)
            {
                var index = chain.Count - 4 + i;
                if (index >= 0)
                    map[(int)bones[i]] = chain[index];
            }
        }

        return map;
    }

    // The hips: the shallowest bone with two similar child branches going down (legs) and a branch going up (spine).
    private static int FindHips(Skeleton skeleton, out int[] legs, out int spineChild)
    {
        foreach (var bone in Enumerable.Range(0, skeleton.Count).OrderBy(b => skeleton.Depth[b]))
        {
            var children = skeleton.Children[bone];
            if (children.Count < 3)
                continue;
            for (var a = 0; a < children.Count; a++)
            {
                for (var b = a + 1; b < children.Count; b++)
                {
                    var ca = children[a];
                    var cb = children[b];
                    if (skeleton.SubtreeSize[ca] != skeleton.SubtreeSize[cb] || skeleton.SubtreeSize[ca] < 3)
                        continue;
                    var origin = skeleton.Positions[bone];
                    var endA = skeleton.Positions[Chain(skeleton, ca)[^1]];
                    var endB = skeleton.Positions[Chain(skeleton, cb)[^1]];
                    var legLength = Vector3.Distance((endA + endB) * 0.5f, origin);
                    if (legLength < 1e-6f)
                        continue;
                    var down = ((endA + endB) * 0.5f - origin) / legLength;
                    var rest = children.Where(c => c != ca && c != cb).MaxBy(c => skeleton.SubtreeSize[c]);
                    if (MaxAlong(skeleton, rest, origin, -down) < 0.25f * legLength)
                        continue; // the third branch must rise well above the hips
                    if (skeleton.SubtreeSize[rest] <= skeleton.SubtreeSize[ca])
                        continue; // the spine carries the arms and head
                    legs = [ca, cb];
                    spineChild = rest;
                    return bone;
                }
            }
        }

        legs = [];
        spineChild = -1;
        return -1;
    }

    // At `bone`, two similar child branches going sideways (arms) and one going up (the neck).
    private static bool FindPair(Skeleton skeleton, int bone, Vector3 up, out int[] pair, out int rest)
    {
        var children = skeleton.Children[bone];
        pair = [];
        rest = -1;
        if (children.Count < 3)
            return false;
        for (var a = 0; a < children.Count; a++)
        {
            for (var b = a + 1; b < children.Count; b++)
            {
                var ca = children[a];
                var cb = children[b];
                if (skeleton.SubtreeSize[ca] != skeleton.SubtreeSize[cb] || skeleton.SubtreeSize[ca] < 3)
                    continue;
                // Arms point apart across the body (measured across the up axis, so an A-pose counts too).
                var origin = skeleton.Positions[bone];
                var endA = skeleton.Positions[Chain(skeleton, ca)[^1]] - origin;
                var endB = skeleton.Positions[Chain(skeleton, cb)[^1]] - origin;
                endA -= up * Vector3.Dot(endA, up);
                endB -= up * Vector3.Dot(endB, up);
                if (endA.LengthSquared() < 1e-8f || endB.LengthSquared() < 1e-8f ||
                    Vector3.Dot(Vector3.Normalize(endA), Vector3.Normalize(endB)) > -0.5f)
                    continue;
                var others = children.Where(c => c != ca && c != cb).ToList();
                if (others.Count == 0)
                    continue;
                var neck = others.MaxBy(c => MaxAlong(skeleton, c, origin, up));
                if (MaxAlong(skeleton, neck, origin, up) <= 0f)
                    continue;
                pair = [ca, cb];
                rest = neck;
                return true;
            }
        }

        return false;
    }

    private static int LargestChild(Skeleton skeleton, int bone) =>
        skeleton.Children[bone].Count == 0 ? -1 : skeleton.Children[bone].MaxBy(c => skeleton.SubtreeSize[c]);

    // From `bone`, follow the largest child; with stopAtBranch, stop at the first bone with two or more similar child
    // branches (a hand's fingers; a twist bone beside the forearm is not similar).
    private static List<int> Chain(Skeleton skeleton, int bone, bool stopAtBranch = false)
    {
        var chain = new List<int>();
        for (var at = bone; at >= 0; at = LargestChild(skeleton, at))
        {
            chain.Add(at);
            if (stopAtBranch && IsBranch(skeleton, at))
                break;
        }

        return chain;
    }

    private static bool IsBranch(Skeleton skeleton, int bone)
    {
        var children = skeleton.Children[bone];
        if (children.Count < 2)
            return false;
        var sizes = children.Select(c => skeleton.SubtreeSize[c]).OrderDescending().ToArray();
        return sizes[1] * 2 >= sizes[0];
    }

    // The furthest any bone of `root`'s subtree reaches from `origin` along `direction`.
    private static float MaxAlong(Skeleton skeleton, int root, Vector3 origin, Vector3 direction)
    {
        var best = float.NegativeInfinity;
        var stack = new Stack<int>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var bone = stack.Pop();
            best = MathF.Max(best, Vector3.Dot(skeleton.Positions[bone] - origin, direction));
            foreach (var child in skeleton.Children[bone])
                stack.Push(child);
        }

        return best;
    }

    // The character's left: up × forward, forward from the feet towards the toes (or +Z when the legs have no toes).
    private static Vector3 Side(Skeleton skeleton, Vector3 up, int[] legs)
    {
        var forward = Vector3.Zero;
        foreach (var leg in legs)
        {
            var chain = Chain(skeleton, leg);
            if (chain.Count >= 4)
                forward += skeleton.Positions[chain[3]] - skeleton.Positions[chain[2]];
        }

        forward -= up * Vector3.Dot(forward, up);
        if (forward.LengthSquared() < 1e-8f)
        {
            var lateral = skeleton.Positions[legs[0]] - skeleton.Positions[legs[1]];
            forward = Vector3.Cross(lateral, up);
            if (forward.Z < 0)
                forward = -forward;
        }

        return Vector3.Normalize(Vector3.Cross(up, Vector3.Normalize(forward)));
    }

    private static Vector3 LateralAxis(Skeleton skeleton, int[] structure)
    {
        var hips = structure[(int)HumanoidBone.Hips];
        var head = structure[(int)HumanoidBone.Head];
        var left = structure[(int)HumanoidBone.LeftUpperLeg];
        var right = structure[(int)HumanoidBone.RightUpperLeg];
        if (hips < 0 || left < 0 || right < 0)
            return Vector3.Zero;
        var lateral = skeleton.Positions[left] - skeleton.Positions[right];
        if (head >= 0)
        {
            var up = Vector3.Normalize(skeleton.Positions[head] - skeleton.Positions[hips]);
            lateral -= up * Vector3.Dot(lateral, up);
        }

        return lateral.LengthSquared() < 1e-10f ? Vector3.Zero : Vector3.Normalize(lateral);
    }

    // ── Checks and overrides ────────────────────────────────────────────────

    private static void CheckSides(Skeleton skeleton, int[] map, List<string> notes)
    {
        var hips = map[(int)HumanoidBone.Hips];
        var left = map[(int)HumanoidBone.LeftUpperLeg];
        var right = map[(int)HumanoidBone.RightUpperLeg];
        var leftArm = map[(int)HumanoidBone.LeftUpperArm];
        var rightArm = map[(int)HumanoidBone.RightUpperArm];
        if (hips < 0 || left < 0 || right < 0 || leftArm < 0 || rightArm < 0)
            return;
        var legs = skeleton.Positions[left] - skeleton.Positions[right];
        var arms = skeleton.Positions[leftArm] - skeleton.Positions[rightArm];
        if (Vector3.Dot(legs, arms) < 0f)
            notes.Add("The left leg and the left arm are on opposite sides; check the map's sides.");
    }

    private static void ApplyOverrides(Skeleton skeleton, IReadOnlyDictionary<string, string>? overrides, int[] map,
        HumanoidMapSource[] sources, List<string> notes)
    {
        if (overrides is null)
            return;
        foreach (var (canonicalName, rigName) in overrides)
        {
            if (!HumanoidBones.TryParse(canonicalName, out var bone))
            {
                var suggestion = HumanoidBones.Suggest(canonicalName);
                notes.Add(suggestion is { } s
                    ? $"Override '{canonicalName}' is not a humanoid bone (did you mean {s}?); ignored."
                    : $"Override '{canonicalName}' is not a humanoid bone; ignored.");
                continue;
            }

            if (string.IsNullOrEmpty(rigName))
            {
                map[(int)bone] = -1; // an empty override unmaps the bone
                continue;
            }

            var index = Array.IndexOf(skeleton.Names, rigName);
            if (index < 0)
            {
                notes.Add($"Override {bone}: the skeleton has no bone '{rigName}'; ignored.");
                continue;
            }

            for (var other = 0; other < map.Length; other++)
                if (map[other] == index && other != (int)bone)
                    map[other] = -1;
            map[(int)bone] = index;
            sources[(int)bone] = HumanoidMapSource.Override;
        }
    }
}
