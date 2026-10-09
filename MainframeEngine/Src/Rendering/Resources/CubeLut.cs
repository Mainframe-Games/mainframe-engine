using System.Globalization;
using System.Numerics;
using System.Text;

namespace MainframeEngine;

/// <summary>
/// A colour lookup table in the Adobe/Resolve <c>.cube</c> format (ADR 0168): a 3D table of <c>LUT_3D_SIZE</c>³ RGB
/// entries (red changing fastest, then green, then blue) or a 1D table of <c>LUT_1D_SIZE</c> per-channel curves, over an
/// input domain (<c>DOMAIN_MIN</c>/<c>DOMAIN_MAX</c>, default 0–1). <see cref="ToTexture3D"/> turns it into the
/// <see cref="Texture3D"/> the colour grade samples (<see cref="PostProcessSettings.AdjustmentColorCorrection"/>);
/// <see cref="CubeLutImporter"/> does that for <c>.cube</c> files loaded through <see cref="ResourceLoader"/>.
/// </summary>
/// <remarks>
/// Parsing follows Resolve's rules: <c>#</c> comments and blank lines are skipped, <c>TITLE "…"</c> is optional, keyword
/// lines come before the data, unknown keywords (<c>LUT_IN_VIDEO_RANGE</c>, …) are ignored, and the table must have
/// exactly size³ (or size) rows of three numbers. <c>LUT_3D_INPUT_RANGE</c>/<c>LUT_1D_INPUT_RANGE min max</c> set the
/// same domain on every channel.
/// </remarks>
public sealed class CubeLut
{
    /// <summary>The largest <c>LUT_3D_SIZE</c> accepted (Resolve's limit).</summary>
    public const int MaxSize3D = 256;

    /// <summary>The largest <c>LUT_1D_SIZE</c> accepted.</summary>
    public const int MaxSize1D = 65536;

    /// <summary>The 3D size a 1D table (or a resampled 3D one) becomes in <see cref="ToTexture3D"/>.</summary>
    public const int DefaultTextureSize = 33;

    private readonly Vector3[] _entries;

    /// <summary>A table of <paramref name="size"/>³ entries (3D) or <paramref name="size"/> entries (1D), copied.</summary>
    public CubeLut(int size, bool is1D, ReadOnlySpan<Vector3> entries, Vector3? domainMin = null, Vector3? domainMax = null, string? title = null)
    {
        if (size < 2 || size > (is1D ? MaxSize1D : MaxSize3D))
            throw new ArgumentOutOfRangeException(nameof(size), size, $"A {(is1D ? "1D" : "3D")} LUT has 2–{(is1D ? MaxSize1D : MaxSize3D)} entries per axis.");
        var count = is1D ? size : (long)size * size * size;
        if (entries.Length != count)
            throw new ArgumentException($"Expected {count} entries for a {(is1D ? "1D" : "3D")} LUT of size {size}, got {entries.Length}.", nameof(entries));
        var min = domainMin ?? Vector3.Zero;
        var max = domainMax ?? Vector3.One;
        if (!(max.X > min.X && max.Y > min.Y && max.Z > min.Z))
            throw new ArgumentException($"DOMAIN_MAX {max} must be above DOMAIN_MIN {min} on every channel.", nameof(domainMax));
        Size = size;
        Is1D = is1D;
        DomainMin = min;
        DomainMax = max;
        Title = title;
        _entries = entries.ToArray();
    }

    public string? Title { get; }

    /// <summary>Entries per axis (3D) or per curve (1D).</summary>
    public int Size { get; }

    /// <summary>True for a <c>LUT_1D_SIZE</c> table: three independent channel curves.</summary>
    public bool Is1D { get; }

    public Vector3 DomainMin { get; }
    public Vector3 DomainMax { get; }

    /// <summary>The table rows: index <c>r + g·N + b·N²</c> (3D) or <c>i</c> (1D).</summary>
    public ReadOnlySpan<Vector3> Entries => _entries;

