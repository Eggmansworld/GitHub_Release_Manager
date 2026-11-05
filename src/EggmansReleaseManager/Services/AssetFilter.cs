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

    public AssetFilter(FilterRule rule)
    {
        _excludedTags = new HashSet<string>(rule.ExcludePlatforms, StringComparer.OrdinalIgnoreCase);
        _exclude = Compile(rule.ExcludePatterns);
        _include = Compile(rule.IncludePatterns);
        IsEmpty = _excludedTags.Count == 0 && _exclude.Count == 0 && _include.Count == 0;
    }

    /// <summary>Build the effective filter for a repo from the saved config.</summary>
    public static AssetFilter For(FilterConfig cfg, string repoKey) => new(Effective(cfg, repoKey));

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
        };
    }

    public FilterDecision Evaluate(string fileName)
    {
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
