using System.Net.Http.Headers;
using System.Text.Json;

namespace MkvHelper;

/// <summary>
/// Fetches the latest stable release tag from GitHub for Netflix/vmaf.
///
/// Uses the GitHub REST API (unauthenticated — 60 requests/hour limit per IP,
/// which is plenty for a tool that checks once per `dependency update`).
/// Filters out pre-releases and drafts; pre-releases on the vmaf repo
/// tend to be release candidates that aren't intended for downstream use.
/// </summary>
public static class ReleaseFetcher
{
    private const string RepoApi = "https://api.github.com/repos/Netflix/vmaf/releases/latest";

    public sealed record LatestRelease(string Tag, string Name, DateTime PublishedUtc, string HtmlUrl);

    public static async Task<LatestRelease> GetLatestAsync(CancellationToken ct)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("mkvhelper", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.Timeout = TimeSpan.FromSeconds(20);

        using var resp = await http.GetAsync(RepoApi, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"GitHub API returned {(int)resp.StatusCode} {resp.ReasonPhrase} for {RepoApi}.");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString()
            ?? throw new InvalidOperationException("GitHub response missing tag_name.");
        string name = root.TryGetProperty("name", out var n) ? (n.GetString() ?? tag) : tag;
        DateTime published = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetDateTime() : DateTime.UtcNow;
        string url = root.TryGetProperty("html_url", out var u) ? (u.GetString() ?? "") : "";

        return new LatestRelease(tag, name, published, url);
    }
}