    /// <summary>True when the domain is the default 0–1 on every channel.</summary>
    public bool HasUnitDomain => DomainMin == Vector3.Zero && DomainMax == Vector3.One;

    /// <summary>The 3D entry at (<paramref name="r"/>, <paramref name="g"/>, <paramref name="b"/>).</summary>
    public Vector3 this[int r, int g, int b] => Is1D
        ? throw new InvalidOperationException("A 1D LUT has no 3D entries.")
        : _entries[r + Size * (g + Size * b)];

    /// <summary>A 3D identity table: entry (r, g, b) = (r, g, b) / (N − 1).</summary>
    public static CubeLut Identity(int size = DefaultTextureSize) => FromFunction(size, static c => c, "identity");

    /// <summary>A 3D table of <paramref name="size"/>³ entries holding <paramref name="grade"/> of each grid colour (0–1).</summary>
    public static CubeLut FromFunction(int size, Func<Vector3, Vector3> grade, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(grade);
        if (size < 2 || size > MaxSize3D)
            throw new ArgumentOutOfRangeException(nameof(size), size, $"A 3D LUT has 2–{MaxSize3D} entries per axis.");
        var entries = new Vector3[size * size * size];
        var step = 1f / (size - 1);
        for (var b = 0; b < size; b++)
            for (var g = 0; g < size; g++)
                for (var r = 0; r < size; r++)
                    entries[r + size * (g + size * b)] = grade(new Vector3(r * step, g * step, b * step));
        return new CubeLut(size, false, entries, title: title);
    }

    /// <summary>
    /// Looks <paramref name="color"/> up as the GPU does: normalised into the domain, clamped, then trilinear between the
    /// eight nearest entries (3D) or linear along each channel's curve (1D).
    /// </summary>
    public Vector3 Sample(Vector3 color)
    {
        var t = Vector3.Clamp((color - DomainMin) / (DomainMax - DomainMin), Vector3.Zero, Vector3.One) * (Size - 1);
        if (Is1D)
            return new Vector3(Curve(t.X).X, Curve(t.Y).Y, Curve(t.Z).Z);

        var i = new Vector3(MathF.Floor(t.X), MathF.Floor(t.Y), MathF.Floor(t.Z));
        var f = t - i;
        int r0 = (int)i.X, g0 = (int)i.Y, b0 = (int)i.Z;
        int r1 = Math.Min(r0 + 1, Size - 1), g1 = Math.Min(g0 + 1, Size - 1), b1 = Math.Min(b0 + 1, Size - 1);
        var c00 = Vector3.Lerp(this[r0, g0, b0], this[r1, g0, b0], f.X);
        var c10 = Vector3.Lerp(this[r0, g1, b0], this[r1, g1, b0], f.X);
        var c01 = Vector3.Lerp(this[r0, g0, b1], this[r1, g0, b1], f.X);
        var c11 = Vector3.Lerp(this[r0, g1, b1], this[r1, g1, b1], f.X);
        return Vector3.Lerp(Vector3.Lerp(c00, c10, f.Y), Vector3.Lerp(c01, c11, f.Y), f.Z);

        Vector3 Curve(float x)
        {
            var lo = Math.Min((int)x, Size - 1);
            var hi = Math.Min(lo + 1, Size - 1);
            return Vector3.Lerp(_entries[lo], _entries[hi], x - lo);
        }
    }

