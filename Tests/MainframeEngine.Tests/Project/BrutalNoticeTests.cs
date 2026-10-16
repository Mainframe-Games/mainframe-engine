namespace MainframeEngine.Tests.Project;

/// <summary>
/// BRUTAL licence condition 2.3 (ADR 0192): BRUTAL's NOTICE.txt lies alongside every product. The engine project copies it
/// next to every app that references it (this test assembly is one), as BRUTAL-NOTICE.txt.
/// </summary>
public sealed class BrutalNoticeTests
{
    [Fact]
    public void BrutalsNoticeIsCopiedNextToTheAppAsBrutalNotice()
    {
        var notice = Path.Combine(AppContext.BaseDirectory, "BRUTAL-NOTICE.txt");
        Assert.True(File.Exists(notice), $"{notice} is missing: MainframeEngine.csproj copies BRUTAL's NOTICE.txt next to every app.");
        Assert.Contains("BRUTAL", File.ReadAllText(notice), StringComparison.Ordinal);
    }
}
