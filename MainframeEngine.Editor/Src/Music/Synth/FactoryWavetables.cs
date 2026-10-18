namespace MainframeEngine.Editor.Music;

/// <summary>
/// The synth's factory wavetables, generated from code (never stored), all original: <c>factory:&lt;set&gt;/&lt;name&gt;</c>.
/// Each returns its frames (2048 samples, one cycle) for <see cref="Wavetable.Build"/>.
/// </summary>
public static class FactoryWavetables
{
    public const string Prefix = "factory:";
    public const string DefaultSource = "factory:basic/saw";

    private const int N = Wavetable.FrameSize;

    private static readonly (string Name, Func<List<float[]>> Make)[] Tables =
    [
        ("basic/sine", () => [Wave(static p => Math.Sin(2 * Math.PI * p))]),
        ("basic/triangle", () => [Wave(Triangle)]),
        ("basic/saw", () => [Additive(static k => 1.0 / k)]),
        ("basic/square", () => [Additive(static k => k % 2 == 1 ? 1.0 / k : 0)]),
        ("basic/shapes", BasicShapes),
        ("analog/pwm", () => Frames(64, static t => Additive(k => Math.Sin(Math.PI * k * (0.5 - 0.47 * t)) / k))),
        ("analog/saw-filter", () => Frames(64, static t => Additive(k => 1.0 / k / Math.Sqrt(1 + Math.Pow(k / (1 + 63 * t * t), 4))))),
        ("analog/sync-sweep", () => Frames(64, static t => Wave(p => 2 * Frac(p * (1 + 7 * t)) - 1))),
        ("harmonic/build-up", () => Frames(64, static t => Additive(k => k <= 1 + (int)(t * 63) ? 1.0 / Math.Sqrt(k) : 0))),
        ("harmonic/odd-sweep", () => Frames(32, static t => Additive(k => k % 2 == 1 && k <= 1 + (int)(t * 63) ? 1.0 / k : 0))),
        ("harmonic/even-sweep", () => Frames(32, static t => Additive(k => k == 1 || (k % 2 == 0 && k <= 2 + (int)(t * 62)) ? 1.0 / k : 0))),
        ("harmonic/bell", () => Frames(32, static t => Additive(k => k is 1 or 3 or 5 or 7 or 12 or 17 or 23 ? Math.Exp(-k * (0.02 + 0.2 * t)) : 0))),
        ("formant/vowels", Vowels),
        ("digital/bitcrush", () => Frames(32, static t => Wave(p => Quantize(2 * p - 1, (int)Math.Round(Math.Pow(2, 8 - 6 * t)))))),
        ("digital/fm", () => Frames(64, static t => Wave(p => Math.Sin(2 * Math.PI * p + 8 * t * Math.Sin(4 * Math.PI * p))))),
        ("digital/fold", () => Frames(64, static t => Wave(p => Fold((1 + 7 * t) * Math.Sin(2 * Math.PI * p))))),
    ];

    /// <summary>Every factory table's source, in menu order.</summary>
    public static IReadOnlyList<string> Sources { get; } = [.. Tables.Select(static t => Prefix + t.Name)];

