using System.Buffers;
using MainframeEngine.EditorLink;

namespace MainframeEngine.Tests.Crash;

public sealed class CrashFormatTests
{
    [Theory]
    [InlineData(134, "SIGABRT")]
    [InlineData(139, "SIGSEGV")]
    [InlineData(137, "SIGKILL")]
    [InlineData(132, "SIGILL")]
    public void UnixExitCodesAboveOneTwentyEightAreSignals(int code, string signal)
    {
        Assert.Equal(signal, ExitCodes.SignalName(code));
        Assert.StartsWith(signal, ExitCodes.Describe(code, windows: false), StringComparison.Ordinal);
        Assert.True(ExitCodes.IsAbnormal(code, windows: false));
    }

    [Fact]
    public void OrdinaryExitCodesStayCodes()
    {
        Assert.Equal("exit code 1", ExitCodes.Describe(1, windows: false));
        Assert.Null(ExitCodes.SignalName(1));
        Assert.False(ExitCodes.IsAbnormal(1, windows: false));
        Assert.False(ExitCodes.IsAbnormal(143, windows: false)); // SIGTERM: asked to stop
        Assert.Equal("exit code 0", ExitCodes.Describe(0, windows: true));
    }

    [Theory]
    [InlineData(0xC0000005u, "access violation (0xC0000005)")]
    [InlineData(0xC00000FDu, "stack overflow (0xC00000FD)")]
    [InlineData(0xE0434352u, "unhandled .NET exception (0xE0434352)")]
    [InlineData(0xC0001234u, "NTSTATUS 0xC0001234")]
    public void WindowsCodesAreNtStatusNames(uint status, string text)
    {
        Assert.Equal(text, ExitCodes.Describe(unchecked((int)status), windows: true));
        Assert.True(ExitCodes.IsAbnormal(unchecked((int)status), windows: true));
    }

    [Fact]
    public void DriverVersionsUseTheVendorsPacking()
    {
        // NVIDIA 537.58: 10.8.8.6 bits.
        var nvidia = (537u << 22) | (58u << 14);
        Assert.Equal("537.58.0.0", GpuInfo.DecodeDriverVersion(GpuInfo.VendorNvidia, nvidia, windows: true));
        // Intel on Windows: 18.14 bits (101.5333).
        Assert.Equal("101.5333", GpuInfo.DecodeDriverVersion(GpuInfo.VendorIntel, (101u << 14) | 5333u, windows: true));
        // Everyone else (and Intel on Linux): Vulkan's 10.10.12.
        var standard = (0u << 22) | (2u << 12) | 2208u;
        Assert.Equal("0.2.2208", GpuInfo.DecodeDriverVersion(GpuInfo.VendorApple, standard, windows: false));
        Assert.Equal("0.2.2208", GpuInfo.DecodeDriverVersion(GpuInfo.VendorIntel, standard, windows: false));
        Assert.Equal("1.2.296", GpuInfo.DecodeApiVersion((1u << 22) | (2u << 12) | 296u));
    }

    [Theory]
    [InlineData("0.42.0+92dbb40a1234567890", "92dbb40a")]
    [InlineData("1.0.0-preview+build.5.abcdef12", "abcdef12")]
    [InlineData("0.0.0-dev", null)]
    [InlineData("0.0.0-dev+notasha", null)]
    [InlineData(null, null)]
    public void TheCommitComesFromTheInformationalVersion(string? informational, string? commit) =>
        Assert.Equal(commit, EngineInfo.ReadCommit(informational));

