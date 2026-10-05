namespace MainframeEngine.Editor;

/// <summary>The editor's one <see cref="HttpClient"/> (update checks, update and demo downloads); callers set timeouts per request.</summary>
internal static class EditorHttp
{
    private static readonly Lazy<HttpClient> Client = new(() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    public static HttpClient Shared => Client.Value;
}