    /// <summary>
    /// The <see cref="Texture3DFormat.Rgba16F"/> volume the colour grade samples over 0–1 (alpha 1). A 3D table with the
    /// unit domain is copied entry for entry; a 1D table, or a 3D one over another domain, is resampled through
    /// <see cref="Sample"/> into a <paramref name="resampleSize"/>³ table.
    /// </summary>
    public Texture3D ToTexture3D(int resampleSize = DefaultTextureSize)
    {
        var direct = !Is1D && HasUnitDomain;
        var n = direct ? Size : resampleSize;
        if (n < 2 || n > MaxSize3D)
            throw new ArgumentOutOfRangeException(nameof(resampleSize), resampleSize, $"A 3D LUT has 2–{MaxSize3D} entries per axis.");
        var rgba = new float[n * n * n * 4];
        var step = 1f / (n - 1);
        for (var b = 0; b < n; b++)
            for (var g = 0; g < n; g++)
                for (var r = 0; r < n; r++)
                {
                    var index = r + n * (g + n * b);
                    var c = direct ? _entries[index] : Sample(DomainMin + new Vector3(r, g, b) * step * (DomainMax - DomainMin));
                    rgba[index * 4] = c.X;
                    rgba[index * 4 + 1] = c.Y;
                    rgba[index * 4 + 2] = c.Z;
                    rgba[index * 4 + 3] = 1f;
                }

        return Texture3D.FromRgba(n, n, n, rgba);
    }

    /// <summary>Reads a <c>.cube</c> file (a path for <see cref="ContentPaths.Resolve(string)"/>).</summary>
    public static CubeLut Load(string path)
    {
        var full = ContentPaths.Resolve(path);
        using var reader = new StreamReader(full, Encoding.UTF8);
        return Parse(reader, Path.GetFileName(path));
    }

    /// <summary>Parses <c>.cube</c> text.</summary>
    public static CubeLut Parse(string text) => Parse(new StringReader(text));

    /// <summary>Parses <c>.cube</c> text; <paramref name="source"/> names it in error messages.</summary>
    /// <exception cref="InvalidDataException">The text is not a valid <c>.cube</c> table.</exception>
    public static CubeLut Parse(TextReader reader, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var where = source ?? ".cube";
        string? title = null;
        int size3D = 0, size1D = 0;
        Vector3? min = null, max = null;
        List<Vector3>? rows = null;
        var lineNumber = 0;
        Span<float> values = stackalloc float[3];
        while (reader.ReadLine() is { } raw)
        {
            lineNumber++;
            var line = raw.AsSpan().Trim();
            if (line.IsEmpty || line[0] == '#')
                continue;

            if (char.IsAsciiLetter(line[0]))
            {
                if (rows is not null)
                    throw Error($"keyword after the table data: '{line}'");
                var space = line.IndexOfAny(' ', '\t');
                var keyword = space < 0 ? line : line[..space];
                var rest = space < 0 ? ReadOnlySpan<char>.Empty : line[(space + 1)..].Trim();
                switch (keyword)
                {
                    case "TITLE":
                        title = rest.Trim('"').ToString();
                        break;
                    case "LUT_3D_SIZE":
                        size3D = ParseInt(rest);
                        if (size3D < 2 || size3D > MaxSize3D)
                            throw Error($"LUT_3D_SIZE {size3D} is outside 2–{MaxSize3D}");
                        break;
                    case "LUT_1D_SIZE":
                        size1D = ParseInt(rest);
                        if (size1D < 2 || size1D > MaxSize1D)
                            throw Error($"LUT_1D_SIZE {size1D} is outside 2–{MaxSize1D}");
                        break;
                    case "DOMAIN_MIN":
                        min = ParseTriple(rest);
                        break;
                    case "DOMAIN_MAX":
                        max = ParseTriple(rest);
                        break;
                    case "LUT_1D_INPUT_RANGE" or "LUT_3D_INPUT_RANGE":
                        {
                            var range = rest.ToString().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                            if (range.Length != 2)
                                throw Error($"{keyword} needs two numbers");
                            var lo = ParseFloat(range[0]);
                            var hi = ParseFloat(range[1]);
                            min = new Vector3(lo);
                            max = new Vector3(hi);
                            break;
                        }
                    default:
                        break; // LUT_IN_VIDEO_RANGE, LUT_OUT_VIDEO_RANGE and other vendor keywords: no effect here
                }

                continue;
            }

            if (size3D == 0 && size1D == 0)
                throw Error("table data before LUT_3D_SIZE or LUT_1D_SIZE");
            if (size3D != 0 && size1D != 0)
                throw Error("both LUT_3D_SIZE and LUT_1D_SIZE are given");
            rows ??= new List<Vector3>(size3D != 0 ? size3D * size3D * size3D : size1D);
            var count = 0;
            foreach (var range in line.SplitAny(' ', '\t'))
            {
                var token = line[range];
                if (token.IsEmpty)
                    continue;
                if (count == 3)
                    throw Error($"more than three numbers: '{line}'");
                values[count++] = ParseFloat(token);
            }

            if (count != 3)
                throw Error($"expected three numbers, got '{line}'");
            rows.Add(new Vector3(values[0], values[1], values[2]));
        }

        if (size3D == 0 && size1D == 0)
            throw new InvalidDataException($"{where}: no LUT_3D_SIZE or LUT_1D_SIZE.");
        var is1D = size1D != 0;
        var expected = is1D ? size1D : size3D * size3D * size3D;
        var got = rows?.Count ?? 0;
        if (got != expected)
            throw new InvalidDataException($"{where}: expected {expected} table rows for {(is1D ? $"LUT_1D_SIZE {size1D}" : $"LUT_3D_SIZE {size3D}")}, got {got}.");
        try
        {
            return new CubeLut(is1D ? size1D : size3D, is1D, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows), min, max, title);
        }
        catch (ArgumentException e)
        {
            throw new InvalidDataException($"{where}: {e.Message}", e);
        }

