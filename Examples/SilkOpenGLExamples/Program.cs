using Silk.NET.Maths;
using Silk.NET.Windowing;
using SilkTest.Examples;
using SilkTest.Utils;
using Monitor = Silk.NET.Windowing.Monitor;

Console.ForegroundColor = ConsoleColor.DarkBlue;

var example = GetExample();

var exampleName = example.GetType().Name;
Console.WriteLine($"Running {exampleName}");

var windowOptions = WindowOptions.Default;
windowOptions.Size = new Vector2D<int>(800, 600);
var window = Window.Create(windowOptions) ?? throw new NullReferenceException();

window.Title = exampleName;

// set window to center of monitor
var monitor = Monitor.GetMainMonitor(window);
var centerScreen = (monitor.VideoMode.Resolution - window.Size) / 2;
window.Position = centerScreen!.Value;

window.Load += () => example.OnLoad(window);
window.FramebufferResize += s => example.OnFramebufferResize(s);
window.Update += delta => example.OnUpdate(delta);
window.Render += delta => example.OnRender(delta);
window.Closing += () => example.OnClose();

window.Run();
window.Dispose();

Console.WriteLine("Program terminated.");
return;

static IExample GetExample()
{
    var exampleTypes = typeof(IExample)
        .Assembly.GetTypes()
        .Where(x => x.GetInterfaces().Contains(typeof(IExample)) && !x.IsAbstract)
        .Select(x => new SelectionUI.Info { Name = x.Name.Replace("Example", ""), Type = x })
        .ToArray();
    var selectionUi = new SelectionUI(exampleTypes);
    var selectedExampleType = selectionUi.Select() ?? throw new NullReferenceException();
    var example =
        Activator.CreateInstance(selectedExampleType.Type) as IExample
        ?? throw new NullReferenceException();
    return example;
}
