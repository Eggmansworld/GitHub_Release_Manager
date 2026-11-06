using System.Text.Json;
using System.Text.Json.Serialization;

namespace EggmansReleaseManager.Models;

/// <summary>
/// Contents of Eggmans_GitHub_Release_Manager_config.json.
/// The first block of properties is identical to the v3 Python config so old files load unchanged.
/// </summary>
public sealed class AppConfig
{
    // ---- v3 (Python) keys ----
    public string Repo { get; set; } = "";
    public string RootFolder { get; set; } = "";
    public int MaxThreads { get; set; } = 5;
    public bool SkipExisting { get; set; } = true;
    public string DiscordWebhook { get; set; } = "";
    public string GithubToken { get; set; } = "";
    public bool AutoUpdateEnabled { get; set; }
    public int AutoUpdateInterval { get; set; } = 60;

    // ---- v4 additions ----
    public string Theme { get; set; } = "ansi";
    public string FontFamily { get; set; } = "Cascadia Mono, Consolas, Lucida Console, Courier New";
    public double FontSize { get; set; } = 13;
    public bool CrtScanlines { get; set; } = true;
    public bool Scroller { get; set; } = true;
    public bool ModemIntro { get; set; } = true;

    /// <summary>Your BBS handle (max 30 chars, like WWIV). Empty = Windows user name.</summary>
    public string Username { get; set; } = "";
    /// <summary>ZMODEM beep when downloads finish, NO CARRIER hang-up on exit.</summary>
    public bool SoundEffects { get; set; } = true;
    /// <summary>Write a FILE_ID.DIZ into each release folder.</summary>
    public bool WriteDiz { get; set; } = true;
    /// <summary>Run the UI text through the 31337 translator.</summary>
    public bool LeetSpeak { get; set; }
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 860;

    /// <summary>Tracked Repos column widths in px, by column header id (user-resized).</summary>
    public Dictionary<string, double> RepoColumnWidths { get; set; } = new();

    public FilterConfig Filters { get; set; } = new();

    /// <summary>Custom cracktro scroller messages, one per entry. Null = the built-in greetz.</summary>
    public List<string>? ScrollerText { get; set; }

    /// <summary>Lifetime transfer counters for the 1:3 ratio box. Null until first run of v4.1.</summary>
    public UserStats? Stats { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class UserStats
{
    /// <summary>Bytes pulled down from GitHub, re-downloads included.</summary>
    public long DownloadedBytes { get; set; }
    public long DownloadedFiles { get; set; }
    public string Since { get; set; } = "";
}

public sealed class FilterConfig
{
    /// <summary>Rules applied to every repo (unless a repo opts out).</summary>
    public FilterRule Global { get; set; } = new();

    /// <summary>Per-repo rules keyed by "owner/repo".</summary>
    public Dictionary<string, RepoFilterRule> Repos { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class FilterRule
{
    /// <summary>Platform/kind tag ids (see PlatformDetector) whose assets are skipped.</summary>
    public List<string> ExcludePlatforms { get; set; } = [];

    /// <summary>Filename patterns to skip. Glob (*, ?) or "re:" + regex. Case-insensitive.</summary>
    public List<string> ExcludePatterns { get; set; } = [];

    /// <summary>Filename patterns that are always downloaded, overriding every exclude.</summary>
    public List<string> IncludePatterns { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty => ExcludePlatforms.Count == 0 && ExcludePatterns.Count == 0 && IncludePatterns.Count == 0;
}

public sealed class RepoFilterRule : FilterRule
{
    /// <summary>When true the global rule is merged with this one; when false only this rule applies.</summary>
    public bool UseGlobal { get; set; } = true;
}
