using Silk.NET.Maths;
using Silk.NET.Windowing;
using SilkVulkanExamples.Vulkan;
using Spectre.Console;

using var example = GetExample();
Console.WriteLine($"Running {example.Name}");

var windowOptions = WindowOptions.DefaultVulkan with
{
    Size = new Vector2D<int>(800, 600),
    Title = example.Name,
};

var window = Window.Create(windowOptions) ?? throw new InvalidOperationException("Failed to create the window.");

using (window)
{
    example.Initialize(window);
    example.Run();
    example.Dispose(); // shut down before the window is disposed
}

Console.WriteLine("Program terminated.");
return;

static IExample GetExample()
{
    var exampleTypes = typeof(IExample)
        .Assembly.GetTypes()
        .Where(x => x.IsSubclassOf(typeof(ExampleBase)) && !x.IsAbstract)
        .ToArray();

    var choice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("Select an example to run:")
            .AddChoices(exampleTypes.Select(x => x.Name)));

    AnsiConsole.MarkupLine($"You selected: [green]{choice}[/]");
    var selected = exampleTypes.First(x => x.Name == choice);
    var example = Activator.CreateInstance(selected) as IExample
        ?? throw new InvalidOperationException("Failed to create example instance.");
    return example;
}
