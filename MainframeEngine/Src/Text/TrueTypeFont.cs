using System.Buffers.Binary;
using System.Numerics;

namespace MainframeEngine;

/// <summary>One closed contour of a glyph: points in font units (Y up), each on or off the curve (quadratic B-splines).</summary>
internal readonly record struct GlyphContour(Vector2[] Points, bool[] OnCurve);

/// <summary>
/// A TrueType (<c>glyf</c>) font file read in managed code (ADR 0118): character map (formats 4 and 12), horizontal metrics,
/// simple and composite outlines, and pair kerning from GPOS (<c>kern</c> feature, PairPos formats 1 and 2, extension
/// lookups) or the legacy <c>kern</c> table. CFF/OpenType-PostScript outlines, hinting and shaping beyond kerning are
/// not supported.
/// </summary>
internal sealed class TrueTypeFont
{
    private readonly byte[] _data;
    private readonly Dictionary<uint, (int Offset, int Length)> _tables = [];
    private readonly int _numGlyphs, _numHMetrics, _hmtx, _loca, _glyf, _cmap;
    private readonly bool _longLoca;
    private readonly List<int> _kernSubtables = []; // GPOS PairPos subtable offsets of the 'kern' feature
    private readonly int _legacyKern = -1;
    private readonly Dictionary<(int, int), float> _kernCache = [];

    public TrueTypeFont(byte[] data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        if (data.Length < 12)
            throw new InvalidDataException("Not a TrueType font (too short).");
        var version = U32(0);
        if (version != 0x00010000 && version != 0x74727565) // 1.0 or 'true'
            throw new InvalidDataException(version == 0x4F54544F ? "CFF (OpenType PostScript) fonts are not supported; use a TrueType (.ttf) font." : "Not a TrueType font.");
        var count = U16(4);
        for (var i = 0; i < count; i++)
        {
            var record = 12 + i * 16;
            _tables[U32(record)] = ((int)U32(record + 8), (int)U32(record + 12));
        }

        var head = Table("head");
        UnitsPerEm = U16(head + 18);
        _longLoca = I16(head + 50) != 0;
        var hhea = Table("hhea");
        Ascender = I16(hhea + 4);
        Descender = I16(hhea + 6);
        LineGap = I16(hhea + 8);
        _numHMetrics = U16(hhea + 34);
        _numGlyphs = U16(Table("maxp") + 4);
        _hmtx = Table("hmtx");
        _loca = Table("loca");
        _glyf = Table("glyf");
        _cmap = FindCmap();
        if (Ascender == 0 && Descender == 0 && _tables.TryGetValue(Tag("OS/2"), out var os2))
        {
            Ascender = I16(os2.Offset + 68);
            Descender = I16(os2.Offset + 70);
            LineGap = I16(os2.Offset + 72);
        }

        if (_tables.TryGetValue(Tag("GPOS"), out var gpos))
            FindKernLookups(gpos.Offset);
        else if (_tables.TryGetValue(Tag("kern"), out var kern))
            _legacyKern = kern.Offset;
    }

    public int UnitsPerEm { get; }
    public int Ascender { get; }
    public int Descender { get; }
    public int LineGap { get; }
    public int GlyphCount => _numGlyphs;

    private static uint Tag(string tag) => (uint)(tag[0] << 24 | tag[1] << 16 | tag[2] << 8 | tag[3]);

    private int Table(string tag) =>
        _tables.TryGetValue(Tag(tag), out var t) ? t.Offset : throw new InvalidDataException($"The font has no '{tag}' table" + (tag == "glyf" ? " (CFF fonts are not supported)." : "."));

