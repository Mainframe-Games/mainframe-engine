namespace MainframeEngine.Editor.Tests.Projects;

/// <summary>Uses <see cref="TemplateLocator.EnginePathVariable"/> (process-wide), so it runs serially.</summary>
[Collection(nameof(SerialEditor))]
public sealed class TemplateLocatorTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-locator").FullName;
    private readonly string? _previousEnginePath = Environment.GetEnvironmentVariable(TemplateLocator.EnginePathVariable);

    public TemplateLocatorTests() => Environment.SetEnvironmentVariable(TemplateLocator.EnginePathVariable, null);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TemplateLocator.EnginePathVariable, _previousEnginePath);
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>A fake checkout: the engine project and the template's template.json.</summary>
    private string FakeCheckout(string name, bool withTemplate = true)
    {
        var root = Path.Combine(_directory, name);
        Directory.CreateDirectory(Path.Combine(root, "MainframeEngine"));
        File.WriteAllText(Path.Combine(root, TemplateLocator.EngineProjectRelativePath), "<Project />");
        if (withTemplate)
        {
            var config = Directory.CreateDirectory(Path.Combine(root, TemplateLocator.CheckoutTemplateRelativePath, ".template.config")).FullName;
            File.WriteAllText(Path.Combine(config, "template.json"), "{}");
        }

        return root;
    }

    [Fact]
    public void WalksUpFromANestedFolderToTheCheckout()
    {
        var checkout = FakeCheckout("engine");
        var nested = Directory.CreateDirectory(Path.Combine(checkout, "MainframeEngine.Editor", "bin", "Debug", "net10.0")).FullName;

        Assert.Equal(checkout, TemplateLocator.FindEngineCheckout(nested));
        Assert.Equal(checkout, TemplateLocator.FindEngineCheckout(checkout + Path.DirectorySeparatorChar));
        Assert.Equal(Path.Combine(checkout, TemplateLocator.CheckoutTemplateRelativePath), TemplateLocator.FindTemplate(checkout, nested));
    }

    [Fact]
    public void AFolderWithoutTheTemplateIsNotACheckout()
    {
        var partial = FakeCheckout("partial", withTemplate: false);
        var nested = Directory.CreateDirectory(Path.Combine(partial, "a", "b")).FullName;
        Assert.Null(TemplateLocator.FindEngineCheckout(nested)); // nothing above the temp folder is a checkout either
        Assert.Null(TemplateLocator.FindTemplate(partial, nested));
    }

    [Fact]
    public void EnvironmentOverrideWinsWhenItIsACheckout()
    {
        var configured = FakeCheckout("configured", withTemplate: false); // the override only needs the engine project
        var other = FakeCheckout("other");
        Environment.SetEnvironmentVariable(TemplateLocator.EnginePathVariable, configured + Path.DirectorySeparatorChar);
        Assert.Equal(configured, TemplateLocator.FindEngineCheckout(other));

        Environment.SetEnvironmentVariable(TemplateLocator.EnginePathVariable, Path.Combine(_directory, "missing"));
        Assert.Equal(other, TemplateLocator.FindEngineCheckout(other)); // ignored (with a warning)
    }

    [Fact]
    public void FallsBackToAPackagedTemplateNextToTheApp()
    {
        var app = Path.Combine(_directory, "app");
        var config = Directory.CreateDirectory(Path.Combine(app, TemplateLocator.PackagedTemplateRelativePath, ".template.config")).FullName;
        File.WriteAllText(Path.Combine(config, "template.json"), "{}");

        Assert.Equal(Path.Combine(app, "Templates", "mfgame"), TemplateLocator.FindTemplate(null, app));
        Assert.Equal(Path.Combine(app, "Templates", "mfgame"), TemplateLocator.FindTemplate(Path.Combine(_directory, "no-checkout"), app));
        Assert.Null(TemplateLocator.FindTemplate(null, _directory));
    }

    [Fact]
    public void FindsThisRepositoryFromTheTestOutputFolder()
    {
        var checkout = TemplateLocator.FindEngineCheckout();
        Assert.NotNull(checkout);
        Assert.True(File.Exists(Path.Combine(checkout, "MainframeEngine.slnx")));
        Assert.StartsWith(checkout, AppContext.BaseDirectory, StringComparison.Ordinal);

        var template = TemplateLocator.FindTemplate(checkout);
        Assert.NotNull(template);
        Assert.True(File.Exists(Path.Combine(template, "project.mfproj")));
        Assert.True(TemplateLocator.IsTemplate(template));
    }
}