    /// <summary>The frames of factory table <paramref name="source"/>, or null when there is no such table.</summary>
    public static List<float[]>? Generate(string source)
    {
        if (!source.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        var name = source[Prefix.Length..];
        foreach (var (n, make) in Tables)
        {
            if (string.Equals(n, name, StringComparison.Ordinal))
                return make();
        }

        return null;
    }

    /// <summary>"Saw (Basic)" for <c>factory:basic/saw</c>.</summary>
    public static string DisplayName(string source)
    {
        var name = source.StartsWith(Prefix, StringComparison.Ordinal) ? source[Prefix.Length..] : Path.GetFileNameWithoutExtension(source);
        var slash = name.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
            return name;
        static string Title(string s) => string.Join(' ', s.Split('-').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
        return $"{Title(name[(slash + 1)..])} ({Title(name[..slash])})";
    }

    private static List<float[]> Frames(int count, Func<double, float[]> frame)
    {
        var frames = new List<float[]>(count);
        for (var i = 0; i < count; i++)
            frames.Add(frame(count == 1 ? 0 : i / (double)(count - 1)));
        return frames;
    }

    private static float[] Wave(Func<double, double> f)
    {
        var frame = new float[N];
        for (var i = 0; i < N; i++)
            frame[i] = (float)f(i / (double)N);
        return frame;
    }

    // A cycle from harmonic amplitudes (sine phase), harmonics 1…1023: the spectrum, then one inverse FFT.
    private static float[] Additive(Func<int, double> amplitude)
    {
        var re = new double[N];
        var im = new double[N];
        for (var k = 1; k < N / 2; k++)
        {
            var a = amplitude(k);
            if (a == 0 || !double.IsFinite(a))
                continue;
            im[k] = -a * N / 2;
            im[N - k] = a * N / 2;
        }

        Fft.Transform(re, im, inverse: true);
        return [.. re.Select(static v => (float)v)];
    }

    private static double Triangle(double p) => p < 0.25 ? 4 * p : p < 0.75 ? 2 - 4 * p : 4 * p - 4;

    private static double Frac(double x) => x - Math.Floor(x);

    private static double Quantize(double x, int levels) => Math.Round(x * levels) / levels;

    private static double Fold(double x)
    {
        var t = (x + 1) * 0.25;
        t -= Math.Floor(t);
        return Math.Abs(t * 4 - 2) - 1;
    }

    // Sine → triangle → saw → square across 64 frames (each pair crossfaded).
    private static List<float[]> BasicShapes()
    {
        float[][] shapes =
        [
            Wave(static p => Math.Sin(2 * Math.PI * p)), Wave(Triangle), Additive(static k => 1.0 / k),
            Additive(static k => k % 2 == 1 ? 1.0 / k : 0),
        ];
        var peaks = shapes.Select(static s => s.Max(MathF.Abs)).ToArray();
        return Frames(64, t =>
        {
            var x = t * 3;
            var i = Math.Min(2, (int)x);
            var f = (float)(x - i);
            var frame = new float[N];
            for (var s = 0; s < N; s++)
                frame[s] = shapes[i][s] / peaks[i] * (1 - f) + shapes[i + 1][s] / peaks[i + 1] * f;
            return frame;
        });
    }

    // Additive frames shaped by vowel formants (A → E → I → O → U) at a 110 Hz fundamental.
    private static List<float[]> Vowels()
    {
        (double F1, double F2, double F3)[] vowels = [(800, 1150, 2900), (400, 1600, 2700), (350, 1700, 2700), (450, 800, 2830), (325, 700, 2530)];
        return Frames(64, t =>
        {
            var x = t * (vowels.Length - 1);
            var i = Math.Min(vowels.Length - 2, (int)x);
            var f = x - i;
            var (a1, a2, a3) = vowels[i];
            var (b1, b2, b3) = vowels[i + 1];
            double[] formants = [a1 + (b1 - a1) * f, a2 + (b2 - a2) * f, a3 + (b3 - a3) * f];
            return Additive(k =>
            {
                var hz = 110.0 * k;
                var amp = 0.0;
                for (var n = 0; n < 3; n++)
                {
                    var bw = 60 + 40 * n;
                    amp += Math.Exp(-Math.Pow((hz - formants[n]) / bw, 2)) / (n + 1);
                }

                return k > 120 ? 0 : amp / Math.Sqrt(k);
            });
        });
    }
}

/// <summary>
/// The synth's wavetables by source, built once per process on a worker task and shared by every synth. Until a table
/// is ready, <see cref="Get"/> returns null (the synth keeps its previous table or the default saw);
/// <see cref="Version"/> moves when one finishes, which makes the song player rebuild and pick it up.
/// </summary>
public static class WavetableCache
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Task<Wavetable?>> Builds = new(StringComparer.Ordinal);
    private static Wavetable? _default;
    private static int _version;

    /// <summary>Bumped whenever a table finishes building.</summary>
    public static int Version => Volatile.Read(ref _version);

    /// <summary>The default table (the factory saw), built on first use on the calling thread.</summary>
    public static Wavetable Default
    {
        get
        {
            if (Volatile.Read(ref _default) is { } table)
                return table;
            table = Wavetable.Build(FactoryWavetables.DefaultSource, FactoryWavetables.Generate(FactoryWavetables.DefaultSource)!);
            Interlocked.CompareExchange(ref _default, table, null);
            return _default!;
        }
    }

    /// <summary>
    /// UI thread: the table for <paramref name="source"/> if it is built, else null (its build is started). An unknown
    /// source sets <paramref name="error"/> and returns null.
    /// </summary>
    public static Wavetable? Get(string source, out string? error)
    {
        error = null;
        if (string.Equals(source, FactoryWavetables.DefaultSource, StringComparison.Ordinal))
            return Default;
        Task<Wavetable?> build;
        lock (Gate)
        {
            if (!Builds.TryGetValue(source, out build!))
            {
                build = Task.Run(() =>
                {
                    var frames = FactoryWavetables.Generate(source);
                    var table = frames is null ? null : Wavetable.Build(source, frames);
                    Interlocked.Increment(ref _version);
                    return table;
                });
                Builds[source] = build;
            }
        }

        if (!build.IsCompleted)
            return null;
        var result = build.IsCompletedSuccessfully ? build.Result : null;
        if (result is null)
            error = "missing wavetable: " + source;
        return result;
    }

    /// <summary>
    /// The table for <paramref name="source"/>, built on the calling thread if it is not ready (small single-frame tables:
    /// the sub oscillator's shapes); the default table for an unknown source.
    /// </summary>
    public static Wavetable GetNow(string source)
    {
        if (Get(source, out _) is { } ready)
            return ready;
        Task<Wavetable?> build;
        lock (Gate)
            build = Builds[source];
        build.Wait();
        return build.Result ?? Default;
    }

    /// <summary>Waits for every table being built (offline renders, tests).</summary>
    public static void WaitForPending()
    {
        Task[] pending;
        lock (Gate)
            pending = [.. Builds.Values.Where(static t => !t.IsCompleted)];
        if (pending.Length > 0)
            Task.WaitAll(pending);
    }
}
