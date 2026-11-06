using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EggmansReleaseManager.Models;

namespace EggmansReleaseManager.Services;

public sealed class GitHubException(string message) : Exception(message);

public static class GitHubApi
{
    public const string UserAgent = "Eggman-GitHub-ReleaseManager/4.1";

    /// <summary>Shared client for API calls and downloads (redirects to the CDN are followed).</summary>
    public static readonly HttpClient Http = CreateClient();

    public static event Action<RateLimitInfo>? RateLimitChanged;
    public static RateLimitInfo? LastRateLimit { get; private set; }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 32,
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    /// <summary>Accepts "owner/repo" or any github.com URL and returns "owner/repo", or null.</summary>
    public static string? NormalizeRepo(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        var idx = t.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) t = t[(idx + "github.com/".Length)..];
        var parts = t.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var repo = parts[1];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        return $"{parts[0]}/{repo}";
    }

    public static async Task<List<GhRelease>> GetAllReleasesAsync(string repoKey, string? token, CancellationToken ct, int maxPages = int.MaxValue)
    {
        var releases = new List<GhRelease>();
        for (var page = 1; page <= maxPages; page++)
        {
            var url = $"https://api.github.com/repos/{repoKey}/releases?per_page=100&page={page}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("token", token.Trim());

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            CaptureRateLimit(resp);

            if (!resp.IsSuccessStatusCode)
                throw new GitHubException(await DescribeErrorAsync(resp).ConfigureAwait(false));

            var batch = await resp.Content.ReadFromJsonAsync<List<GhRelease>>(Json.Options, cts.Token).ConfigureAwait(false) ?? [];
            if (batch.Count == 0) break;
            releases.AddRange(batch);
            if (batch.Count < 100) break; // last page; saves one API call per repo vs v3
        }
        return releases;
    }

    private static void CaptureRateLimit(HttpResponseMessage resp)
    {
        static int? H(HttpResponseMessage r, string name) =>
            r.Headers.TryGetValues(name, out var v) && int.TryParse(v.FirstOrDefault(), out var i) ? i : null;

        var remaining = H(resp, "X-RateLimit-Remaining");
        var limit = H(resp, "X-RateLimit-Limit");
        if (remaining is null || limit is null) return;
        var resetEpoch = H(resp, "X-RateLimit-Reset");
        DateTimeOffset? reset = resetEpoch is null ? null : DateTimeOffset.FromUnixTimeSeconds(resetEpoch.Value);
        LastRateLimit = new RateLimitInfo(remaining.Value, limit.Value, reset);
        RateLimitChanged?.Invoke(LastRateLimit);
    }

    private static async Task<string> DescribeErrorAsync(HttpResponseMessage resp)
    {
        string? apiMsg = null;
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("message", out var m)) apiMsg = m.GetString();
        }
        catch { /* not json */ }

        var code = (int)resp.StatusCode;
        if ((code == 403 || code == 429) && LastRateLimit is { Remaining: 0 } rl)
        {
            var reset = rl.Reset?.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) ?? "later";
            return $"GitHub API rate limit hit ({rl.Limit}/hr). Resets at {reset}. Add a token in SETUP for 5000/hr.";
        }
        if (code == 404) return "Repo not found (404) - check owner/repo spelling, or it may be private.";
        if (code == 401) return "GitHub rejected the token (401) - check it in SETUP.";
        return $"HTTP {code} {resp.ReasonPhrase}{(apiMsg is null ? "" : $" - {apiMsg}")}";
    }

    public static async Task SendDiscordAsync(string webhook, string content)
    {
        if (string.IsNullOrWhiteSpace(webhook)) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Http.PostAsJsonAsync(webhook.Trim(), new { content }, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // fail silently, like v3
        }
    }
}
