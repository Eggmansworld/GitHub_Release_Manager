using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EggmansReleaseManager.Models;
using EggmansReleaseManager.Services;
using EggmansReleaseManager.UI;

namespace EggmansReleaseManager.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppConfig _cfg;
    private readonly IUiHost _ui;
    private RepoDb _db;

    private readonly DispatcherTimer _autoTimer = new();
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _dbSaveTimer;
    private DateTime? _nextAutoRun;

    // run state
    private CancellationTokenSource _soft = new();
    private CancellationTokenSource _hard = new();
    private readonly PauseGate _pause = new();
    private bool _stopRequested;
    private readonly ConcurrentQueue<DownloadResult> _results = new();
    private readonly Dictionary<DownloadJob, TransferItem> _transferMap = new();
    private RunState? _run;

    public string Version { get; } = "v" + (typeof(MainViewModel).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "4");
    public string Logo => AsciiArt.Logo;
    public string Tagline => AsciiArt.Tagline;
    public string ScrollerText => _cfg.LeetSpeak ? Leet.Apply(AsciiArt.ScrollerJoin(_cfg.ScrollerText)) : AsciiArt.ScrollerJoin(_cfg.ScrollerText);

    public ObservableCollection<RepoRow> Repos { get; } = [];
    public ObservableCollection<TransferItem> Transfers { get; } = [];
    public ObservableCollection<RaceItem> Races { get; } = [];
    public ObservableCollection<LogLine> Log { get; } = [];

    [ObservableProperty] private string _repoInput = "";
    [ObservableProperty] private string _rootFolder = "";
    [ObservableProperty] private bool _skipExisting;
    [ObservableProperty] private bool _autoUpdateEnabled;
    [ObservableProperty] private RepoRow? _selectedRepo;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle), nameof(RunGlyph))] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RunGlyph))] private bool _isDownloading;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PauseLabel), nameof(RunGlyph))] private bool _isPaused;

    [ObservableProperty] private string _statusText = "READY. Select a repo, then hit F5 - or F6 to update them all.";
    [ObservableProperty] private bool _isFlashing;
    [ObservableProperty] private string _progressBar = Fmt.Bar(0, 48);
    [ObservableProperty] private string _progressPct = "";
    [ObservableProperty] private string _countsText = "";

    [ObservableProperty] private string _statRepos = "0";
    [ObservableProperty] private string _statFiles = "0";
    [ObservableProperty] private string _statSize = "0 B";
    [ObservableProperty] private string _statApi = "--";
    [ObservableProperty] private string _statAuto = "OFF";
    [ObservableProperty] private string _statFilter = "none";
    [ObservableProperty] private string _clock = "";

    // 1:3 ratio box
    [ObservableProperty] private string _ratioUser = "";
    [ObservableProperty] private string _ratioNukes = "";
    public string RatioDisplay => Ratio.Display;
    [ObservableProperty] private string _ratioClass = "";
    [ObservableProperty] private string _ratioUp = "";
    [ObservableProperty] private string _ratioDown = "";
    [ObservableProperty] private string _ratioCredits = "";
    [ObservableProperty] private bool _ratioLeech;
    [ObservableProperty] private string _ratioBar = "";
    [ObservableProperty] private string _ratioNext = "";

    [ObservableProperty] private bool _showScanlines;
    [ObservableProperty] private bool _showScroller;

    public bool IsIdle => !IsBusy;
    public string PauseLabel => IsPaused ? "[F7] RESUME" : "[F7] PAUSE";
    public string RunGlyph => IsPaused ? "PAUSED" : IsDownloading ? "XFER" : IsBusy ? "BUSY" : "IDLE";
    public bool NoTransfers => Transfers.Count == 0;
    public bool HasRaces => Races.Count > 0;
    public bool ShowIdleDcc => Transfers.Count == 0 && Races.Count == 0;
    public bool HasFinishedRaces => Races.Any(r => !r.IsRacing);
    private const string Bot = "<EggBot> ";
    private bool _hungUp;
    private int _downloadedThisRun;

    private sealed class RunState
    {
        public required string Repo { get; init; }
        public required int Total { get; init; }
        public required long TotalBytes { get; init; }
        public int Completed;
        public long CompletedBytes;
        public readonly List<AssetRecord> Records = [];
        public int Ok => Records.Count(r => r.Success);
        public int Failed => Records.Count(r => !r.Success);
    }

    public MainViewModel(AppConfig cfg, IUiHost ui)
    {
        _cfg = cfg;
        _ui = ui;
        _db = Stores.LoadRepoDb();
        var merged = Stores.MergeTrackedList(_db);

        _repoInput = cfg.Repo;
        _rootFolder = string.IsNullOrWhiteSpace(cfg.RootFolder) ? Path.Combine(AppPaths.BaseDir, "downloads") : cfg.RootFolder;
        _skipExisting = cfg.SkipExisting;
        _autoUpdateEnabled = cfg.AutoUpdateEnabled;
        _showScanlines = cfg.CrtScanlines;
        _showScroller = cfg.Scroller;
        if (cfg.Stats is null)
        {
            // first run with the ratio box: count what's already archived as downloaded once
            cfg.Stats = new UserStats
            {
                DownloadedBytes = _db.Repos.Values.Sum(r => r.TotalBytes),
                DownloadedFiles = _db.Repos.Values.Sum(r => r.FileCount),
                Since = Stamp.Now(),
            };
            Stores.SaveConfig(cfg);
        }
        if (string.IsNullOrWhiteSpace(_repoInput)) _repoInput = _db.Repos.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault() ?? "";

        Transfers.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(NoTransfers)); OnPropertyChanged(nameof(ShowIdleDcc)); };
        Races.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasRaces)); OnPropertyChanged(nameof(ShowIdleDcc)); RaceStateChanged(); };
        _ratioUser = Handle.For(cfg);
        GitHubApi.RateLimitChanged += rl => Dispatcher.UIThread.Post(() =>
            StatApi = $"{rl.Remaining}/{rl.Limit}");

        _clockTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick());
        _clockTimer.Start();
        _dbSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _dbSaveTimer.Tick += (_, _) => { _dbSaveTimer.Stop(); SaveDb(); };
        _autoTimer.Tick += async (_, _) => await AutoUpdateTick();

        // Startup (the modem intro + welcome line are started by StartIntro once the window is up)
        if (merged > 0) AddLog($"Merged {merged} repo(s) from tracked_repos.txt", LogKind.Ok);
        try
        {
            var moved = Exporters.MigrateLegacyMetadata(RootFolder);
            if (moved > 0) AddLog($"Moved {moved} legacy metadata file(s) into _metadata", LogKind.Ok);
        }
        catch (Exception ex) { AddLog($"Warning: metadata migration: {ex.Message}", LogKind.Warn); }

        TryWriteTrackedList();
        RefreshRepos();
        ScheduleAutoUpdate();
        Tick();
    }

    // ═══════════════════════════ modem intro ═══════════════════════════

    private string WelcomeLine => $"Welcome to EGGNET node 1, {RatioUser} · {_db.Repos.Count} repos online · {Environment.MachineName}";

    /// <summary>Plays the synthesized dial-up handshake and prints the AT log lines in sync with it.</summary>
    public void StartIntro()
    {
        if (!_cfg.ModemIntro)
        {
            AddLog(WelcomeLine, LogKind.Sys);
            return;
        }
        var (wav, cues) = ModemSound.Build();
        var audible = ModemSound.Play(wav);
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            DispatcherTimer.RunOnce(() => AddLog(cue.Text, LogKind.Sys), audible ? cue.At + TimeSpan.FromMilliseconds(1) : TimeSpan.FromMilliseconds(i * 250 + 1));
        }
        var end = audible ? cues[^1].At + TimeSpan.FromMilliseconds(300) : TimeSpan.FromMilliseconds(cues.Count * 250 + 50);
        DispatcherTimer.RunOnce(() => AddLog(WelcomeLine, LogKind.Sys), end);
    }

    // ═══════════════════════════ property hooks ═══════════════════════════

    partial void OnRepoInputChanged(string value)
    {
        if (value.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var norm = GitHubApi.NormalizeRepo(value);
            if (norm is not null && norm != value)
                Dispatcher.UIThread.Post(() => RepoInput = norm);
        }
        // The text box is the source of truth; keep the list highlight in sync with it.
        var match = Repos.FirstOrDefault(r => string.Equals(r.Key, value.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!ReferenceEquals(match, SelectedRepo)) SelectedRepo = match;
    }

    partial void OnSelectedRepoChanged(RepoRow? value)
    {
        if (value is not null) RepoInput = value.Key;
    }

    partial void OnSkipExistingChanged(bool value) { _cfg.SkipExisting = value; SaveConfig(); }

    partial void OnAutoUpdateEnabledChanged(bool value)
    {
        _cfg.AutoUpdateEnabled = value;
        SaveConfig();
        ScheduleAutoUpdate();
        AddLog($"Auto-update {(value ? $"ENABLED every {_cfg.AutoUpdateInterval} min" : "disabled")}", LogKind.Info);
    }

    public void CommitRootFolder()
    {
        var f = RootFolder.Trim();
        if (f == _cfg.RootFolder) return;
        _cfg.RootFolder = f;
        SaveConfig();
        try { Exporters.MigrateLegacyMetadata(f); } catch { /* ignore */ }
        AddLog($"Root download folder -> {f}");
    }

    // ═══════════════════════════ logging / status ═══════════════════════════

    public void AddLog(string msg, LogKind? kind = null)
    {
        var line = new LogLine
        {
            Time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            Text = msg,
            Kind = kind ?? LogLine.Classify(msg),
        };
        Log.Add(line);
        while (Log.Count > 500) Log.RemoveAt(0);
        _ui.ScrollLogToEnd();
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            File.AppendAllText(Path.Combine(AppPaths.LogsDir, $"session_{DateTime.Now:yyyy-MM-dd}.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line.Prefix} {msg}{Environment.NewLine}");
        }
        catch { /* logging must never break the app */ }
    }

    private void Flash(string msg) { StatusText = msg; IsFlashing = true; }
    private void Status(string msg) { StatusText = msg; IsFlashing = false; }

    private void Tick()
    {
        Clock = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        StatAuto = AutoUpdateEnabled && _nextAutoRun is { } n
            ? $"{_cfg.AutoUpdateInterval}m → {n:HH:mm}"
            : "OFF";
    }

    // ═══════════════════════════ zipscript race board ═══════════════════════════

    private void FinishRace(RaceItem race, RaceState state)
    {
        race.Finish(state);
        var stats = $"{race.DoneFiles}F {Zipscript.Compact(race.DoneBytes)} in {Fmt.Eta(race.Elapsed.TotalSeconds)}{(race.Speed is { Length: > 0 } sp ? " @ " + sp : "")}";
        switch (state)
        {
            case RaceState.Complete:
                AddLog($"{Bot}[COMPLETE] {race.Repo} {race.Tag} :: {stats} :: raced by {RatioUser} -[COMPLETE]-", LogKind.Bot);
                break;
            case RaceState.Incomplete:
                AddLog($"{Bot}[INCOMPLETE] {race.Repo} {race.Tag} :: {race.DoneFiles} of {race.TotalFiles}F, {race.FailedFiles} failed", LogKind.Bot);
                break;
            case RaceState.Stopped:
                AddLog($"{Bot}[STOPPED] {race.Repo} {race.Tag} :: {race.DoneFiles} of {race.TotalFiles}F before the plug was pulled", LogKind.Bot);
                break;
        }
    }

    private void RaceStateChanged()
    {
        OnPropertyChanged(nameof(HasFinishedRaces));
        ClearRacesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Clears finished races from the board (ones still racing stay).</summary>
    [RelayCommand(CanExecute = nameof(HasFinishedRaces))]
    private void ClearRaces()
    {
        foreach (var r in Races.Where(r => !r.IsRacing).ToList()) Races.Remove(r);
    }

    // ═══════════════════════════ site wkup board ═══════════════════════════

    [RelayCommand]
    private Task TopUploaders() => _ui.ShowTopBoard(BuildTopBoard(DateTime.Now));

    public string BuildTopBoard(DateTime nowLocal)
    {
        const int inner = 63;
        var sb = new System.Text.StringBuilder();
        void Board(string title, DateTime? since)
        {
            var rows = TopBoard.Compute(_db, since);
            var sinceText = since is { } d ? "since " + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "all time";
            var head = $"-[ {title} ]";
            var tail = $"[ {sinceText} ]-";
            sb.Append('.').Append(head).Append(new string('-', inner - head.Length - tail.Length)).Append(tail).Append(".\n");
            sb.Append("| " + " ##  REPOSiTORY                            FiLES       SiZE".PadRight(inner - 2) + " |\n");
            if (rows.Count == 0) sb.Append("| " + "     nobody raced anything. lamers.".PadRight(inner - 2) + " |\n");
            for (var i = 0; i < rows.Count; i++)
            {
                var e = rows[i];
                var name = e.Repo.Length > 34 ? e.Repo[..33] + "~" : e.Repo;
                var row = $" {i + 1:00}  {name,-34} {e.Files,7:N0} {Fmt.BytesOrZero(e.Bytes),10}";
                sb.Append("| " + row.PadRight(inner - 2) + " |\n");
            }
            sb.Append((char)96).Append(new string('-', inner)).Append("'\n\n");
        }
        sb.Append($"  site stats :: {RatioUser} @ EGGNET :: class {RatioClass} :: ratio {Ratio.Display}\n\n");
        Board("WKUP", TopBoard.WeekStart(nowLocal));
        Board("MONTHUP", TopBoard.MonthStart(nowLocal));
        Board("ALLUP", null);
        return sb.ToString().TrimEnd();
    }

    // ═══════════════════════════ ratio ═══════════════════════════

    private void UpdateRatio()
    {
        var up = _db.Repos.Values.Sum(r => r.TotalBytes);
        var upFiles = _db.Repos.Values.Sum(r => r.FileCount);
        var st = _cfg.Stats ??= new UserStats { Since = Stamp.Now() };
        var credits = Ratio.Credits(up, st.DownloadedBytes);
        var (cls, next, progress) = Ratio.Rank(up);

        RatioClass = cls.Name;
        RatioUp = $"{Fmt.BytesOrZero(up)} ({upFiles.ToString("N0", CultureInfo.InvariantCulture)}f)";
        RatioDown = $"{Fmt.BytesOrZero(st.DownloadedBytes)} ({st.DownloadedFiles.ToString("N0", CultureInfo.InvariantCulture)}f)";
        RatioLeech = credits < 0;
        RatioCredits = credits < 0 ? $"-{Fmt.BytesOrZero(-credits)}  LEECH!" : Fmt.BytesOrZero(credits);
        RatioBar = Fmt.Bar(progress, 10);
        RatioNext = next is null ? "MAXED" : next.Name;
        var nf = _db.Repos.Values.Sum(r => r.NukedFiles);
        RatioNukes = nf == 0 ? "none" : $"{nf.ToString("N0", CultureInfo.InvariantCulture)}F ({Fmt.BytesOrZero(_db.Repos.Values.Sum(r => r.NukedBytes))})";
    }

    // ═══════════════════════════ persistence ═══════════════════════════

    private void SaveConfig()
    {
        try
        {
            _cfg.Repo = RepoInput.Trim();
            Stores.SaveConfig(_cfg);
        }
        catch (Exception ex) { AddLog($"ERROR saving config: {ex.Message}", LogKind.Err); }
    }

    private void SaveDb()
    {
        try { Stores.SaveRepoDb(_db); }
        catch (Exception ex) { AddLog($"ERROR saving repo DB: {ex.Message}", LogKind.Err); }
    }

    private void SaveDbSoon() { _dbSaveTimer.Stop(); _dbSaveTimer.Start(); }

    private void TryWriteTrackedList()
    {
        try { Stores.WriteTrackedList(_db); }
        catch (Exception ex) { AddLog($"ERROR writing tracked_repos.txt: {ex.Message}", LogKind.Err); }
    }

    /// <summary>NO CARRIER: plays the hang-up (blocking, ~0.5 s) once, on the way out.</summary>
    public void HangUp()
    {
        if (_hungUp) return;
        _hungUp = true;
        ModemSound.Stop();
        if (_cfg.SoundEffects) ModemSound.Play(ModemSound.NoCarrier(), wait: true);
    }

    public void OnShutdown()
    {
        _dbSaveTimer.Stop();
        SaveDb();
        SaveConfig();
        _hard.Cancel();
        ModemSound.Stop();
    }

    public void SaveWindowSize(double w, double h)
    {
        _cfg.WindowWidth = w;
        _cfg.WindowHeight = h;
    }

    public (double W, double H) WindowSize => (_cfg.WindowWidth, _cfg.WindowHeight);

    public Dictionary<string, double> RepoColumnWidths => _cfg.RepoColumnWidths;

    // ═══════════════════════════ dashboard ═══════════════════════════

    public void RefreshRepos()
    {
        var selected = RepoInput.Trim();
        Repos.Clear();
        foreach (var (key, info) in _db.Repos.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var files = info.FileCount;
            Repos.Add(new RepoRow
            {
                Key = key,
                LastChecked = Stamp.Pretty(info.LastChecked),
                Files = files > 0 ? files.ToString("N0", CultureInfo.InvariantCulture) : "",
                Size = Fmt.Bytes(info.TotalBytes),
                FileCount = files,
                SizeBytes = info.TotalBytes,
                LastResult = info.LastResult ?? "",
                Status = RepoRow.StatusFrom(info.LastResult),
                HasFilter = _cfg.Filters.Repos.Keys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)),
            });
        }
        SelectedRepo = Repos.FirstOrDefault(r => string.Equals(r.Key, selected, StringComparison.OrdinalIgnoreCase));

        StatRepos = _db.Repos.Count.ToString(CultureInfo.InvariantCulture);
        StatFiles = _db.Repos.Values.Sum(r => r.FileCount).ToString("N0", CultureInfo.InvariantCulture);
        StatSize = Fmt.Bytes(_db.Repos.Values.Sum(r => r.TotalBytes)) is { Length: > 0 } s ? s : "0 B";
        UpdateRatio();
        var g = _cfg.Filters.Global;
        var parts = new List<string>();
        if (g.ExcludePlatforms.Count > 0) parts.Add("-" + string.Join(" -", g.ExcludePlatforms));
        if (g.ExcludePatterns.Count > 0) parts.Add($"{g.ExcludePatterns.Count} pat");
        if (_cfg.Filters.Repos.Count > 0) parts.Add($"{_cfg.Filters.Repos.Count} repo");
        StatFilter = parts.Count == 0 ? "none" : string.Join(" ", parts);
    }

    private string? CurrentRepoKey()
    {
        var norm = GitHubApi.NormalizeRepo(RepoInput);
        return norm;
    }

    private async Task<bool> EnsureIdle()
    {
        if (!IsBusy) return true;
        await _ui.ShowMessage("BUSY", "Downloads already in progress.", MsgKind.Warn);
        return false;
    }

    private async Task<string?> RequireRoot()
    {
        CommitRootFolder();
        var root = RootFolder.Trim();
        if (string.IsNullOrEmpty(root))
        {
            await _ui.ShowMessage("ERROR", "Please specify a root download folder.", MsgKind.Error);
            return null;
        }
        try { Directory.CreateDirectory(root); }
        catch (Exception ex)
        {
            await _ui.ShowMessage("ERROR", $"Cannot create root folder:\n{ex.Message}", MsgKind.Error);
            return null;
        }
        return root;
    }

    // ═══════════════════════════ commands: repos ═══════════════════════════

    [RelayCommand]
    private async Task AddRepo()
    {
        var key = CurrentRepoKey();
        if (key is null)
        {
            await _ui.ShowMessage("INVALID", "Enter repo in owner/repo format (or paste a GitHub URL).", MsgKind.Error);
            return;
        }
        RepoInput = key;
        if (_db.Repos.ContainsKey(key))
        {
            await _ui.ShowMessage("EXISTS", $"Repo already tracked:\n{key}");
            return;
        }
        _db.Ensure(key);
        SaveDb();
        TryWriteTrackedList();
        RefreshRepos();
        SaveConfig();
        AddLog($"Added repo {key}", LogKind.Ok);
    }

    [RelayCommand]
    private async Task RemoveRepo()
    {
        var key = CurrentRepoKey();
        if (key is null || !_db.Repos.ContainsKey(key)) return;
        if (!await EnsureIdle()) return;
        if (!await _ui.Confirm("REMOVE REPO", $"Remove {key} from tracked list?\n(This will NOT delete any files.)")) return;
        _db.Repos.Remove(key);
        SaveDb();
        TryWriteTrackedList();
        RefreshRepos();
        AddLog($"Removed repo: {key}", LogKind.Warn);
    }

    [RelayCommand]
    private async Task UpdateSelected()
    {
        if (!await EnsureIdle()) return;
        var key = CurrentRepoKey();
        if (key is null)
        {
            await _ui.ShowMessage("ERROR", "Repository must be in the form owner/repo.", MsgKind.Error);
            return;
        }
        RepoInput = key;
        await RunUpdates([key], batch: false, auto: false);
    }

    [RelayCommand]
    private async Task UpdateAll()
    {
        if (!await EnsureIdle()) return;
        var list = OrderedForBatch();
        if (list.Count == 0)
        {
            await _ui.ShowMessage("NO REPOS", "No repos are tracked yet.");
            return;
        }
        if (!await _ui.Confirm("UPDATE ALL REPOS?", $"This will scan and update {list.Count} repos.\n\nProceed?")) return;
        await RunUpdates(list, batch: true, auto: false);
    }

    /// <summary>Oldest last_checked first, never-checked first of all (v3 order).</summary>
    private List<string> OrderedForBatch() =>
        _db.Repos.OrderBy(kv => Stamp.Parse(kv.Value.LastChecked) ?? DateTime.MinValue)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key).ToList();

    // ═══════════════════════════ auto update ═══════════════════════════

    private void ScheduleAutoUpdate()
    {
        _autoTimer.Stop();
        _nextAutoRun = null;
        if (AutoUpdateEnabled)
        {
            var minutes = Math.Max(5, _cfg.AutoUpdateInterval);
            _autoTimer.Interval = TimeSpan.FromMinutes(minutes);
            _nextAutoRun = DateTime.Now.AddMinutes(minutes);
            _autoTimer.Start();
        }
        Tick();
    }

    private async Task AutoUpdateTick()
    {
        ScheduleAutoUpdate();
        if (IsBusy) { AddLog("Auto-update skipped: busy", LogKind.Warn); return; }
        AddLog($"Auto-update triggered ({_cfg.AutoUpdateInterval} min).");
        await RunUpdates(OrderedForBatch(), batch: true, auto: true);
    }

    // ═══════════════════════════ the update pipeline ═══════════════════════════

    private async Task RunUpdates(List<string> repos, bool batch, bool auto)
    {
        var root = await RequireRoot();
        if (root is null) return;

        IsBusy = true;
        _stopRequested = false;
        _soft = new CancellationTokenSource();
        _hard = new CancellationTokenSource();
        _pause.Resume();
        IsPaused = false;
        var summary = new List<string>();
        _downloadedThisRun = 0;
        if (batch) AddLog($"Starting batch update for {repos.Count} repos...");

        try
        {
            foreach (var repo in repos)
            {
                if (_stopRequested) break;
                if (batch) AddLog($"Batch: updating {repo}...");
                var result = await UpdateOne(repo, root, batch);
                summary.Add($"{repo}: {result}");
            }
        }
        finally
        {
            IsBusy = false;
            IsDownloading = false;
            IsPaused = false;
            _pause.Resume();
            Transfers.Clear();
            _transferMap.Clear();
            _dbSaveTimer.Stop();
            SaveDb();
            RefreshRepos();
        }
        if (_downloadedThisRun > 0 && _cfg.SoundEffects) ModemSound.Play(ModemSound.ZmodemBeep());

        if (batch && !_stopRequested)
        {
            Status("Batch update complete.");
            ProgressBar = Fmt.Bar(0, 48);
            ProgressPct = "";
            AddLog("Batch update finished.", LogKind.Ok);
            if (!auto) await _ui.ShowMessage("BATCH UPDATE", "Batch update results:\n\n" + string.Join("\n", summary), MsgKind.Ok);
        }
    }

    private async Task<string> UpdateOne(string repo, string root, bool batch)
    {
        var info = _db.Ensure(repo);
        SaveDb();
        TryWriteTrackedList();
        RefreshRepos();
        SaveConfig();
        Flash($"{repo} :: PROCESSING REQUEST, PLEASE WAIT...");
        ProgressBar = Fmt.Bar(0, 48);
        ProgressPct = "";
        CountsText = "";

        // 1) ask GitHub
        List<GhRelease> releases;
        try
        {
            releases = await GitHubApi.GetAllReleasesAsync(repo, _cfg.GithubToken, _hard.Token);
        }
        catch (OperationCanceledException) when (_hard.IsCancellationRequested)
        {
            return "stopped";
        }
        catch (Exception ex)
        {
            var msg = ex is GitHubException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            AddLog($"Error fetching releases for {repo}: {msg}", LogKind.Err);
            info.LastResult = $"Error: {Trim(msg, 60)}";
            info.LastChecked = Stamp.Now();
            SaveDb();
            RefreshRepos();
            Status($"{repo}: error - {msg}");
            if (!batch) await _ui.ShowMessage("ERROR", $"Failed to fetch releases for {repo}:\n{msg}", MsgKind.Error);
            return "error";
        }

        // 2) plan (filters + skip rules), off the UI thread since it touches disk
        var filter = AssetFilter.For(_cfg.Filters, repo);
        var skip = SkipExisting;
        UpdatePlan plan;
        try
        {
            var diz = _cfg.WriteDiz ? RatioUser : null;
            plan = await Task.Run(() => RepoMaintenance.Plan(repo, releases, info, root, skip, filter, diz));
        }
        catch (Exception ex)
        {
            AddLog($"Error preparing {repo}: {ex.Message}", LogKind.Err);
            Status($"{repo}: error - {ex.Message}");
            return "error";
        }
        foreach (var w in plan.Warnings) AddLog($"Warning: {w}", LogKind.Warn);
        var filteredNote = plan.Filtered > 0 ? $" ({plan.Filtered} filtered)" : "";
        info.NukedFiles = plan.Nukes.Count;
        info.NukedBytes = plan.Nukes.Sum(n => n.Size);
        if (plan.Filtered > 0)
            AddLog($"{Bot}[NUKE] {repo} :: {plan.Filtered} of {plan.TotalSeen}F nuked ({Zipscript.Compact(info.NukedBytes)}) :: {Nuke.Summary(plan.Nukes)}", LogKind.Bot);

        if (plan.TotalSeen == 0)
        {
            Status("No release assets found.");
            AddLog($"{repo}: no assets found.", LogKind.Warn);
            info.LastChecked = Stamp.Now();
            info.LastResult = "No assets";
            SaveDb();
            RefreshRepos();
            return "no assets found";
        }

        if (plan.Jobs.Count == 0)
        {
            Status($"{repo}: already up to date{filteredNote}.");
            AddLog($"{repo}: already up to date{filteredNote}.", LogKind.Ok);
            info.LastResult = "Up to date" + filteredNote;
            info.LastChecked = Stamp.Now();
            SaveDb();
            RefreshRepos();
            return "up to date" + filteredNote;
        }

        // 3) download
        return await Download(repo, root, plan, batch, filteredNote);
    }

    private async Task<string> Download(string repo, string root, UpdatePlan plan, bool batch, string filteredNote)
    {
        var info = _db.Ensure(repo);
        _run = new RunState { Repo = repo, Total = plan.Jobs.Count, TotalBytes = plan.TotalBytes };
        while (_results.TryDequeue(out _)) { }
        Transfers.Clear();
        _transferMap.Clear();
        IsDownloading = true;
        foreach (var g in plan.Jobs.GroupBy(j => j.Tag))
        {
            var race = new RaceItem
            {
                Repo = repo, Tag = g.Key, TotalFiles = g.Count(), TotalBytes = g.Sum(j => j.Asset.Size),
                IsNew = !info.Assets.ContainsKey(g.Key),
            };
            race.Refresh();
            race.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RaceItem.State)) RaceStateChanged(); };
            Races.Insert(0, race);
            if (race.IsNew)
                AddLog($"{Bot}[NEW] {repo} {g.Key} :: {race.TotalFiles}F {Zipscript.Compact(race.TotalBytes)} :: racing now", LogKind.Bot);
        }
        while (Races.Count > 200) Races.RemoveAt(Races.Count - 1);

        Status($"{repo}: downloading {plan.Jobs.Count} asset(s) ({Fmt.Bytes(plan.TotalBytes)}) of {plan.TotalSeen} seen{filteredNote}...");
        AddLog($"{repo}: starting {plan.Jobs.Count} asset(s), {Fmt.Bytes(plan.TotalBytes)} total...");
        info.LastChecked = Stamp.Now();
        SaveDb();
        UpdateProgress();

        var dl = new Downloader();
        dl.Started += job => Dispatcher.UIThread.Post(() =>
        {
            if (_run is null) return;
            var item = new TransferItem { Job = job };
            _transferMap[job] = item;
            Transfers.Add(item);
        });
        dl.Progress += p => Dispatcher.UIThread.Post(() =>
        {
            if (_transferMap.TryGetValue(p.Job, out var item))
            {
                item.Update(p.Downloaded, p.BytesPerSec, p.EtaSeconds);
                UpdateProgress();
            }
        });
        dl.Completed += r =>
        {
            _results.Enqueue(r);
            Dispatcher.UIThread.Post(DrainResults);
        };

        var threads = Math.Clamp(_cfg.MaxThreads, 1, 16);
        try
        {
            await dl.RunAsync(plan.Jobs, threads, _pause, _soft.Token, _hard.Token);
        }
        catch (Exception ex)
        {
            AddLog($"Downloader error: {ex.Message}", LogKind.Err);
        }
        DrainResults();

        // 4) finalize
        var run = _run;
        _run = null;
        IsDownloading = false;
        Transfers.Clear();
        _transferMap.Clear();

        var ok = run.Ok;
        var failed = run.Failed;
        var stopped = _stopRequested;
        foreach (var race in Races.Where(r => r.IsRacing && r.Repo == repo).ToList())
            FinishRace(race, stopped ? RaceState.Stopped : RaceState.Incomplete);
        var result = $"OK={ok}, Failed={failed}{(plan.Filtered > 0 ? $", Filtered={plan.Filtered}" : "")}";
        if (stopped) result = "Stopped: " + result;
        info.LastResult = result;
        info.LastChecked = Stamp.Now();
        SaveDb();
        RefreshRepos();

        string? jsonPath = null, csvPath = null;
        if (run.Records.Count > 0)
        {
            try { (jsonPath, csvPath) = Exporters.WriteRunLog(root, repo, run.Records); }
            catch (Exception ex) { AddLog($"Warning: failed to write JSON/CSV: {ex.Message}", LogKind.Warn); }
        }

        foreach (var r in run.Records.Where(r => !r.Success))
            AddLog($"  FAILED {r.Tag}/{r.Name}: {r.Error}", LogKind.Err);

        var raced = run.Records.Where(r => r.Success).Sum(r => r.SizeBytes);
        if (raced > 0)
        {
            SaveConfig(); // lifetime DN counter
            AddLog($"RACE: {repo} +{Fmt.Bytes(raced)} UP -> +{Fmt.Bytes(raced * Ratio.Multiplier)} credits @ {Ratio.Display} · class {RatioClass}", LogKind.Sys);
        }
        Status($"{repo}: downloads {(stopped ? "stopped" : "finished")}. OK={ok}, Failed={failed}");
        AddLog($"{repo}: update finished — OK={ok}, failed={failed}", failed > 0 ? LogKind.Err : LogKind.Ok);
        _ = GitHubApi.SendDiscordAsync(_cfg.DiscordWebhook, $"{repo}: update finished — OK={ok}, failed={failed}");

        if (!batch && !stopped)
        {
            var meta = jsonPath is null ? "" : $"\n\nMetadata:\n{jsonPath}\n{csvPath}";
            await _ui.ShowMessage("DONE", $"{repo}\n\nDownloads complete.\nOK={ok}, Failed={failed}{meta}",
                failed > 0 ? MsgKind.Warn : MsgKind.Ok);
        }
        return result;
    }

    private void DrainResults()
    {
        var run = _run;
        if (run is null) return;
        while (_results.TryDequeue(out var r))
        {
            run.Completed++;
            run.CompletedBytes += r.Job.Asset.Size;
            if (_transferMap.Remove(r.Job, out var item)) Transfers.Remove(item);

            if (r.Cancelled && !r.Success)
            {
                // hard stop: partial file stays on disk for resume next time; don't count as a failure record
                continue;
            }

            if (Races.FirstOrDefault(x => x.IsRacing && x.Repo == run.Repo && x.Tag == r.Job.Tag) is { } race)
            {
                race.Add(r.Success && r.Sha1 is not null, r.Job.Asset.Size);
                if (race.IsFinishedCount) FinishRace(race, race.FailedFiles == 0 ? RaceState.Complete : RaceState.Incomplete);
            }
            if (r.Success) _downloadedThisRun++;

            run.Records.Add(new AssetRecord
            {
                Repo = run.Repo,
                Tag = r.Job.Tag,
                Name = r.Job.Asset.Name,
                SizeBytes = r.Job.Asset.Size,
                Url = r.Job.Asset.BrowserDownloadUrl,
                Path = r.Job.FilePath,
                Sha1 = r.Sha1,
                Success = r.Success,
                Error = r.Error,
            });

            if (r.Success && r.Sha1 is not null)
            {
                var info = _db.Ensure(run.Repo);
                if (!info.Assets.TryGetValue(r.Job.Tag, out var tagAssets))
                    info.Assets[r.Job.Tag] = tagAssets = new Dictionary<string, AssetMeta>();
                tagAssets[r.Job.Asset.Name] = new AssetMeta
                {
                    SizeBytes = r.Job.Asset.Size,
                    Sha1 = r.Sha1,
                    Path = r.Job.FilePath,
                    DownloadedAt = Stamp.Now(),
                };
                SaveDbSoon();
                if (_cfg.Stats is { } st) { st.DownloadedBytes += r.Job.Asset.Size; st.DownloadedFiles++; }
                AddLog($"{r.Job.Tag}/{r.Job.Asset.Name} saved ({Fmt.Bytes(r.Job.Asset.Size)})", LogKind.Ok);
            }
        }
        UpdateProgress();
        UpdateRatio();
    }

    private void UpdateProgress()
    {
        var run = _run;
        if (run is null) return;
        var inflight = _transferMap.Values.Sum(t => t.Downloaded);
        var done = run.CompletedBytes + inflight;
        var frac = run.TotalBytes > 0 ? done / (double)run.TotalBytes : run.Total > 0 ? run.Completed / (double)run.Total : 0;
        ProgressBar = Fmt.Bar(frac, 48);
        ProgressPct = $"{Math.Clamp(frac, 0, 1) * 100:0.0}%";
        CountsText = $"FiLES {run.Completed}/{run.Total} · OK {run.Ok} · FAiL {run.Failed} · {Fmt.Bytes(done)} / {Fmt.Bytes(run.TotalBytes)}";
    }

    // ═══════════════════════════ pause / stop ═══════════════════════════

    [RelayCommand]
    private async Task TogglePause()
    {
        if (!IsDownloading) { await _ui.ShowMessage("IDLE", "Nothing is running."); return; }
        if (_pause.IsPaused) _pause.Resume(); else _pause.Pause();
        IsPaused = _pause.IsPaused;
        AddLog($"Pause toggled: {(IsPaused ? "ON" : "OFF")}", LogKind.Warn);
        if (IsPaused) Flash("PAUSED - press F7 to resume");
        else Status("Resumed.");
    }

    [RelayCommand]
    private async Task SafeStop()
    {
        if (!IsBusy) { await _ui.ShowMessage("IDLE", "Nothing to stop."); return; }
        AddLog("Safe stop requested.", LogKind.Warn);
        _stopRequested = true;
        _soft.Cancel();
        _pause.Resume();
        IsPaused = false;
        Status("Safe stop requested. Current downloads finishing; remaining skipped.");
    }

    [RelayCommand]
    private async Task HardStop()
    {
        if (!IsBusy) { await _ui.ShowMessage("IDLE", "Nothing to stop."); return; }
        AddLog("Hard stop requested.", LogKind.Warn);
        _stopRequested = true;
        _soft.Cancel();
        _hard.Cancel();
        _pause.Resume();
        IsPaused = false;
        Status("Hard stop requested. Downloads aborted ASAP (partial files kept for resume).");
    }

    // ═══════════════════════════ misc commands ═══════════════════════════

    [RelayCommand]
    private async Task BrowseFolder()
    {
        var path = await _ui.PickFolder("Select root download folder", RootFolder);
        if (path is null) return;
        RootFolder = path;
        CommitRootFolder();
    }

    public void DropFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        RootFolder = path;
        CommitRootFolder();
    }

    [RelayCommand]
    private void OpenOnGitHub()
    {
        var key = CurrentRepoKey();
        if (key is null) return;
        OpenShell($"https://github.com/{key}/releases");
    }

    [RelayCommand]
    private async Task OpenRepoFolder()
    {
        var key = CurrentRepoKey();
        if (key is null) return;
        var folder = AppPaths.RepoRoot(RootFolder.Trim(), key);
        if (Directory.Exists(folder)) OpenShell(folder);
        else await _ui.ShowMessage("FOLDER MISSING", $"{folder}\n\nDoes not exist.", MsgKind.Warn);
    }

    private void OpenShell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { AddLog($"Error opening {target}: {ex.Message}", LogKind.Err); }
    }

    [RelayCommand]
    private async Task ExportDb()
    {
        if (_db.Repos.Count == 0) { await _ui.ShowMessage("NO DATA", "No repo metadata to export yet."); return; }
        var path = await _ui.PickSaveFile("Export metadata DB", $"repos_export_{DateTime.Now:yyyyMMdd}.json");
        if (path is null) return;
        try
        {
            Exporters.ExportDb(_db, path);
            AddLog($"DB exported to {path}", LogKind.Ok);
            await _ui.ShowMessage("EXPORTED", $"Metadata exported to:\n{path}", MsgKind.Ok);
        }
        catch (Exception ex) { await _ui.ShowMessage("ERROR", $"Failed to export DB:\n{ex.Message}", MsgKind.Error); }
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    [RelayCommand]
    private async Task About()
    {
        if (await _ui.ShowRules()) await _ui.ShowAbout();
    }

    // ═══════════════════════════ tools (DAT / PDF / orphans / hashes / scan) ═══════════════════════════

    private async Task<(string Key, RepoInfo Info, string Root)?> SelectedForTool()
    {
        var key = CurrentRepoKey();
        if (key is null) { await _ui.ShowMessage("ERROR", "Selected repo must be in owner/repo format.", MsgKind.Error); return null; }
        if (!_db.Repos.TryGetValue(key, out var info)) { await _ui.ShowMessage("ERROR", "Selected repo has no metadata yet.", MsgKind.Error); return null; }
        var root = RootFolder.Trim();
        if (root.Length == 0) { await _ui.ShowMessage("ERROR", "Set a root download folder first.", MsgKind.Error); return null; }
        if (!await EnsureIdle()) return null;
        return (key, info, root);
    }

    /// <summary>Run a long tool job in the background with the UI in busy state.</summary>
    private async Task<T?> Busy<T>(string label, Func<IProgress<string>, CancellationToken, T> work)
    {
        IsBusy = true;
        _stopRequested = false;
        _hard = new CancellationTokenSource();
        _soft = new CancellationTokenSource();
        Flash(label);
        var progress = new Progress<string>(s => StatusText = $"{label}  {Trim(s, 70)}");
        try
        {
            return await Task.Run(() => work(progress, _hard.Token));
        }
        catch (OperationCanceledException)
        {
            AddLog($"{label}: cancelled", LogKind.Warn);
            return default;
        }
        finally
        {
            IsBusy = false;
            Status("Ready.");
        }
    }

    [RelayCommand]
    private async Task GenerateDat()
    {
        if (await SelectedForTool() is not { } t) return;
        var path = await Busy("GENERATiNG DAT...", (_, ct) => Exporters.GenerateDat(t.Key, t.Info, t.Root, ct));
        if (path is null) return;
        SaveDb(); // CRCs get cached
        AddLog($"{t.Key}: DAT generated at {path}", LogKind.Ok);
        await _ui.ShowMessage("DAT GENERATED", $"DAT file created:\n{path}", MsgKind.Ok);
    }

    [RelayCommand]
    private async Task GeneratePdf()
    {
        if (await SelectedForTool() is not { } t) return;
        try
        {
            var path = Exporters.GenerateSummaryPdf(t.Key, t.Info, t.Root);
            AddLog($"{t.Key}: summary generated at {path}", LogKind.Ok);
            await _ui.ShowMessage("SUMMARY GENERATED", $"Summary file created:\n{path}", MsgKind.Ok);
        }
        catch (Exception ex) { await _ui.ShowMessage("ERROR", ex.Message, MsgKind.Error); }
    }

    [RelayCommand]
    private async Task CleanupOrphans()
    {
        if (await SelectedForTool() is not { } t) return;
        var orphans = RepoMaintenance.FindOrphans(t.Key, t.Root, t.Info);
        if (orphans.Count == 0) { await _ui.ShowMessage("ORPHANS", "No orphaned files detected.", MsgKind.Ok); return; }
        var preview = string.Join("\n", orphans.Take(12).Select(o => "  " + Path.GetRelativePath(t.Root, o)));
        if (orphans.Count > 12) preview += $"\n  ... and {orphans.Count - 12} more";
        if (!await _ui.Confirm("CLEANUP ORPHANS",
                $"Found {orphans.Count} orphaned file(s):\n{preview}\n\nThey will be moved to an 'orphans' subfolder.\nProceed?")) return;
        var (moved, orphanRoot) = RepoMaintenance.MoveOrphans(t.Key, t.Root, orphans);
        AddLog($"{t.Key}: moved {moved} orphan(s) to {orphanRoot}", LogKind.Warn);
        await _ui.ShowMessage("CLEANUP COMPLETE", $"Moved {moved} orphaned file(s) to:\n{orphanRoot}", MsgKind.Ok);
    }

    [RelayCommand]
    private async Task HashRescan()
    {
        if (await SelectedForTool() is not { } t) return;
        if (!await _ui.Confirm("FORCE HASH RESCAN", "This will recompute SHA-1 for all known assets.\nProceed?")) return;
        var res = await Busy("HASHiNG...", (p, ct) => RepoMaintenance.ForceHashRescan(t.Info, p, ct));
        SaveDb();
        RefreshRepos();
        AddLog($"{t.Key}: hash rescan — {res.Updated} updated, {res.Missing} missing/fail", res.Missing > 0 ? LogKind.Warn : LogKind.Ok);
        await _ui.ShowMessage("HASH RESCAN COMPLETE", $"{t.Key}:\nUpdated {res.Updated} asset(s).\nMissing/failed: {res.Missing}");
    }

    [RelayCommand]
    private async Task ScanLocal()
    {
        if (await SelectedForTool() is not { } t) return;
        var repoRoot = AppPaths.RepoRoot(t.Root, t.Key);
        if (!Directory.Exists(repoRoot)) { await _ui.ShowMessage("MISSING FOLDER", $"Repo folder does not exist:\n{repoRoot}", MsgKind.Error); return; }
        if (!await _ui.Confirm("SCAN LOCAL FILES",
                "This will scan all files under this repo's folder on disk,\nuse the top-level subfolder names as tags, and rebuild metadata.\n\nProceed?")) return;
        var res = await Busy("SCANNiNG...", (p, ct) => RepoMaintenance.ScanLocal(t.Key, t.Root, t.Info, p, ct));
        if (res is null) return;
        SaveDb();
        RefreshRepos();
        AddLog($"{t.Key}: local scan complete — added={res.Added}, updated={res.Updated}, skipped={res.Skipped}", LogKind.Ok);
        try
        {
            var (j, c) = Exporters.RebuildMetadataIfMissing(t.Key, t.Info, t.Root);
            if (j || c) AddLog($"{t.Key}: metadata rebuilt (json={j}, csv={c})", LogKind.Ok);
        }
        catch (Exception ex) { AddLog($"{t.Key}: metadata rebuild failed: {ex.Message}", LogKind.Err); }
        await _ui.ShowMessage("SCAN COMPLETE",
            $"{t.Key}:\nScanned files: {res.Total}\nAdded: {res.Added}\nUpdated: {res.Updated}\nUnchanged (skipped): {res.Skipped}", MsgKind.Ok);
    }

    // ═══════════════════════════ dialogs: filters / setup ═══════════════════════════

    [RelayCommand]
    private Task OpenFilters() => OpenFilterEditor(null);

    [RelayCommand]
    private Task OpenRepoFilters() => OpenFilterEditor(CurrentRepoKey());

    private async Task OpenFilterEditor(string? scope)
    {
        var vm = new FilterEditorViewModel(_cfg.Filters, _db.Repos.Keys, scope, _cfg.GithubToken);
        if (!await _ui.ShowFilters(vm)) return;
        _cfg.Filters = vm.Result();
        SaveConfig();
        RefreshRepos();
        AddLog($"Filter rules saved · global: {StatFilter}", LogKind.Ok);
        await CleanupFilteredFiles(afterSave: true);
    }

    [RelayCommand]
    private Task CleanupFiltered() => CleanupFilteredFiles(afterSave: false);

    /// <summary>Offer to remove files already on disk that the current filter rules would now skip.</summary>
    private async Task CleanupFilteredFiles(bool afterSave)
    {
        var root = RootFolder.Trim();
        if (root.Length == 0 || !Directory.Exists(root))
        {
            if (!afterSave) await _ui.ShowMessage("ERROR", "Set an existing root download folder first.", MsgKind.Error);
            return;
        }
        if (IsBusy)
        {
            if (!afterSave) await EnsureIdle();
            else AddLog("Skipped the filtered-file check (busy) - run Tools > Clean Up Filtered Files later.", LogKind.Warn);
            return;
        }

        var repos = _db.Repos.Keys.ToList();
        var filters = _cfg.Filters;
        var found = await Busy("CHECKiNG LOCAL FiLES AGAiNST FiLTERS...",
            (_, ct) => RepoMaintenance.FindFilteredLocal(repos, filters, root, ct));
        if (found is null) return;
        if (found.Count == 0)
        {
            if (!afterSave) await _ui.ShowMessage("FILTER CLEANUP", "No local files match the current skip rules.", MsgKind.Ok);
            else AddLog("No existing local files match the new skip rules.", LogKind.Ok);
            return;
        }

        var total = found.Sum(f => f.Size);
        var perRepo = string.Join("\n", found.GroupBy(f => f.Repo)
            .Select(g => $"  {g.Key}: {g.Count()} file(s), {Fmt.Bytes(g.Sum(f => f.Size))}"));
        var sample = string.Join("\n", found.Take(10).Select(f => $"  {Path.GetFileName(f.Path)}  ({f.Reason})"));
        if (found.Count > 10) sample += $"\n  ... and {found.Count - 10} more";
        var msg = $"{found.Count} file(s) already in your archive ({Fmt.Bytes(total)}) match the skip rules:\n\n" +
                  $"{perRepo}\n\n{sample}\n\n" +
                  "Clean them up? They'll also be removed from the repo DB, and tag folders\n" +
                  "left with only release notes are removed.\n\n" +
                  "RECYCLE BIN = undo-able from the bin · MOVE = kept in owner_repo\\_filtered\\";
        var pick = await _ui.Choose("CLEAN UP FILTERED FILES?", msg, "RECYCLE BIN", "MOVE TO _filtered", "LEAVE THEM");
        if (pick is not (0 or 1))
        {
            AddLog($"Filter cleanup skipped - {found.Count} matching local file(s) left in place.", LogKind.Warn);
            return;
        }

        var mode = pick == 0 ? RepoMaintenance.CleanupMode.RecycleBin : RepoMaintenance.CleanupMode.MoveToFiltered;
        var db = _db;
        var res = await Busy(mode == RepoMaintenance.CleanupMode.RecycleBin ? "RECYCLiNG..." : "MOViNG...",
            (_, _) => RepoMaintenance.CleanupFiltered(found, db, root, mode));
        SaveDb();
        RefreshRepos();
        var where = mode == RepoMaintenance.CleanupMode.RecycleBin ? "sent to the Recycle Bin" : "moved to _filtered";
        AddLog($"Filter cleanup: {res.Done} file(s) {where}, {res.Folders} empty tag folder(s) removed" +
               (res.Failed > 0 ? $", {res.Failed} FAILED" : ""), res.Failed > 0 ? LogKind.Err : LogKind.Ok);
        await _ui.ShowMessage("FILTER CLEANUP DONE",
            $"{res.Done} file(s) {where} ({Fmt.Bytes(total)}).\n{res.Folders} empty tag folder(s) removed." +
            (res.Failed > 0 ? $"\n{res.Failed} file(s) could not be removed (in use?)." : ""),
            res.Failed > 0 ? MsgKind.Warn : MsgKind.Ok);
    }

    [RelayCommand]
    private async Task OpenSettings()
    {
        var vm = new SettingsViewModel(_cfg);
        var ok = await _ui.ShowSettings(vm);
        if (!ok)
        {
            ThemeManager.Apply(_cfg.Theme, _cfg.FontFamily, _cfg.FontSize); // undo live preview
            return;
        }
        vm.ApplyTo(_cfg);
        SaveConfig();
        ThemeManager.Apply(_cfg.Theme, _cfg.FontFamily, _cfg.FontSize);
        ShowScanlines = _cfg.CrtScanlines;
        ShowScroller = _cfg.Scroller;
        OnPropertyChanged(nameof(ScrollerText));
        RatioUser = Handle.For(_cfg);
        UI.LeetText.Enabled = _cfg.LeetSpeak;
        if (AutoUpdateEnabled != _cfg.AutoUpdateEnabled) AutoUpdateEnabled = _cfg.AutoUpdateEnabled; // triggers reschedule
        else ScheduleAutoUpdate();
        AddLog("Sysop setup saved.", LogKind.Ok);
    }

    [RelayCommand]
    private static void Exit()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d)
            d.Shutdown();
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
