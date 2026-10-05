using System.Net;
using System.Text;

namespace MainframeEngine.Editor.Tests.Updates;

/// <summary>GitHub <c>releases/latest</c> responses: the real v1.0.0 answer (trimmed) and generated variants.</summary>
internal static class ReleaseFixtures
{
    public const string Recorded = """
        {
          "html_url": "https://github.com/Mainframe-Games/mainframe-engine/releases/tag/v1.0.0",
          "tag_name": "v1.0.0",
          "name": "Mainframe Engine v1.0.0",
          "draft": false,
          "prerelease": false,
          "assets": [
            { "name": "MainframeEngine-1.0.0-linux-x64.tar.gz", "content_type": "application/x-gtar", "size": 56920893,
              "digest": "sha256:dfdf31c3d560f72aa9850e71af5511fe6e03f63e0e5fb052010fe2b16fa2f89e",
              "browser_download_url": "https://github.com/Mainframe-Games/mainframe-engine/releases/download/v1.0.0/MainframeEngine-1.0.0-linux-x64.tar.gz" },
            { "name": "MainframeEngine-1.0.0-osx-arm64.tar.gz", "content_type": "application/x-gtar", "size": 56772908,
              "digest": "sha256:857EF126FFC089AFCC78405195C60550DED974703A418EC5E03EAF5C61AFE6DA",
              "browser_download_url": "https://github.com/Mainframe-Games/mainframe-engine/releases/download/v1.0.0/MainframeEngine-1.0.0-osx-arm64.tar.gz" },
            { "name": "MainframeEngine-1.0.0-win-x64.zip", "content_type": "application/zip", "size": 55036179,
              "digest": "md5:0123",
              "browser_download_url": "https://github.com/Mainframe-Games/mainframe-engine/releases/download/v1.0.0/MainframeEngine-1.0.0-win-x64.zip" }
          ],
          "body": "## What's Changed\n* M0–M10: engine foundation through editor"
        }
        """;

    public static string Latest(string tag = "v1.1.0", bool digests = true, params string[] rids)
    {
        rids = rids.Length > 0 ? rids : ["linux-x64", "osx-arm64", "win-x64"];
        var version = tag.TrimStart('v');
        var assets = string.Join(",\n", rids.Select(rid =>
        {
            var name = $"MainframeEngine-{version}-{rid}{(rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz")}";
            var digest = digests ? $"\"digest\": \"sha256:{new string('a', 64)}\"," : "";
            return $$"""{ "name": "{{name}}", "size": 1000, {{digest}} "browser_download_url": "https://github.com/Mainframe-Games/mainframe-engine/releases/download/{{tag}}/{{name}}" }""";
        }));
        return $$"""
            { "tag_name": "{{tag}}", "html_url": "https://github.com/Mainframe-Games/mainframe-engine/releases/tag/{{tag}}",
              "body": "Notes for {{tag}}", "assets": [ {{assets}} ] }
            """;
    }

    public static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);
}

/// <summary>An <see cref="HttpMessageHandler"/> that answers from a function and records requests.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : this((r, _) => Task.FromResult(respond(r))) { }

    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        return respond(request, cancellationToken);
    }

    public static StubHandler Bytes(byte[] body) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });

    public static StubHandler Json(string json) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    public static StubHandler Status(HttpStatusCode code) => new(_ => new HttpResponseMessage(code));

    public static StubHandler Throws(Exception exception) => new(_ => throw exception);
}
