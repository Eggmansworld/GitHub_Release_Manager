namespace EggmansReleaseManager.Models;

public sealed class GhRelease
{
    public string? TagName { get; set; }
    public string? Name { get; set; }
    public string? Body { get; set; }
    public string? PublishedAt { get; set; }
    public bool Draft { get; set; }
    public bool Prerelease { get; set; }
    public List<GhAsset> Assets { get; set; } = [];

    public string FolderTag => !string.IsNullOrWhiteSpace(TagName) ? TagName!
                             : !string.IsNullOrWhiteSpace(Name) ? Name! : "untagged";
}

public sealed class GhAsset
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string BrowserDownloadUrl { get; set; } = "";
    public string? ContentType { get; set; }
    public string? Label { get; set; }

    /// <summary>Generated archives (source code, branch snapshots) have no size until they're downloaded.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool SizeUnknown { get; set; }
}

public sealed record RateLimitInfo(int Remaining, int Limit, DateTimeOffset? Reset);
