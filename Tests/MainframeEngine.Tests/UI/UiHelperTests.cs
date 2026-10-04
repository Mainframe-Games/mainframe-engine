using System.Numerics;
using MainframeEngine.UI.Rml;
using Silk.NET.Input;

namespace MainframeEngine.Tests.UI;

/// <summary>Pure UI helpers: key and gamepad maps, hot-reload batching, premultiplication, blur parameters, paths, dp ratios.</summary>
public sealed class UiHelperTests
{
    [Theory]
    [InlineData(Key.A, RmlKey.A)]
    [InlineData(Key.Z, RmlKey.Z)]
    [InlineData(Key.Number0, RmlKey.D0)]
    [InlineData(Key.Number9, RmlKey.D9)]
    [InlineData(Key.Enter, RmlKey.Return)]
    [InlineData(Key.KeypadEnter, RmlKey.NumpadEnter)]
    [InlineData(Key.Backspace, RmlKey.Back)]
    [InlineData(Key.Tab, RmlKey.Tab)]
    [InlineData(Key.Escape, RmlKey.Escape)]
    [InlineData(Key.Left, RmlKey.Left)]
    [InlineData(Key.PageDown, RmlKey.Next)]
    [InlineData(Key.F1, RmlKey.F1)]
    [InlineData(Key.F12, RmlKey.F12)]
    [InlineData(Key.Keypad5, RmlKey.Numpad5)]
    [InlineData(Key.ShiftLeft, RmlKey.LeftShift)]
    [InlineData(Key.Unknown, RmlKey.Unknown)]
    public void KeysMapToRmlUiIdentifiers(Key key, RmlKey expected) => Assert.Equal(expected, UiInputMap.ToRmlKey(key));

    [Fact]
    public void EveryLetterAndDigitMaps()
    {
        for (var k = Key.A; k <= Key.Z; k++)
            Assert.Equal(RmlKey.A + (k - Key.A), UiInputMap.ToRmlKey(k));
        for (var k = Key.Number0; k <= Key.Number9; k++)
            Assert.Equal(RmlKey.D0 + (k - Key.Number0), UiInputMap.ToRmlKey(k));
    }

    [Fact]
    public void ModifierKeysToggleTheirFlag()
    {
        Assert.Equal(RmlKeyModifiers.Shift, UiInputMap.ModifierOf(Key.ShiftRight));
        Assert.Equal(RmlKeyModifiers.Ctrl, UiInputMap.ModifierOf(Key.ControlLeft));
        Assert.Equal(RmlKeyModifiers.Alt, UiInputMap.ModifierOf(Key.AltRight));
        Assert.Equal(RmlKeyModifiers.Meta, UiInputMap.ModifierOf(Key.SuperLeft));
        Assert.Equal(RmlKeyModifiers.None, UiInputMap.ModifierOf(Key.A));
    }

    [Theory]
    [InlineData(ButtonName.DPadUp, RmlKey.Up)]
    [InlineData(ButtonName.DPadDown, RmlKey.Down)]
    [InlineData(ButtonName.DPadLeft, RmlKey.Left)]
    [InlineData(ButtonName.DPadRight, RmlKey.Right)]
    [InlineData(ButtonName.A, RmlKey.Return)]
    [InlineData(ButtonName.B, RmlKey.Escape)]
    [InlineData(ButtonName.X, RmlKey.Unknown)]
    public void GamepadButtonsDriveNavigation(ButtonName button, RmlKey expected) =>
        Assert.Equal(expected, UiInputMap.ToNavigationKey(button));

    [Theory]
    [InlineData(0f, 0f, RmlKey.Unknown)]
    [InlineData(0.3f, 0.2f, RmlKey.Unknown)]
    [InlineData(0.9f, 0.1f, RmlKey.Right)]
    [InlineData(-0.9f, 0.5f, RmlKey.Left)]
    [InlineData(0.2f, 0.8f, RmlKey.Down)]
    [InlineData(0.1f, -0.7f, RmlKey.Up)]
    public void StickPicksTheDominantDirection(float x, float y, RmlKey expected) =>
        Assert.Equal(expected, UiInputMap.StickDirection(x, y, 0.5f));

    [Fact]
    public void MouseButtonsMapToRmlUiIndices()
    {
        Assert.Equal(0, UiInputMap.ToRmlButton(MouseButton.Left));
        Assert.Equal(1, UiInputMap.ToRmlButton(MouseButton.Right));
        Assert.Equal(2, UiInputMap.ToRmlButton(MouseButton.Middle));
        Assert.Equal(-1, UiInputMap.ToRmlButton(MouseButton.Unknown));
    }

    [Theory]
    [InlineData("ui/hud.rcss", UiReloadKind.StyleSheets)]
    [InlineData("ui/HUD.RML", UiReloadKind.Documents)]
    [InlineData("ui/icon.png", UiReloadKind.Textures)]
    [InlineData("ui/fonts/x.ttf", UiReloadKind.Textures)]
    [InlineData("ui/notes.txt", UiReloadKind.None)]
    public void ChangesAreClassified(string path, UiReloadKind expected) => Assert.Equal(expected, UiHotReload.Classify(path));

    [Fact]
    public void HotReloadDebouncesAndMergesABatch()
    {
        using var reload = new UiHotReload(TimeSpan.FromMilliseconds(40));
        Assert.False(reload.TryTake(out _));
        reload.Enqueue("a.rcss");
        reload.Enqueue("b.rml");
        reload.Enqueue("ignored.txt");
        Assert.False(reload.TryTake(out _)); // still within the debounce interval
        Thread.Sleep(80);
        Assert.True(reload.TryTake(out var kind));
        Assert.Equal(UiReloadKind.StyleSheets | UiReloadKind.Documents, kind);
        Assert.False(reload.TryTake(out _));
    }

