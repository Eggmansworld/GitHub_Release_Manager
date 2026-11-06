using CommunityToolkit.Mvvm.ComponentModel;
using EggmansReleaseManager.Services;

namespace EggmansReleaseManager.ViewModels;

public enum RepoStatus { Unchecked, UpToDate, Changed, Failed }

public sealed class RepoRow
{
    public required string Key { get; init; }
    public string LastChecked { get; init; } = "";
    public string Files { get; init; } = "";
    public string Size { get; init; } = "";
    public string LastResult { get; init; } = "";
    public RepoStatus Status { get; init; }
    public bool HasFilter { get; init; }

    // numeric sort keys for the grid (the display strings above are formatted)
    public int FileCount { get; init; }
    public long SizeBytes { get; init; }

    public string Glyph => Status switch
    {
        RepoStatus.UpToDate => "■",
        RepoStatus.Changed => "▲",
        RepoStatus.Failed => "×",
        _ => "·",
    };

    public bool IsOk => Status == RepoStatus.UpToDate;
    public bool IsChanged => Status == RepoStatus.Changed;
    public bool IsFailed => Status == RepoStatus.Failed;
    public bool IsUnchecked => Status == RepoStatus.Unchecked;
    public string FilterGlyph => HasFilter ? "ƒ" : "";

    /// <summary>Same colour rules as v3: Up to date = green, OK&gt;0 = orange/changed, Failed&gt;0 = red.</summary>
    public static RepoStatus StatusFrom(string? lastResult)
    {
        var lr = lastResult ?? "";
        if (lr.StartsWith("Error", StringComparison.OrdinalIgnoreCase)) return RepoStatus.Failed;
        if (lr.Contains("Up to date", StringComparison.OrdinalIgnoreCase)) return RepoStatus.UpToDate;
        var failed = ParseCount(lr, "Failed=");
        if (failed is > 0) return RepoStatus.Failed;
        var ok = ParseCount(lr, "OK=");
        if (ok is > 0) return RepoStatus.Changed;
        if (failed is not null || ok is not null) return RepoStatus.UpToDate;
        return RepoStatus.Unchecked;
    }

    private static int? ParseCount(string s, string key)
    {
        var i = s.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var digits = new string(s[(i + key.Length)..].TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : null;
    }
}

public sealed partial class TransferItem : ObservableObject
{
    public required DownloadJob Job { get; init; }
    public string Name => Job.Asset.Name;
    public string Tag => Job.Tag;
    public long Total => Job.Asset.Size;

    [ObservableProperty] private long _downloaded;
    [ObservableProperty] private string _bar = Fmt.Bar(0, 24);
    [ObservableProperty] private string _pct = "  0%";
    [ObservableProperty] private string _speed = "";
    [ObservableProperty] private string _eta = "";

    public void Update(long downloaded, double speed, double eta)
    {
        Downloaded = downloaded;
        var f = Total > 0 ? downloaded / (double)Total : 0;
        Bar = Fmt.Bar(f, 24);
        Pct = $"{f * 100,3:0}%";
        Speed = speed > 0 ? Fmt.Speed(speed) : "hashing";
        Eta = speed > 0 ? "ETA " + Fmt.Eta(eta) : "";
    }
}

public enum LogKind { Info, Ok, Warn, Err, Sys }

public sealed class LogLine
{
    public required string Time { get; init; }
    public required string Text { get; init; }
    public LogKind Kind { get; init; }

    public string Prefix => Kind switch
    {
        LogKind.Err => "!!!",
        LogKind.Ok => "+++",
        LogKind.Warn => "-!-",
        LogKind.Sys => ">>>",
        _ => "***",
    };

    public bool IsErr => Kind == LogKind.Err;
    public bool IsOk => Kind == LogKind.Ok;
    public bool IsWarn => Kind == LogKind.Warn;
    public bool IsSys => Kind == LogKind.Sys;

    /// <summary>Colour by content, like v3's log().</summary>
    public static LogKind Classify(string msg)
    {
        var lo = msg.ToLowerInvariant();
        if (new[] { "error", "failed=1", "fail", "exception" }.Any(lo.Contains) && !lo.Contains("failed=0")) return LogKind.Err;
        if (new[] { "finished", "complete", "success", "ok=", "saved", "generated", "added", "up to date" }.Any(lo.Contains)) return LogKind.Ok;
        if (new[] { "warn", "missing", "skip", "cancel", "stop", "abort", "filtered", "pause" }.Any(lo.Contains)) return LogKind.Warn;
        return LogKind.Info;
    }
}