    private ushort U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at, 2));
    private short I16(int at) => BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(at, 2));
    private uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at, 4));

    // ── Character map ──

    private int FindCmap()
    {
        var cmap = Table("cmap");
        int best = -1, bestScore = -1;
        for (int i = 0, n = U16(cmap + 2); i < n; i++)
        {
            var record = cmap + 4 + i * 8;
            int platform = U16(record), encoding = U16(record + 2);
            var offset = cmap + (int)U32(record + 4);
            var format = U16(offset);
            var score = (platform, encoding, format) switch
            {
                (3, 10, 12) or (0, 4, 12) or (0, 6, 12) => 3,
                (3, 1, 4) or (0, 3, 4) => 2,
                (0, _, 4) => 1,
                _ => -1,
            };
            if (score > bestScore)
                (best, bestScore) = (offset, score);
        }

        return best;
    }

    /// <summary>The glyph index of <paramref name="codepoint"/> (0, the .notdef glyph, when missing).</summary>
    public int GlyphIndex(int codepoint)
    {
        if (_cmap < 0)
            return 0;
        if (U16(_cmap) == 12)
        {
            int low = 0, high = (int)U32(_cmap + 12) - 1;
            while (low <= high)
            {
                var mid = (low + high) / 2;
                var group = _cmap + 16 + mid * 12;
                if (codepoint < U32(group))
                    high = mid - 1;
                else if (codepoint > U32(group + 4))
                    low = mid + 1;
                else
                    return (int)(U32(group + 8) + (uint)(codepoint - (int)U32(group)));
            }

            return 0;
        }

        if (codepoint > 0xFFFF)
            return 0;
        var segX2 = U16(_cmap + 6);
        var ends = _cmap + 14;
        var starts = ends + segX2 + 2;
        var deltas = starts + segX2;
        var ranges = deltas + segX2;
        for (var s = 0; s < segX2; s += 2)
        {
            if (codepoint > U16(ends + s))
                continue;
            var start = U16(starts + s);
            if (codepoint < start)
                return 0;
            var delta = I16(deltas + s);
            var rangeOffset = U16(ranges + s);
            if (rangeOffset == 0)
                return (codepoint + delta) & 0xFFFF;
            var glyph = U16(ranges + s + rangeOffset + (codepoint - start) * 2);
            return glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
        }

        return 0;
    }

    // ── Metrics and outlines ──

    /// <summary>The glyph's advance width in font units.</summary>
    public int AdvanceWidth(int glyph) => U16(_hmtx + 4 * Math.Min(glyph, _numHMetrics - 1));

    private (int Offset, int Length) GlyphData(int glyph)
    {
        if (glyph < 0 || glyph >= _numGlyphs)
            return (0, 0);
        int start, end;
        if (_longLoca)
        {
            start = (int)U32(_loca + glyph * 4);
            end = (int)U32(_loca + glyph * 4 + 4);
        }
        else
        {
            start = U16(_loca + glyph * 2) * 2;
            end = U16(_loca + glyph * 2 + 2) * 2;
        }

        return (_glyf + start, end - start);
    }

    /// <summary>The glyph's contours (composite glyphs resolved), in font units; empty for blank glyphs.</summary>
    public List<GlyphContour> Outline(int glyph)
    {
        var contours = new List<GlyphContour>();
        AppendOutline(glyph, Matrix3x2.Identity, contours, 0);
        return contours;
    }

    private void AppendOutline(int glyph, Matrix3x2 transform, List<GlyphContour> contours, int depth)
    {
        var (at, length) = GlyphData(glyph);
        if (length <= 0 || depth > 8)
            return;
        var contourCount = I16(at);
        if (contourCount >= 0)
        {
            AppendSimple(at, contourCount, transform, contours);
            return;
        }

        // Composite: components with an offset (or anchor points, unsupported → ignored) and an optional 2×2 transform.
        var p = at + 10;
        const int Arg1And2AreWords = 0x1, ArgsAreXyValues = 0x2, WeHaveAScale = 0x8, MoreComponents = 0x20,
            WeHaveAnXAndYScale = 0x40, WeHaveATwoByTwo = 0x80;
        int flags;
        do
        {
            flags = U16(p);
            var component = U16(p + 2);
            p += 4;
            float dx, dy;
            if ((flags & Arg1And2AreWords) != 0)
            {
                (dx, dy) = (I16(p), I16(p + 2));
                p += 4;
            }
            else
            {
                (dx, dy) = ((sbyte)_data[p], (sbyte)_data[p + 1]);
                p += 2;
            }

            if ((flags & ArgsAreXyValues) == 0)
                (dx, dy) = (0, 0);
            float a = 1, b = 0, c = 0, d = 1;
            if ((flags & WeHaveAScale) != 0)
            {
                a = d = F2Dot14(p);
                p += 2;
            }
            else if ((flags & WeHaveAnXAndYScale) != 0)
            {
                a = F2Dot14(p);
                d = F2Dot14(p + 2);
                p += 4;
            }
            else if ((flags & WeHaveATwoByTwo) != 0)
            {
                a = F2Dot14(p);
                b = F2Dot14(p + 2);
                c = F2Dot14(p + 4);
                d = F2Dot14(p + 6);
                p += 8;
            }

            var local = new Matrix3x2(a, b, c, d, dx, dy);
            AppendOutline(component, local * transform, contours, depth + 1);
        }
        while ((flags & MoreComponents) != 0);
    }

    private float F2Dot14(int at) => I16(at) / 16384f;

    private void AppendSimple(int at, int contourCount, Matrix3x2 transform, List<GlyphContour> contours)
    {
        if (contourCount == 0)
            return;
        var endPts = new int[contourCount];
        for (var i = 0; i < contourCount; i++)
            endPts[i] = U16(at + 10 + i * 2);
        var pointCount = endPts[^1] + 1;
        var p = at + 10 + contourCount * 2;
        p += 2 + U16(p); // instructions
        var flags = new byte[pointCount];
        for (var i = 0; i < pointCount;)
        {
            var f = _data[p++];
            flags[i++] = f;
            if ((f & 0x08) != 0)
                for (int r = _data[p++]; r > 0 && i < pointCount; r--)
                    flags[i++] = f;
        }

        var xs = new int[pointCount];
        var ys = new int[pointCount];
        for (int i = 0, x = 0; i < pointCount; i++)
        {
            var f = flags[i];
            if ((f & 0x02) != 0)
                x += (f & 0x10) != 0 ? _data[p++] : -_data[p++];
            else if ((f & 0x10) == 0)
            {
                x += I16(p);
                p += 2;
            }

            xs[i] = x;
        }

        for (int i = 0, y = 0; i < pointCount; i++)
        {
            var f = flags[i];
            if ((f & 0x04) != 0)
                y += (f & 0x20) != 0 ? _data[p++] : -_data[p++];
            else if ((f & 0x20) == 0)
            {
                y += I16(p);
                p += 2;
            }

            ys[i] = y;
        }

        var start = 0;
        foreach (var end in endPts)
        {
            var n = end - start + 1;
            if (n > 0)
            {
                var points = new Vector2[n];
                var on = new bool[n];
                for (var i = 0; i < n; i++)
                {
                    points[i] = Vector2.Transform(new Vector2(xs[start + i], ys[start + i]), transform);
                    on[i] = (flags[start + i] & 0x01) != 0;
                }

                contours.Add(new GlyphContour(points, on));
            }

            start = end + 1;
        }
    }

    // ── Kerning ──

    /// <summary>The kerning between two glyphs in font units (GPOS pair adjustment of the first glyph's advance).</summary>
    public float Kerning(int left, int right)
    {
        if (_kernSubtables.Count == 0 && _legacyKern < 0)
            return 0;
        if (_kernCache.TryGetValue((left, right), out var cached))
            return cached;
        float value = 0;
        foreach (var subtable in _kernSubtables)
            if (PairAdjustment(subtable, left, right) is { } v)
            {
                value = v;
                break;
            }

        if (_kernSubtables.Count == 0)
            value = LegacyKerning(left, right);
        return _kernCache[(left, right)] = value;
    }

    private void FindKernLookups(int gpos)
    {
        var featureList = gpos + U16(gpos + 6);
        var lookupList = gpos + U16(gpos + 8);
        var lookups = new SortedSet<int>();
        for (int i = 0, n = U16(featureList); i < n; i++)
        {
            var record = featureList + 2 + i * 6;
            if (U32(record) != Tag("kern"))
                continue;
            var feature = featureList + U16(record + 4);
            for (int l = 0, count = U16(feature + 2); l < count; l++)
                lookups.Add(U16(feature + 4 + l * 2));
        }

        foreach (var index in lookups)
        {
            var lookup = lookupList + U16(lookupList + 2 + index * 2);
            var type = U16(lookup);
            for (int s = 0, count = U16(lookup + 4); s < count; s++)
            {
                var subtable = lookup + U16(lookup + 6 + s * 2);
                var subtype = type;
                if (type == 9)
                {
                    subtype = U16(subtable + 2);
                    subtable += (int)U32(subtable + 4);
                }

                if (subtype == 2)
                    _kernSubtables.Add(subtable);
            }
        }
    }

    private static int ValueRecordSize(int format) => BitOperations.PopCount((uint)format) * 2;

    private float XAdvance(int record, int format) =>
        (format & 0x4) == 0 ? 0 : I16(record + BitOperations.PopCount((uint)(format & 0x3)) * 2);

    private float? PairAdjustment(int subtable, int left, int right)
    {
        var format = U16(subtable);
        var coverageIndex = Coverage(subtable + U16(subtable + 2), left);
        if (coverageIndex < 0)
            return null;
        int vf1 = U16(subtable + 4), vf2 = U16(subtable + 6);
        var recordSize = 2 + ValueRecordSize(vf1) + ValueRecordSize(vf2);
        if (format == 1)
        {
            var pairSet = subtable + U16(subtable + 10 + coverageIndex * 2);
            int low = 0, high = U16(pairSet) - 1;
            while (low <= high)
            {
                var mid = (low + high) / 2;
                var record = pairSet + 2 + mid * recordSize;
                var second = U16(record);
                if (right < second)
                    high = mid - 1;
                else if (right > second)
                    low = mid + 1;
                else
                    return XAdvance(record + 2, vf1);
            }

            return null;
        }

        if (format == 2)
        {
            var class1 = ClassOf(subtable + U16(subtable + 8), left);
            var class2 = ClassOf(subtable + U16(subtable + 10), right);
            int class1Count = U16(subtable + 12), class2Count = U16(subtable + 14);
            if (class1 >= class1Count || class2 >= class2Count)
                return null;
            var size = ValueRecordSize(vf1) + ValueRecordSize(vf2);
            var record = subtable + 16 + (class1 * class2Count + class2) * size;
            return XAdvance(record, vf1);
        }

        return null;
    }

    private int Coverage(int table, int glyph)
    {
        var format = U16(table);
        var count = U16(table + 2);
        int low = 0, high = count - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (format == 1)
            {
                var g = U16(table + 4 + mid * 2);
                if (glyph < g)
                    high = mid - 1;
                else if (glyph > g)
                    low = mid + 1;
                else
                    return mid;
            }
            else
            {
                var range = table + 4 + mid * 6;
                if (glyph < U16(range))
                    high = mid - 1;
                else if (glyph > U16(range + 2))
                    low = mid + 1;
                else
                    return U16(range + 4) + glyph - U16(range);
            }
        }

        return -1;
    }

    private int ClassOf(int table, int glyph)
    {
        var format = U16(table);
        if (format == 1)
        {
            var start = U16(table + 2);
            var count = U16(table + 4);
            return glyph >= start && glyph < start + count ? U16(table + 6 + (glyph - start) * 2) : 0;
        }

        int low = 0, high = U16(table + 2) - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var range = table + 4 + mid * 6;
            if (glyph < U16(range))
                high = mid - 1;
            else if (glyph > U16(range + 2))
                low = mid + 1;
            else
                return U16(range + 4);
        }

        return 0;
    }

    private float LegacyKerning(int left, int right)
    {
        // kern version 0, first subtable format 0 (horizontal).
        var at = _legacyKern;
        if (U16(at) != 0 || U16(at + 2) == 0)
            return 0;
        var sub = at + 4;
        if ((U16(sub + 4) >> 8) != 0)
            return 0;
        var pairs = U16(sub + 6);
        var key = (uint)(left << 16 | right);
        int low = 0, high = pairs - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var record = sub + 14 + mid * 6;
            var k = U32(record);
            if (key < k)
                high = mid - 1;
            else if (key > k)
                low = mid + 1;
            else
                return I16(record + 4);
        }

        return 0;
    }
}