    [Fact]
    public void HotReloadWatcherSeesFileChanges()
    {
        var dir = Directory.CreateTempSubdirectory("mf-ui-watch").FullName;
        try
        {
            using var reload = new UiHotReload(TimeSpan.FromMilliseconds(20));
            reload.Watch(dir);
            reload.Watch(dir); // once
            Assert.Single(reload.Directories);
            File.WriteAllText(Path.Combine(dir, "x.rcss"), "p {}");

            var kind = UiReloadKind.None;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !reload.TryTake(out kind))
                Thread.Sleep(20);
            Assert.Equal(UiReloadKind.StyleSheets, kind);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PremultiplyScalesColourByAlpha()
    {
        byte[] rgba = [255, 128, 0, 128, 10, 20, 30, 255, 200, 200, 200, 0];
        VulkanUiRenderer.Premultiply(rgba);
        Assert.Equal([128, 64, 0, 128, 10, 20, 30, 255, 0, 0, 0, 0], rgba);
    }

    [Fact]
    public void StraightRgbaBecomesPremultipliedFloats()
    {
        var c = VulkanUiRenderer.PremultipliedColor(0x80_00_80_FFu); // R 255, G 128, B 0, A 128
        Assert.Equal(128f / 255f, c.W, 5);
        Assert.Equal(1f * c.W, c.X, 5);
        Assert.Equal(128f / 255f * c.W, c.Y, 5);
        Assert.Equal(0f, c.Z);
    }

    [Theory]
    [InlineData(1f, 0, 1f)]
    [InlineData(2.9f, 0, 2.9f)]
    [InlineData(3f, 1, 1.5f)]
    [InlineData(12f, 3, 1.5f)]
    [InlineData(0f, 0, 0f)]
    public void LargeBlursDownscaleFirst(float sigma, int passes, float perPass)
    {
        VulkanUiRenderer.SigmaToParameters(sigma, out var level, out var remaining);
        Assert.Equal(passes, level);
        Assert.Equal(perPass, remaining, 4);
    }

    [Fact]
    public void BlurWeightsAreNormalised()
    {
        foreach (var sigma in new[] { 0f, 0.5f, 1f, 3f })
        {
            var w = VulkanUiRenderer.BlurWeights(sigma);
            Assert.Equal(1f, w.X + 2 * (w.Y + w.Z + w.W), 4);
            Assert.True(w.X >= w.Y && w.Y >= w.Z && w.Z >= w.W);
        }

        Assert.Equal(new Vector4(1, 0, 0, 0), VulkanUiRenderer.BlurWeights(0f)); // no blur: a single tap
    }

    [Fact]
    public void SourceDirectoriesOverrideTheContentFolder()
    {
        var dir = Directory.CreateTempSubdirectory("mf-ui-src").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "UI"));
            File.WriteAllText(Path.Combine(dir, "UI", "hud.rml"), "<rml/>");
            var files = new UiFileInterface();
            files.AddSourceDirectory(dir);
            files.AddSourceDirectory(dir); // once
            Assert.Single(files.SourceDirectories);

            Assert.Equal(Path.Combine(dir, "UI", "hud.rml"), files.ResolvePath("Content/UI/hud.rml"));
            Assert.Equal(Path.Combine(dir, "UI", "hud.rml"), files.ResolvePath("UI/hud.rml"));
            Assert.Null(files.ResolvePath("Content/UI/missing.rml"));
            Assert.NotNull(files.ResolvePath("Content/UI/fonts/LatoLatin-Regular.ttf")); // falls back to the output's Content
            Assert.NotNull(files.ResolveDirectory("Content/UI/fonts"));
            using var stream = files.Open("Content/UI/hud.rml");
            Assert.NotNull(stream);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("Content/UI/x.rml", "UI/x.rml")]
    [InlineData("Content\\UI\\x.rml", "UI/x.rml")]
    [InlineData("UI/x.rml", "UI/x.rml")]
    public void ContentPrefixIsStripped(string path, string expected) => Assert.Equal(expected, UiFileInterface.ContentRelative(path));

    [Fact]
    public void LayerDpRatioFollowsTheScaleMode()
    {
        var layer = new UiLayer();
        Assert.Equal(2f, layer.ComputeDpRatio(new Vector2(2560, 1440), 2f)); // Dpi (default): pixels per point
        layer.ScaleMode = UiScaleMode.Pixels;
        Assert.Equal(1f, layer.ComputeDpRatio(new Vector2(2560, 1440), 2f));
        layer.ScaleMode = UiScaleMode.ReferenceResolution;
        layer.ReferenceResolution = new Vector2(1280, 720);
        Assert.Equal(2f, layer.ComputeDpRatio(new Vector2(2560, 1440), 1f));
        Assert.Equal(1.5f, layer.ComputeDpRatio(new Vector2(2560, 1080), 1f)); // the smaller axis wins
        layer.Free();
    }

    [Theory]
    [InlineData("pointer", StandardCursor.Hand)]
    [InlineData("text", StandardCursor.IBeam)]
    [InlineData("move", StandardCursor.ResizeAll)]
    [InlineData("ew-resize", StandardCursor.HResize)]
    [InlineData("not-allowed", StandardCursor.NotAllowed)]
    [InlineData("", StandardCursor.Default)]
    [InlineData("whatever", StandardCursor.Default)]
    public void CursorNamesMapToSystemCursors(string name, StandardCursor expected) =>
        Assert.Equal(expected, UiSystemInterface.MapCursor(System.Text.Encoding.UTF8.GetBytes(name)));
}
