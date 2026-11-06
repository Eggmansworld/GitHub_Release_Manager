using System.Text;
using EggmansReleaseManager.Models;

namespace EggmansReleaseManager.Services;

public sealed record UpdatePlan(List<DownloadJob> Jobs, int TotalSeen, int Filtered, int Releases, List<string> Warnings)
{
    /// <summary>Assets the filters skipped, with a short reason tag.</summary>
    public List<Nuke> Nukes { get; init; } = [];

    public long TotalBytes => Jobs.Sum(j => j.Asset.Size);
}

public static class RepoMaintenance
{
    public const string ReleaseNotesFile = "release_notes.txt";
    private static readonly string[] NonAssetFiles = [ReleaseNotesFile, Diz.FileName];

    /// <summary>
    /// Works out which assets need downloading (same rules as v3) after applying the asset filter,
    /// and writes release_notes.txt into each tag folder that has something kept.
    /// </summary>
    public static UpdatePlan Plan(string repoKey, IReadOnlyList<GhRelease> releases, RepoInfo info,
        string rootFolder, bool skipExisting, AssetFilter filter, string? dizHandle = null)
    {
        var repoRoot = AppPaths.RepoRoot(rootFolder, repoKey);
        Directory.CreateDirectory(repoRoot);
        var jobs = new List<DownloadJob>();
        var warnings = new List<string>();
        var nukes = new List<Nuke>();
        int seen = 0, filtered = 0;

        foreach (var rel in releases)
        {
            if (rel.Draft) continue;
            var tag = rel.FolderTag;
            var tagFolder = Path.Combine(repoRoot, AppPaths.SafeTagFolder(tag));

            var kept = new List<GhAsset>();
            foreach (var a in rel.Assets)
            {
                seen++;
                var d = filter.Evaluate(a.Name);
                if (d.Keep) kept.Add(a);
                else
                {
                    filtered++;
                    nukes.Add(new Nuke(tag, a.Name, a.Size, Nuke.ReasonTag(d.Reason)));
                }
            }

            // Every asset of this release was filtered out -> don't create a folder at all.
            if (rel.Assets.Count > 0 && kept.Count == 0) continue;

            Directory.CreateDirectory(tagFolder);
            try { WriteReleaseNotes(repoKey, rel, tagFolder); }
            catch (Exception ex) { warnings.Add($"release notes {tag}: {ex.Message}"); }
            if (dizHandle is not null)
            {
                try { Diz.Write(tagFolder, Diz.Build(repoKey, tag, rel.PublishedAt, kept.Count, kept.Sum(k => k.Size), dizHandle)); }
                catch (Exception ex) { warnings.Add($"FILE_ID.DIZ {tag}: {ex.Message}"); }
            }

            info.Assets.TryGetValue(tag, out var tagDb);
            foreach (var asset in kept)
            {
                var filePath = Path.Combine(tagFolder, asset.Name);
                AssetMeta? entry = null;
                tagDb?.TryGetValue(asset.Name, out entry);

                bool needs;
                if (!File.Exists(filePath)) needs = true;
                else
                {
                    var local = new FileInfo(filePath).Length;
                    if (local < asset.Size) needs = true;                              // partial -> resume
                    else if (entry is not null && entry.SizeBytes != asset.Size) needs = true; // changed upstream
                    else if (entry is null) needs = true;                              // on disk but untracked -> hash it in
                    else needs = !skipExisting;
                }
                if (needs) jobs.Add(new DownloadJob(asset, tagFolder, tag));
            }
        }
        return new UpdatePlan(jobs, seen, filtered, releases.Count, warnings) { Nukes = nukes };
    }