        InvalidDataException Error(string message) => new($"{where}({lineNumber}): {message}.");

        int ParseInt(ReadOnlySpan<char> text) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw Error($"'{text}' is not an integer");

        float ParseFloat(ReadOnlySpan<char> text) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v)
                ? v
                : throw Error($"'{text}' is not a number");

        Vector3 ParseTriple(ReadOnlySpan<char> text)
        {
            var parts = text.ToString().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
                throw Error($"expected three numbers, got '{text}'");
            return new Vector3(ParseFloat(parts[0]), ParseFloat(parts[1]), ParseFloat(parts[2]));
        }
    }

    /// <summary>Writes the table as <c>.cube</c> text (six decimals, LF line endings).</summary>
    public void Write(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var inv = CultureInfo.InvariantCulture;
        if (Title is { Length: > 0 })
            writer.Write($"TITLE \"{Title}\"\n");
        writer.Write(Is1D ? $"LUT_1D_SIZE {Size}\n" : $"LUT_3D_SIZE {Size}\n");
        if (!HasUnitDomain)
        {
            writer.Write(string.Create(inv, $"DOMAIN_MIN {DomainMin.X:0.######} {DomainMin.Y:0.######} {DomainMin.Z:0.######}\n"));
            writer.Write(string.Create(inv, $"DOMAIN_MAX {DomainMax.X:0.######} {DomainMax.Y:0.######} {DomainMax.Z:0.######}\n"));
        }

        foreach (var e in _entries)
            writer.Write(string.Create(inv, $"{e.X:0.000000} {e.Y:0.000000} {e.Z:0.000000}\n"));
    }

    /// <summary>Writes the table to <paramref name="path"/> as <c>.cube</c> text.</summary>
    public void Save(string path)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        Write(writer);
    }

    public override string ToString() => $"CubeLut {(Is1D ? "1D" : "3D")} {Size}{(Title is null ? "" : $" \"{Title}\"")}";
}

/// <summary><c>.cube</c> colour lookup tables → <see cref="Texture3D"/> (<see cref="CubeLut.ToTexture3D"/>), for colour grading.</summary>
public sealed class CubeLutImporter : IAssetImporter
{
    public string Name => "cube-lut";

    public IReadOnlyList<string> Extensions { get; } = [".cube"];

    public Resource Import(string fullPath, string projectPath, AssetMeta? meta)
    {
        using var reader = new StreamReader(fullPath, Encoding.UTF8);
        return CubeLut.Parse(reader, projectPath).ToTexture3D();
    }
}
