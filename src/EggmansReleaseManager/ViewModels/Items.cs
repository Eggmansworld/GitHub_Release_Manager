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
    [ObservableProperty] private string _bar = Fmt.Bar(0, 20);
    [ObservableProperty] private string _pct = "  0%";
    [ObservableProperty] private string _speed = "";
    [ObservableProperty] private string _eta = "";

    public void Update(long downloaded, double speed, double eta)
    {
        Downloaded = downloaded;
        var f = Total > 0 ? downloaded / (double)Total : 0;
        Bar = Fmt.Bar(f, 20);
        Pct = $"{f * 100,3:0}%";
        Speed = speed > 0 ? Fmt.Speed(speed) : "hashing";
        Eta = speed > 0 ? "ETA " + Fmt.Eta(eta) : "";
    }
}

public enum LogKind { Info, Ok, Warn, Err, Sys, Bot }

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
        LogKind.Bot => "<@>",
        _ => "***",
    };

    public bool IsErr => Kind == LogKind.Err;
    public bool IsOk => Kind == LogKind.Ok;
    public bool IsWarn => Kind == LogKind.Warn;
    public bool IsSys => Kind == LogKind.Sys;
    public bool IsBot => Kind == LogKind.Bot;

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

public enum RaceState { Racing, Complete, Incomplete, Stopped }

/// <summary>One release (repo + tag) being raced in: the zipscript line in the DCC panel.</summary>
public sealed partial class RaceItem : ObservableObject
{
    public required string Repo { get; init; }
    public required string Tag { get; init; }
    public required int TotalFiles { get; init; }
    public required long TotalBytes { get; init; }
    public bool IsNew { get; init; }
    public DateTime Started { get; } = DateTime.UtcNow;

    public int DoneFiles { get; private set; }
    public int FailedFiles { get; private set; }
    public long DoneBytes { get; private set; }
    public TimeSpan Elapsed { get; private set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsRacing), nameof(IsComplete), nameof(IsBad))]
    private RaceState _state = RaceState.Racing;
    [ObservableProperty] private string _line = "";
    [ObservableProperty] private string _detail = "";

    public bool IsRacing => State == RaceState.Racing;
    public bool IsComplete => State == RaceState.Complete;
    public bool IsBad => State is RaceState.Incomplete or RaceState.Stopped;
    public bool IsFinishedCount => DoneFiles + FailedFiles >= TotalFiles;

    public void Add(bool ok, long bytes)
    {
        if (ok) { DoneFiles++; DoneBytes += bytes; } else FailedFiles++;
        Refresh();
    }

    public void Finish(RaceState state)
    {
        Elapsed = DateTime.UtcNow - Started;
        State = state;
        Refresh();
    }

    public string Speed => Elapsed.TotalSeconds > 0.5 ? Fmt.Speed(DoneBytes / Elapsed.TotalSeconds) : "";

    public void Refresh()
    {
        var pct = TotalBytes > 0 ? DoneBytes * 100.0 / TotalBytes : TotalFiles > 0 ? DoneFiles * 100.0 / TotalFiles : 100;
        Line = State switch
        {
            RaceState.Complete => Zipscript.Complete(DoneBytes, DoneFiles),
            RaceState.Incomplete => Zipscript.Incomplete(DoneFiles, TotalFiles),
            RaceState.Stopped => Zipscript.Incomplete(DoneFiles, TotalFiles, "STOPPED"),
            _ => Zipscript.Racing(DoneFiles, TotalFiles, pct),
        };
        var took = State == RaceState.Racing ? "" : $" · {Fmt.Eta(Elapsed.TotalSeconds)}{(Speed is { Length: > 0 } sp ? " @ " + sp : "")}";
        Detail = $"  {Repo} {Tag}{took}{(FailedFiles > 0 ? $" · {FailedFiles} failed" : "")}";
    }
}
