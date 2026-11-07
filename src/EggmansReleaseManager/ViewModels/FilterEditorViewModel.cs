using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EggmansReleaseManager.Models;
using EggmansReleaseManager.Services;

namespace EggmansReleaseManager.ViewModels;

public sealed partial class FilterScope : ObservableObject
{
    public required string Key { get; init; }      // "" = global
    public bool IsGlobal => Key.Length == 0;
    public string Display => IsGlobal ? "*** GLOBAL RULES ***" : Key;
    [ObservableProperty] private bool _hasRule;
    public string Glyph => IsGlobal ? "◆" : HasRule ? "ƒ" : "·";
    partial void OnHasRuleChanged(bool value) => OnPropertyChanged(nameof(Glyph));
}

public sealed partial class PlatformToggle : ObservableObject
{
    public required PlatformDetector.PlatformTag Tag { get; init; }
    public string Label => Tag.Label;
    public string Hint => Tag.Description;
    public string Group => Tag.Kind switch
    {
        PlatformDetector.TagKind.Os => "OS",
        PlatformDetector.TagKind.Arch => "ARCH",
        _ => "KiND",
    };
    [ObservableProperty] private bool _excluded;
    public Action? Changed { get; set; }
    partial void OnExcludedChanged(bool value) => Changed?.Invoke();
}

public sealed class PreviewRow
{
    public required string Tag { get; init; }
    public required string Name { get; init; }
    public required string Size { get; init; }
    public required bool Keep { get; init; }
    public required string Reason { get; init; }
    public required string Platforms { get; init; }
    public string Verdict => Keep ? "GET " : "SKIP";
    public bool Skip => !Keep;
}

/// <summary>Edits a working copy of the filter config; nothing is saved until the window returns true.</summary>
public sealed partial class FilterEditorViewModel : ObservableObject
{
    private readonly FilterConfig _work;
    private readonly string? _token;
    private bool _loading;

