using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MainframeEngine.Editor;

/// <summary>
/// Reads the latest editor release from GitHub (<see cref="LatestReleaseUri"/>; public repository, no token; drafts and
/// pre-releases are never "latest"). Errors surface as <see cref="UpdateException"/>; cancellation by the caller does not.
/// </summary>
public sealed class ReleaseFeed(HttpClient http, string editorVersion)
{
    public static readonly Uri LatestReleaseUri = new("https://api.github.com/repos/Mainframe-Games/mainframe-engine/releases/latest");

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly SearchValues<char> Hex = SearchValues.Create("0123456789abcdefABCDEF");

    /// <summary>The latest release, or null when its tag is not <c>vX.Y.Z</c>.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MainframeEngine-Editor", editorVersion));
        try
        {
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new UpdateException("GitHub's rate limit for update checks was reached; try again in an hour.");
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"GitHub answered {(int)response.StatusCode} ({response.ReasonPhrase}).");
            var json = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            return Parse(json);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException($"GitHub did not answer within {Timeout.TotalSeconds:0} seconds.", e);
        }
        catch (HttpRequestException e)
        {
            throw new UpdateException($"Could not reach GitHub ({e.Message}).", e);
        }
    }

    /// <summary>Reads a <c>releases/latest</c> response; null when the tag is not <c>vX.Y.Z</c>.</summary>
    public static ReleaseInfo? Parse(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var tag = Required(root, "tag_name");
            if (tag[0] is not 'v' || !ReleaseVersion.TryParse(tag, out var version))
                return null;
            var assets = new List<ReleaseAsset>();
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? Sha256Of(d.GetString()) : null;
                assets.Add(new ReleaseAsset(Required(asset, "name"), new Uri(Required(asset, "browser_download_url")),
                    asset.GetProperty("size").GetInt64(), digest));
            }

            var notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "";
            return new ReleaseInfo(version, tag, notes, new Uri(Required(root, "html_url")), assets);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new UpdateException("GitHub's release information could not be read.", e);
        }
    }

    private static string Required(JsonElement element, string name) =>
        element.GetProperty(name).GetString() is { Length: > 0 } value ? value : throw new KeyNotFoundException(name);

    // "sha256:<64 hex>" → lower-case hex; anything else is no digest.
    private static string? Sha256Of(string? digest) =>
        digest is { Length: 71 } && digest.StartsWith("sha256:", StringComparison.Ordinal) && !digest.AsSpan(7).ContainsAnyExcept(Hex)
            ? digest[7..].ToLowerInvariant()
            : null;
}