    [Fact]
    public void TheCrashSectionRoundTripsAndDefaultsWriteNothing()
    {
        var settings = new ProjectSettings { Name = "Crashy" };
        Assert.DoesNotContain("\"crash\"", System.Text.Encoding.UTF8.GetString(settings.ToJson()), StringComparison.Ordinal);

        settings.Crash.Prompt = CrashPromptMode.None;
        settings.Crash.LogEntries = 500;
        settings.Crash.MaxReports = 3;
        settings.Crash.GpuBreadcrumbs = false;
        settings.Crash.NativeDumps = NativeDumpMode.Triage;
        settings.Crash.Upload.Url = "https://key@sentry.example.com/42";
        settings.Crash.Upload.Mode = CrashUploadMode.Consented;
        settings.Crash.Upload.Format = CrashUploadFormat.Zip;
        settings.Crash.Upload.IncludeLog = false;
        settings.Crash.Upload.IncludeDumps = true;

        var path = Path.Combine(Path.GetTempPath(), "mf-crash-settings-" + Guid.NewGuid().ToString("N") + ".mfproj");
        try
        {
            File.WriteAllBytes(path, settings.ToJson());
            var back = ProjectSettings.Load(path).Crash;
            Assert.Equal(CrashPromptMode.None, back.Prompt);
            Assert.Equal(500, back.LogEntries);
            Assert.Equal(3, back.MaxReports);
            Assert.False(back.GpuBreadcrumbs);
            Assert.Equal(NativeDumpMode.Triage, back.NativeDumps);
            Assert.Equal("https://key@sentry.example.com/42", back.Upload.Url);
            Assert.Equal(CrashUploadMode.Consented, back.Upload.Mode);
            Assert.Equal(CrashUploadFormat.Zip, back.Upload.Format);
            Assert.False(back.Upload.IncludeLog);
            Assert.True(back.Upload.IncludeDumps);
            Assert.False(settings.ToEngineOptions().GpuBreadcrumbs);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ABadCrashValueNamesItsKey()
    {
        var path = Path.Combine(Path.GetTempPath(), "mf-crash-bad-" + Guid.NewGuid().ToString("N") + ".mfproj");
        try
        {
            File.WriteAllText(path, """{ "format": 3, "name": "X", "crash": { "nativeDumps": "always" } }""");
            var error = Assert.Throws<InvalidDataException>(() => ProjectSettings.Load(path));
            Assert.Contains("crash.nativeDumps", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheSettingsSnapshotLeavesTheUploadEndpointOut()
    {
        var settings = new ProjectSettings { Name = "Snap" };
        settings.Crash.Upload.Url = "https://secret-key@sentry.example.com/1";
        settings.Crash.MaxReports = 4;

        var json = System.Text.Encoding.UTF8.GetString(GameHost.CrashSettingsSnapshot(settings, null, null));

        Assert.DoesNotContain("secret-key", json, StringComparison.Ordinal);
        Assert.Contains("\"maxReports\": 4", json, StringComparison.Ordinal);
    }

    [Fact]
    public void UploadConsentPersistsInTheUserSettings()
    {
        var user = new UserSettings(new ConfigFile());
        Assert.False(user.CrashUploadConsent);
        user.CrashUploadConsent = true;
        Assert.True(user.CrashUploadConsent);
        user.Reset(UserSettingsSection.Game); // not a game section
        Assert.True(user.CrashUploadConsent);
        user.CrashUploadConsent = false;
        Assert.False(user.Config.HasSection("crash") && user.Config.HasSectionKey("crash", "uploadConsent"));
    }

    [Fact]
    public void TheLinkCarriesACrashReportFrame()
    {
        var buffer = new ArrayBufferWriter<byte>();
        Assert.True(EditorLinkProtocol.WriteCrashReport(buffer, new EditorLinkCrashReport("{\"format\":1}", "/tmp/crashes/x")));
        Assert.True(EditorLinkProtocol.TryReadHeader(buffer.WrittenSpan, out var length));
        Assert.True(EditorLinkProtocol.TryDecode(buffer.WrittenSpan.Slice(EditorLinkProtocol.HeaderLength, length), out var message));
        Assert.Equal(EditorLinkMessageType.CrashReport, message.Type);
        Assert.Equal("{\"format\":1}", message.CrashReport.Json);
        Assert.Equal("/tmp/crashes/x", message.CrashReport.Folder);
        Assert.Equal(5, EditorLinkProtocol.Version);

        // Too large for a frame: not written (cut JSON is useless; the editor reads the bundle instead).
        var big = new string('x', EditorLinkProtocol.MaxCrashReportBytes + 1);
        Assert.False(EditorLinkProtocol.WriteCrashReport(new ArrayBufferWriter<byte>(), new EditorLinkCrashReport(big, "")));
    }
}
