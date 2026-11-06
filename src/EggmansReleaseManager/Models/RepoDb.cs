using System.Text.Json;
using System.Text.Json.Serialization;

namespace EggmansReleaseManager.Models;

/// <summary>Contents of Eggmans_GitHub_Release_Manager_repos.json (format unchanged from v3).</summary>
public sealed class RepoDb
{
    public Dictionary<string, RepoInfo> Repos { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public RepoInfo Ensure(string key)
    {
        if (!Repos.TryGetValue(key, out var info))
        {
            info = new RepoInfo();
            Repos[key] = info;
        }
        return info;
    }
}

public sealed class RepoInfo
{
    public string? LastChecked { get; set; }
    public string? LastResult { get; set; } = "";

    /// <summary>tag -> asset filename -> metadata</summary>
    public Dictionary<string, Dictionary<string, AssetMeta>> Assets { get; set; } = new();

    /// <summary>Assets skipped by the filters on the last check ("nukes").</summary>
    public int NukedFiles { get; set; }
    public long NukedBytes { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public int FileCount => Assets.Values.Sum(t => t.Count);

    [JsonIgnore]
    public long TotalBytes => Assets.Values.SelectMany(t => t.Values).Sum(m => m.SizeBytes);
}

public sealed class AssetMeta
{
    public long SizeBytes { get; set; }
    public string? Sha1 { get; set; }
    public string? Path { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DownloadedAt { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndexedAt { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HashRescannedAt { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Crc { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