    public ObservableCollection<FilterScope> Scopes { get; } = [];
    public ObservableCollection<PlatformToggle> Platforms { get; } = [];
    public ObservableCollection<PreviewRow> Preview { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsRepoScope), nameof(ScopeTitle), nameof(EditorEnabled))]
    private FilterScope? _selectedScope;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(EditorEnabled))] private bool _hasCustomRule;
    [ObservableProperty] private bool _useGlobal = true;
    [ObservableProperty] private string _excludePatternsText = "";
    [ObservableProperty] private string _includePatternsText = "";
    [ObservableProperty] private string _testFileName = "lichii-linux-x86_64.AppImage";
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private bool _testKeep = true;
    [ObservableProperty] private string _patternWarning = "";
    [ObservableProperty] private string _previewStatus = "Pick a repo on the left and hit [ PREViEW ] to test these rules against its real releases.";
    [ObservableProperty] private bool _isPreviewing;
    /// <summary>Source code archives. Global: true/false. Repo: null = follow GLOBAL.</summary>
    [ObservableProperty] private bool? _sourceZip;
    [ObservableProperty] private bool? _sourceTarGz;
    [ObservableProperty] private bool _snapshot;

    public bool IsRepoScope => SelectedScope is { IsGlobal: false };
    public bool EditorEnabled => SelectedScope is { IsGlobal: true } || HasCustomRule;
    public string ScopeTitle => SelectedScope is null ? "" : SelectedScope.IsGlobal ? "GLOBAL RULES (apply to every repo)" : $"REPO RULES :: {SelectedScope.Key}";

    public FilterEditorViewModel(FilterConfig current, IEnumerable<string> repoKeys, string? initialScope, string? token)
    {
        _work = JsonSerializer.Deserialize<FilterConfig>(JsonSerializer.Serialize(current, Json.Options), Json.Options) ?? new FilterConfig();
        _work.Repos ??= new();
        _token = token;

        Scopes.Add(new FilterScope { Key = "" });
        var keys = repoKeys.Concat(_work.Repos.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys)
            Scopes.Add(new FilterScope { Key = k, HasRule = FindRule(k) is not null });

        foreach (var tag in PlatformDetector.All)
            Platforms.Add(new PlatformToggle { Tag = tag, Changed = OnEdited });

        SelectedScope = (initialScope is null ? null : Scopes.FirstOrDefault(s => string.Equals(s.Key, initialScope, StringComparison.OrdinalIgnoreCase)))
                        ?? Scopes[0];
    }

    private RepoFilterRule? FindRule(string key) =>
        _work.Repos.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    private void RemoveRule(string key)
    {
        foreach (var k in _work.Repos.Keys.Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToList())
            _work.Repos.Remove(k);
    }

    partial void OnSelectedScopeChanged(FilterScope? oldValue, FilterScope? newValue)
    {
        if (oldValue is not null) Commit(oldValue);
        Load(newValue);
        Preview.Clear();
        PreviewStatus = newValue is { IsGlobal: false }
            ? "Hit [ PREViEW ] to test these rules against this repo's real releases (1 API call)."
            : "Global scope: preview uses the repo you pick on the left. Or test a filename below.";
    }

    private void Load(FilterScope? scope)
    {
        _loading = true;
        try
        {
            FilterRule? rule = scope is null ? null : scope.IsGlobal ? _work.Global : FindRule(scope.Key);
            HasCustomRule = scope is { IsGlobal: false } && rule is not null;
            UseGlobal = (rule as RepoFilterRule)?.UseGlobal ?? true;
            foreach (var p in Platforms)
                p.Excluded = rule?.ExcludePlatforms.Contains(p.Tag.Id, StringComparer.OrdinalIgnoreCase) == true;
            ExcludePatternsText = string.Join(Environment.NewLine, rule?.ExcludePatterns ?? []);
            IncludePatternsText = string.Join(Environment.NewLine, rule?.IncludePatterns ?? []);
            if (scope is { IsGlobal: true })
            {
                SourceZip = rule?.SourceZip == true;
                SourceTarGz = rule?.SourceTarGz == true;
            }
            else
            {
                SourceZip = rule?.SourceZip;
                SourceTarGz = rule?.SourceTarGz;
            }
            Snapshot = (rule as RepoFilterRule)?.Snapshot == true;
        }
        finally { _loading = false; }
        OnEdited();
    }

    private void Commit(FilterScope scope)
    {
        if (scope.IsGlobal)
        {
            Fill(_work.Global);
            // global: unticked is stored as "not set" so the config stays clean
            _work.Global.SourceZip = SourceZip == true ? true : null;
            _work.Global.SourceTarGz = SourceTarGz == true ? true : null;
            return;
        }
        if (!HasCustomRule) { RemoveRule(scope.Key); scope.HasRule = false; return; }
        var rule = FindRule(scope.Key);
        if (rule is null) { rule = new RepoFilterRule(); _work.Repos[scope.Key] = rule; }
        rule.UseGlobal = UseGlobal;
        Fill(rule);
        rule.SourceZip = SourceZip;
        rule.SourceTarGz = SourceTarGz;
        rule.Snapshot = Snapshot;
        scope.HasRule = true;
    }

    private void Fill(FilterRule rule)
    {
        rule.ExcludePlatforms = Platforms.Where(p => p.Excluded).Select(p => p.Tag.Id).ToList();
        rule.ExcludePatterns = Lines(ExcludePatternsText);
        rule.IncludePatterns = Lines(IncludePatternsText);
    }

    private static List<string> Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    partial void OnHasCustomRuleChanged(bool value) => OnEdited();
    partial void OnUseGlobalChanged(bool value) => OnEdited();
    partial void OnExcludePatternsTextChanged(string value) => OnEdited();
    partial void OnIncludePatternsTextChanged(string value) => OnEdited();
    partial void OnSourceZipChanged(bool? value) => OnEdited();
    partial void OnSourceTarGzChanged(bool? value) => OnEdited();
    partial void OnSnapshotChanged(bool value) => OnEdited();
    partial void OnTestFileNameChanged(string value) => RunTest();

    [ObservableProperty] private string _inheritedSummary = "";

    private void UpdateInherited()
    {
        var g = _work.Global;
        var parts = new List<string>();
        if (g.ExcludePlatforms.Count > 0) parts.Add("skip " + string.Join(", ", g.ExcludePlatforms.Select(id => PlatformDetector.Find(id)?.Label ?? id)));
        if (g.ExcludePatterns.Count > 0) parts.Add($"{g.ExcludePatterns.Count} skip pattern(s)");
        if (g.IncludePatterns.Count > 0) parts.Add($"{g.IncludePatterns.Count} always-get pattern(s)");
        var src = new[] { g.SourceZip == true ? "zip" : null, g.SourceTarGz == true ? "tar.gz" : null }.Where(x => x is not null).ToList();
        if (src.Count > 0) parts.Add("source " + string.Join("+", src));
        var global = parts.Count == 0 ? "nothing" : string.Join(" · ", parts);
        InheritedSummary = !HasCustomRule ? $"► follows GLOBAL: {global}"
            : UseGlobal ? $"► plus inherited from GLOBAL: {global}"
            : "► GLOBAL rules are NOT applied to this repo";
    }

    private void OnEdited()
    {
        if (_loading || SelectedScope is null) return;
        Commit(SelectedScope);
        UpdateInherited();
        var bad = Lines(ExcludePatternsText).Concat(Lines(IncludePatternsText)).Where(p => AssetFilter.TryCompile(p) is null).ToList();
        PatternWarning = bad.Count == 0 ? "" : "INVALID (ignored): " + string.Join("  ", bad);
        RunTest();
    }

    private AssetFilter CurrentFilter() =>
        SelectedScope is null or { IsGlobal: true }
            ? new AssetFilter(_work.Global)
            : AssetFilter.For(_work, SelectedScope.Key);

    private void RunTest()
    {
        var name = TestFileName?.Trim() ?? "";
        if (name.Length == 0) { TestResult = ""; return; }
        var d = CurrentFilter().Evaluate(name);
        TestKeep = d.Keep;
        var tags = d.Tags.Count == 0 ? "none" : string.Join(", ", d.Tags);
        TestResult = $"{(d.Keep ? "GET  ✓" : "SKIP ✗")}  ·  {d.Reason}  ·  detected: {tags}";
    }

    [RelayCommand]
    private async Task RunPreview()
    {
        var repo = SelectedScope is { IsGlobal: false } s ? s.Key : null;
        if (repo is null)
        {
            PreviewStatus = "Select a repo in the list on the left first (its rules + global rules will be applied).";
            return;
        }
        IsPreviewing = true;
        Preview.Clear();
        PreviewStatus = $"Asking GitHub about {repo}...";
        try
        {
            var releases = await GitHubApi.GetAllReleasesAsync(repo, _token, CancellationToken.None, maxPages: 1);
            var filter = CurrentFilter();
            int keep = 0, skip = 0;
            long keepBytes = 0, skipBytes = 0;
            foreach (var rel in releases.Where(r => !r.Draft))
                foreach (var a in rel.Assets.Concat(SourceRows(repo, rel, filter)))
                {
                    var d = filter.Evaluate(a.Name);
                    if (d.Keep) { keep++; keepBytes += a.Size; } else { skip++; skipBytes += a.Size; }
                    Preview.Add(new PreviewRow
                    {
                        Tag = rel.FolderTag,
                        Name = a.Name,
                        Size = a.SizeUnknown ? "?" : Fmt.Bytes(a.Size),
                        Keep = d.Keep,
                        Reason = d.Reason,
                        Platforms = d.Tags.Count == 0 ? "-" : string.Join(",", d.Tags),
                    });
                }
            PreviewStatus = $"{releases.Count} release(s) · GET {keep} ({Fmt.Bytes(keepBytes)}) · SKIP {skip} ({Fmt.Bytes(skipBytes)})" +
                            (releases.Count >= 100 ? " · showing newest 100 releases" : "");
        }
        catch (Exception ex)
        {
            PreviewStatus = "ERROR: " + ex.Message;
        }
        finally { IsPreviewing = false; }
    }

    /// <summary>The generated "Source code" archives this release would get (only the enabled ones).</summary>
    private static IEnumerable<GhAsset> SourceRows(string repo, GhRelease rel, AssetFilter filter)
    {
        if (string.IsNullOrWhiteSpace(rel.TagName)) yield break;
        if (filter.SourceZip) yield return Archives.SourceAsset(repo, rel.TagName!, Archives.SourceKind.Zip);
        if (filter.SourceTarGz) yield return Archives.SourceAsset(repo, rel.TagName!, Archives.SourceKind.TarGz);
    }

    [RelayCommand]
    private void AddPattern(string pattern)
    {
        var lines = Lines(ExcludePatternsText);
        if (!lines.Contains(pattern)) lines.Add(pattern);
        ExcludePatternsText = string.Join(Environment.NewLine, lines);
    }

    public FilterConfig Result()
    {
        if (SelectedScope is not null) Commit(SelectedScope);
        // drop empty repo rules that still inherit global; they change nothing
        foreach (var k in _work.Repos.Where(kv => kv.Value.UseGlobal && kv.Value.IsEmpty).Select(kv => kv.Key).ToList())
            _work.Repos.Remove(k);
        return _work;
    }
}
