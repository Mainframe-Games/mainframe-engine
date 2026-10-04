using System.Numerics;
using System.Text;
using BenchmarkDotNet.Attributes;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// Game UI (M8) costs: RmlUi layout and update of a 500-element document (idle, with 500 data-bound values dirtied, and
/// a forced full relayout), and one frame of render-interface callbacks through the managed boundary (~1 000
/// <c>RenderGeometry</c> calls into a no-op C# renderer). No GPU; the Vulkan replay is covered by the render tests.
/// </summary>
[MemoryDiagnoser]
public class UiBenchmarks : IDisposable
{
    private const int Rows = 500;

    private CountingRenderer _renderer = null!;
    private RmlContext _context = null!;
    private RmlDataModel _model = null!;
    private RmlElement _root;
    private readonly List<Row> _rows = [];
    private int _tick;
    private bool _wide;

    private sealed class Row
    {
        public int Value { get; set; }
    }

    private static readonly RmlStructType<Row> RowType = new RmlStructType<Row>().Member("value", static r => r.Value);

    /// <summary>Counts callbacks; hands out handles; does nothing else (isolates the binding's callback overhead).</summary>
    private sealed class CountingRenderer : RmlRenderInterface
    {
        private ulong _next;
        public int Draws;

        protected override ulong CompileGeometry(ReadOnlySpan<RmlVertex> vertices, ReadOnlySpan<int> indices) => ++_next;
        protected override void RenderGeometry(ulong geometry, Vector2 translation, ulong texture) => Draws++;
        protected override void ReleaseGeometry(ulong geometry) { }
        protected override ulong LoadTexture(string source, out int width, out int height) { width = height = 1; return ++_next; }
        protected override ulong GenerateTexture(ReadOnlySpan<byte> rgba, int width, int height) => ++_next;
        protected override void ReleaseTexture(ulong texture) { }
        protected override void EnableScissorRegion(bool enable) { }
        protected override void SetScissorRegion(int x, int y, int width, int height) { }
    }

    private sealed class QuietSystem : RmlSystemInterface
    {
        public double Time;

        public override double GetElapsedTime() => Time;

        public override bool LogMessage(RmlLogType type, string message) => true;
    }

    private readonly QuietSystem _system = new();

    [GlobalSetup]
    public void Setup()
    {
        RmlCore.Initialise(_system, new RmlContentFileInterface());
        RmlCore.LoadFontFace("Content/UI/fonts/LatoLatin-Regular.ttf");
        _renderer = new CountingRenderer();
        _context = new RmlContext("bench", 1920, 1080, _renderer);
        for (var i = 0; i < Rows; i++)
            _rows.Add(new Row { Value = i });
        _model = _context.CreateDataModel("bench").BindList("rows", _rows, RowType);

        // 500 elements: rows of a bordered box with a data-bound label (box + text = ~2 geometries each).
        var rml = new StringBuilder();
        rml.Append("<rml><head><style>body { font-family: LatoLatin; font-size: 12px; color: #fff; width: 1800px; } ");
        rml.Append("div.row { display: inline-block; width: 160px; height: 18px; margin: 1px; background-color: #234; border: 1px #456; }");
        rml.Append("</style></head><body id='root' data-model='bench'>");
        rml.Append("<div class='row' data-for='r : rows'>row {{ r.value }}</div>");
        rml.Append("</body></rml>");
        var document = _context.LoadDocumentFromMemory(rml.ToString());
        document.Show(RmlModal.None, RmlFocus.None);
        _root = document.AsElement();
        for (var i = 0; i < 5; i++)
            Frame();
    }

    private void Frame()
    {
        _system.Time += 1.0 / 60.0;
        _context.Update();
        _context.Render();
    }

    /// <summary>Update of an unchanged 500-element document (the steady-state HUD cost).</summary>
    [Benchmark]
    public void Update500Idle()
    {
        _system.Time += 1.0 / 60.0;
        _context.Update();
    }

    /// <summary>500 data-bound values change (every row's text): bindings re-read, text re-laid out.</summary>
    [Benchmark]
    public void Update500DirtyBindings()
    {
        _tick++;
        foreach (var row in _rows)
            row.Value = _tick;
        _model.Dirty("rows");
        _system.Time += 1.0 / 60.0;
        _context.Update();
    }

    /// <summary>A full relayout of the 500 elements (the body's width changes, rows re-flow).</summary>
    [Benchmark]
    public void Relayout500()
    {
        _wide = !_wide;
        _root.SetProperty("width", _wide ? "1700px" : "1800px");
        _system.Time += 1.0 / 60.0;
        _context.Update();
    }

    /// <summary>One render of the document: ~1 000 RenderGeometry callbacks across the native boundary.</summary>
    [Benchmark]
    public int Render500Callbacks()
    {
        _renderer.Draws = 0;
        _context.Render();
        return _renderer.Draws;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _context?.Dispose();
        RmlCore.Shutdown();
        _renderer?.Dispose();
    }

    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }
}