    private static void WriteReleaseNotes(string repoKey, GhRelease rel, string tagFolder)
    {
        var path = Path.Combine(tagFolder, ReleaseNotesFile);
        var sb = new StringBuilder();
        sb.Append($"Release Notes for {repoKey} — {rel.FolderTag}\n");
        sb.Append($"Published: {rel.PublishedAt}\n\n");
        sb.Append("------------------------------------------------------------\n\n");
        sb.Append(string.IsNullOrWhiteSpace(rel.Body) ? "(No release description provided.)\n" : rel.Body);
        var text = sb.ToString();

        // Only touch the file when it changed, so timestamps stay meaningful.
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    // ---------------- Orphans ----------------

    public static List<string> FindOrphans(string repoKey, string rootFolder, RepoInfo info)
    {
        var repoRoot = AppPaths.RepoRoot(rootFolder, repoKey);
        if (!Directory.Exists(repoRoot)) return [];
        var known = new HashSet<string>(
            info.Assets.Values.SelectMany(t => t.Values).Select(m => AppPaths.Normalize(m.Path)).Where(p => p.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        var orphanDir = Path.Combine(repoRoot, "orphans");

        return Directory.EnumerateFiles(repoRoot, "*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(orphanDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(f => !IsReservedFolder(Path.GetRelativePath(repoRoot, f).Split(Path.DirectorySeparatorChar)[0]))
            .Where(f => !NonAssetFiles.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !known.Contains(AppPaths.Normalize(f)))
            .ToList();
    }

    public static (int Moved, string OrphanRoot) MoveOrphans(string repoKey, string rootFolder, IEnumerable<string> orphans)
    {
        var repoRoot = AppPaths.RepoRoot(rootFolder, repoKey);
        var orphanRoot = Path.Combine(repoRoot, "orphans");
        Directory.CreateDirectory(orphanRoot);
        var moved = 0;
        foreach (var p in orphans)
        {
            try
            {
                var dest = Path.Combine(orphanRoot, Path.GetRelativePath(repoRoot, p));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(p, dest, true);
                moved++;
            }
            catch { /* ignore */ }
        }
        return (moved, orphanRoot);
    }

    // ---------------- Filter cleanup ----------------

    public const string FilteredFolder = "_filtered";

    public sealed record FilteredFile(string Repo, string Path, long Size, string Reason);

    public enum CleanupMode { RecycleBin, MoveToFiltered }

    /// <summary>Files already on disk (in tag folders) that the current filter rules would skip.</summary>
    public static List<FilteredFile> FindFilteredLocal(IEnumerable<string> repos, FilterConfig filters, string rootFolder, CancellationToken ct = default)
    {
        var result = new List<FilteredFile>();
        foreach (var repo in repos)
        {
            var filter = AssetFilter.For(filters, repo);
            if (filter.IsEmpty) continue;
            var repoRoot = AppPaths.RepoRoot(rootFolder, repo);
            if (!Directory.Exists(repoRoot)) continue;

            foreach (var file in Directory.EnumerateFiles(repoRoot, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var parts = Path.GetRelativePath(repoRoot, file).Split(Path.DirectorySeparatorChar);
                if (parts.Length < 2) continue;
                if (IsReservedFolder(parts[0])) continue;
                var name = Path.GetFileName(file);
                if (NonAssetFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                var d = filter.Evaluate(name);
                if (d.Keep) continue;
                long size = 0;
                try { size = new FileInfo(file).Length; } catch { /* ignore */ }
                result.Add(new FilteredFile(repo, file, size, d.Reason));
            }
        }
        return result;
    }

    private static bool IsReservedFolder(string top) =>
        top.Equals("orphans", StringComparison.OrdinalIgnoreCase) || top.Equals(FilteredFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Recycles (or moves to owner_repo\_filtered\) the given files, removes them from the DB, and removes tag
    /// folders left holding nothing but release_notes.txt. Returns (handled, failed, foldersRemoved).
    /// </summary>
    public static (int Done, int Failed, int Folders) CleanupFiltered(IReadOnlyList<FilteredFile> files, RepoDb db,
        string rootFolder, CleanupMode mode)
    {
        var done = new List<FilteredFile>();
        var failed = 0;

        if (mode == CleanupMode.RecycleBin)
        {
            // one shell call per repo keeps the Recycle Bin entries grouped
            foreach (var group in files.GroupBy(f => f.Repo))
            {
                var list = group.ToList();
                if (RecycleBin.Send(list.Select(f => f.Path)))
                {
                    foreach (var f in list)
                        if (!File.Exists(f.Path)) done.Add(f); else failed++;
                }
                else failed += list.Count;
            }
        }
        else
        {
            foreach (var f in files)
            {
                try
                {
                    var repoRoot = AppPaths.RepoRoot(rootFolder, f.Repo);
                    var dest = Path.Combine(repoRoot, FilteredFolder, Path.GetRelativePath(repoRoot, f.Path));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Move(f.Path, dest, true);
                    done.Add(f);
                }
                catch { failed++; }
            }
        }

        // drop DB records for files that are gone
        var gone = new HashSet<string>(done.Select(f => AppPaths.Normalize(f.Path)), StringComparer.OrdinalIgnoreCase);
        foreach (var repo in done.Select(f => f.Repo).Distinct())
        {
            if (!db.Repos.TryGetValue(repo, out var info)) continue;
            foreach (var (tag, assets) in info.Assets.ToList())
            {
                foreach (var name in assets.Where(a => gone.Contains(AppPaths.Normalize(a.Value.Path))).Select(a => a.Key).ToList())
                    assets.Remove(name);
                if (assets.Count == 0) info.Assets.Remove(tag);
            }
        }

        // tidy tag folders that now only hold release notes (same as a fully-filtered release on update)
        var folders = 0;
        foreach (var dir in done.Select(f => Path.GetDirectoryName(f.Path)!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var left = Directory.EnumerateFileSystemEntries(dir).ToList();
                if (left.All(e => File.Exists(e) && NonAssetFiles.Contains(Path.GetFileName(e), StringComparer.OrdinalIgnoreCase)))
                {
                    foreach (var e in left) File.Delete(e);   // release_notes.txt is regenerated from GitHub anyway
                    Directory.Delete(dir);
                    folders++;
                }
            }
            catch { /* leave it */ }
        }

        return (done.Count, failed, folders);
    }

    // ---------------- Hash rescan ----------------

    public static (int Updated, int Missing) ForceHashRescan(RepoInfo info, IProgress<string>? progress, CancellationToken ct)
    {
        int count = 0, missing = 0;
        foreach (var (tag, files) in info.Assets)
            foreach (var (name, meta) in files)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(meta.Path) || !File.Exists(meta.Path)) { missing++; continue; }
                try
                {
                    progress?.Report($"{tag}/{name}");
                    meta.Sha1 = Hashing.Sha1File(meta.Path, ct);
                    meta.SizeBytes = new FileInfo(meta.Path).Length;
                    meta.Crc = null; // recomputed on next DAT
                    meta.HashRescannedAt = Stamp.Now();
                    count++;
                }
                catch (OperationCanceledException) { throw; }
                catch { missing++; }
            }
        return (count, missing);
    }

    // ---------------- Scan local files ----------------

    public sealed record ScanResult(int Total, int Added, int Updated, int Skipped);

    /// <summary>Index files on disk using the top-level folder under the repo folder as the tag (v3 behaviour).</summary>
    public static ScanResult ScanLocal(string repoKey, string rootFolder, RepoInfo info, IProgress<string>? progress, CancellationToken ct)
    {
        var repoRoot = AppPaths.RepoRoot(rootFolder, repoKey);
        int total = 0, added = 0, updated = 0, skipped = 0;

        foreach (var file in Directory.EnumerateFiles(repoRoot, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(repoRoot, file);
            var parts = rel.Split(Path.DirectorySeparatorChar);
            if (parts.Length < 2) continue;                       // files directly in repo root
            var tag = parts[0];
            if (IsReservedFolder(tag)) continue;
            var fn = Path.GetFileName(file);
            if (NonAssetFiles.Contains(fn, StringComparer.OrdinalIgnoreCase)) continue;

            total++;
            progress?.Report(rel);
            long size;
            string sha;
            try
            {
                size = new FileInfo(file).Length;
                sha = Hashing.Sha1File(file, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { continue; }

            if (!info.Assets.TryGetValue(tag, out var tagAssets))
                info.Assets[tag] = tagAssets = new Dictionary<string, AssetMeta>();

            if (tagAssets.TryGetValue(fn, out var existing))
            {
                if (existing.SizeBytes == size && existing.Sha1 == sha &&
                    string.Equals(AppPaths.Normalize(existing.Path), AppPaths.Normalize(file), StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }
                updated++;
            }
            else added++;

            tagAssets[fn] = new AssetMeta { SizeBytes = size, Sha1 = sha, Path = file, IndexedAt = Stamp.Now() };
        }
        return new ScanResult(total, added, updated, skipped);
    }
}
