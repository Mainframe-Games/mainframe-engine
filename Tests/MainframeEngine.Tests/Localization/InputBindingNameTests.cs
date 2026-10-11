using MainframeEngine.L10n.Gettext;
using MainframeEngine.Localization;
using Silk.NET.Input;

namespace MainframeEngine.Tests.Localization;

/// <summary><see cref="InputBinding.DisplayName"/>: the key-name table, the Xbox gamepad layout, translation and extraction.</summary>
[Collection(nameof(LocalizationState))]
public sealed class InputBindingNameTests : IDisposable
{
    private readonly LocaleFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData("key:Space", "Space")]
    [InlineData("key:ShiftLeft", "Left Shift")]
    [InlineData("key:ControlRight", "Right Ctrl")]
    [InlineData("key:Escape", "Esc")]
    [InlineData("key:E", "E")]
    [InlineData("key:Number7", "7")]
    [InlineData("key:F11", "F11")]
    [InlineData("key:Keypad5", "Numpad 5")]
    [InlineData("key:PageDown", "Page Down")]
    [InlineData("key:Up", "Up Arrow")]
    [InlineData("key:Slash", "/")]
    [InlineData("mouse:Left", "Mouse Left")]
    [InlineData("mouse:Middle", "Mouse Middle")]
    [InlineData("mouse:Button5", "Mouse 5")]
    [InlineData("pad:A", "A")]
    [InlineData("pad:LeftBumper", "LB")]
    [InlineData("pad:RightStick", "RS")]
    [InlineData("pad:Start", "Start")]
    [InlineData("pad:DPadUp", "D-Pad Up")]
    [InlineData("pad1:A", "A (Pad 2)")]
    [InlineData("axis:LeftY-", "LS ↑")]
    [InlineData("axis:LeftX+", "LS →")]
    [InlineData("axis:RightY+", "RS ↓")]
    [InlineData("axis:LeftTrigger+", "LT")]
    [InlineData("axis:RightTrigger+", "RT")]
    public void BindingsHavePlayerFacingNames(string binding, string name)
    {
        Tr.ResetForTests();
        Assert.Equal(name, InputBinding.Parse(binding).DisplayName());
    }

    [Fact]
    public void ModifierNamesFollowThePlatformAndEmptyBindingsHaveNoName()
    {
        Tr.ResetForTests();
        var alt = InputBinding.Key(Key.AltLeft).DisplayName();
        Assert.Equal(OperatingSystem.IsMacOS() ? "Left Option" : "Left Alt", alt);
        Assert.Equal("", default(InputBinding).DisplayName());
    }

    [Fact]
    public void NamesAreTranslatedInTheInputContext()
    {
        _fixture.AddCatalog("es", LocaleFixture.OneOther, """
            msgctxt "input"
            msgid "Left Shift"
            msgstr "Mayús izq."

            msgctxt "input"
            msgid "Mouse {0}"
            msgstr "Ratón {0}"

            msgctxt "input"
            msgid "{0} (Pad {1})"
            msgstr "{0} (mando {1})"

            msgid "Space"
            msgstr "not the input context"
            """);
        _fixture.Use("es");

        Assert.Equal("Mayús izq.", InputBinding.Key(Key.ShiftLeft).DisplayName());
        Assert.Equal("Ratón 4", InputBinding.Mouse(MouseButton.Button4).DisplayName());
        Assert.Equal("Start (mando 1)", InputBinding.Parse("pad0:Start").DisplayName());
        Assert.Equal("Space", InputBinding.Key(Key.Space).DisplayName());
    }

    [Fact]
    public void TheNameTableIsExtractable()
    {
        var root = ExtractionTests.FindRepositoryRoot();
        Assert.SkipWhen(root is null, "repository root not found");
        var work = Path.Combine(Path.GetTempPath(), "mf-l10n-extractor", Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(work, "Src");
            Directory.CreateDirectory(source);
            File.Copy(Path.Combine(root!, "MainframeEngine", "Src", "Scene", "Input", "InputBindingNames.cs"), Path.Combine(source, "InputBindingNames.cs"));
            var output = Path.Combine(work, "code.pot");
            if (!ExtractionTests.RunExtractor(source, output, out var log))
                Assert.Skip($"GetText.Extractor is not available (run 'dotnet tool restore'): {log}");

            var entries = PoParser.ParseFile(output).ByKey();
            Assert.True(entries.ContainsKey("input\u0004Left Shift"));
            Assert.True(entries.ContainsKey("input\u0004Mouse Left"));
            Assert.True(entries.ContainsKey("input\u0004Space"));
            Assert.True(entries.ContainsKey("input\u0004D-Pad Up"));
            Assert.True(entries.ContainsKey("input\u0004Mouse {0}"));
            Assert.True(entries.ContainsKey("input\u0004{0} (Pad {1})"));
            Assert.All(entries.Keys, key => Assert.StartsWith("input\u0004", key, StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(work))
                Directory.Delete(work, recursive: true);
        }
    }
}
