using System.Text.RegularExpressions;
using EggmansReleaseManager.Models;
using static EggmansReleaseManager.Services.PlatformDetector;

namespace EggmansReleaseManager.Services;

public sealed record FilterDecision(bool Keep, string Reason, IReadOnlyList<string> Tags);

/// <summary>
/// Decides whether a release asset should be downloaded.
/// Order: always-keep patterns win, then exclude patterns, then platform/kind tags.
/// </summary>
public sealed class AssetFilter
{
    private readonly HashSet<string> _excludedTags;
    private readonly List<(string Text, Regex Rx)> _exclude;
    private readonly List<(string Text, Regex Rx)> _include;

    public static readonly AssetFilter None = new(new FilterRule());

    public bool IsEmpty { get; }

    /// <summary>Effective "Source code" archive choices for this repo.</summary>
    public bool SourceZip { get; }
    public bool SourceTarGz { get; }

    /// <summary>Newest N releases only; null = all.</summary>
    public int? ReleaseLimit { get; }
    private readonly string? _repoKey;

    public AssetFilter(FilterRule rule, string? repoKey = null)
    {
        _repoKey = repoKey;
        SourceZip = rule.SourceZip == true;
        SourceTarGz = rule.SourceTarGz == true;
        ReleaseLimit = rule.ReleaseLimit is > 0 ? rule.ReleaseLimit : null;
        _excludedTags = new HashSet<string>(rule.ExcludePlatforms, StringComparer.OrdinalIgnoreCase);
        _exclude = Compile(rule.ExcludePatterns);
        _include = Compile(rule.IncludePatterns);
        IsEmpty = _excludedTags.Count == 0 && _exclude.Count == 0 && _include.Count == 0;
    }

    /// <summary>Build the effective filter for a repo from the saved config.</summary>
    public static AssetFilter For(FilterConfig cfg, string repoKey) => new(Effective(cfg, repoKey), repoKey);

    /// <summary>
    /// Published releases newest-first (by publish date, falling back to creation date), drafts dropped,
    /// cut to the newest <paramref name="limit"/> when one is set.
    /// </summary>
    public static List<GhRelease> ApplyReleaseLimit(IEnumerable<GhRelease> releases, int? limit)
    {
        var ordered = releases.Where(r => !r.Draft)
            .Select((r, i) => (r, i, at: Stamp.Parse(r.PublishedAt) ?? Stamp.Parse(r.CreatedAt)))
            .OrderByDescending(x => x.at ?? DateTime.MinValue)
            .ThenBy(x => x.i)                               // keep GitHub's order for ties / undated
            .Select(x => x.r);
        return (limit is > 0 ? ordered.Take(limit.Value) : ordered).ToList();
    }

    /// <summary>API pages (100 releases each) worth fetching for a limit; a little slack for drafts/ordering.</summary>
    public static int PagesFor(int? limit) => limit is > 0 ? (limit.Value + 10 + 99) / 100 : int.MaxValue;

    /// <summary>Whether this repo opted in to default-branch snapshots.</summary>
    public static bool WantsSnapshot(FilterConfig cfg, string repoKey) =>
        cfg.Repos?.FirstOrDefault(kv => string.Equals(kv.Key, repoKey, StringComparison.OrdinalIgnoreCase)).Value?.Snapshot == true;

    public static FilterRule Effective(FilterConfig cfg, string repoKey)
    {
        var global = cfg.Global ?? new FilterRule();
        var repo = cfg.Repos?.FirstOrDefault(kv => string.Equals(kv.Key, repoKey, StringComparison.OrdinalIgnoreCase)).Value;
        if (repo is null) return global;
        if (!repo.UseGlobal) return repo;
        return new FilterRule
        {
            ExcludePlatforms = global.ExcludePlatforms.Union(repo.ExcludePlatforms, StringComparer.OrdinalIgnoreCase).ToList(),
            ExcludePatterns = global.ExcludePatterns.Concat(repo.ExcludePatterns).ToList(),
            IncludePatterns = global.IncludePatterns.Concat(repo.IncludePatterns).ToList(),
            SourceZip = repo.SourceZip ?? global.SourceZip,
            SourceTarGz = repo.SourceTarGz ?? global.SourceTarGz,
            ReleaseLimit = repo.ReleaseLimit ?? global.ReleaseLimit,
        };
    }

    public FilterDecision Evaluate(string fileName)
    {
        // GitHub's generated "Source code" archives follow their own switches, not the patterns/tags
        switch (Archives.Classify(fileName, _repoKey))
        {
            case Archives.SourceKind.Zip:
                return new(SourceZip, SourceZip ? "source code (zip)" : "source zip off", ["source"]);
            case Archives.SourceKind.TarGz:
                return new(SourceTarGz, SourceTarGz ? "source code (tar.gz)" : "source tar.gz off", ["source"]);
        }

        var tags = Detect(fileName);

        foreach (var (text, rx) in _include)
            if (rx.IsMatch(fileName)) return new(true, $"always-keep '{text}'", tags);

        foreach (var (text, rx) in _exclude)
            if (rx.IsMatch(fileName)) return new(false, $"pattern '{text}'", tags);

        if (_excludedTags.Count > 0 && tags.Count > 0)
        {
            var byTag = tags.Select(Find).Where(t => t is not null).Select(t => t!).ToList();

            // Kind tags (source/checksums/debug): any excluded one skips the asset.
            var kind = byTag.FirstOrDefault(t => t.Kind == TagKind.Kind && _excludedTags.Contains(t.Id));
            if (kind is not null) return new(false, $"kind {kind.Label}", tags);

            // OS and arch tags: skip only if every tag of that group is excluded, so a file named
            // "app-win-linux.zip" survives an exclude on linux, and "apple2-emu-win64.zip" isn't lost.
            foreach (var group in new[] { TagKind.Os, TagKind.Arch })
            {
                var g = byTag.Where(t => t.Kind == group).ToList();
                if (g.Count > 0 && g.All(t => _excludedTags.Contains(t.Id)))
                    return new(false, $"{(group == TagKind.Os ? "platform" : "arch")} {string.Join("+", g.Select(t => t.Label))}", tags);
            }
        }

        return new(true, tags.Count == 0 ? "universal" : "ok", tags);
    }

    public bool Keep(string fileName) => Evaluate(fileName).Keep;

    private static List<(string, Regex)> Compile(IEnumerable<string> patterns)
    {
        var list = new List<(string, Regex)>();
        foreach (var raw in patterns)
        {
            var p = raw?.Trim();
            if (string.IsNullOrEmpty(p)) continue;
            var rx = TryCompile(p);
            if (rx is not null) list.Add((p, rx));
        }
        return list;
    }

    /// <summary>"re:..." = regex; anything else = glob. Globs without wildcards match as "contains".</summary>
    public static Regex? TryCompile(string pattern)
    {
        try
        {
            const RegexOptions opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            if (pattern.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
                return new Regex(pattern[3..], opts, TimeSpan.FromMilliseconds(250));

            var hasWildcard = pattern.IndexOfAny(['*', '?']) >= 0;
            var body = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
            return new Regex(hasWildcard ? $"^{body}$" : body, opts, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
