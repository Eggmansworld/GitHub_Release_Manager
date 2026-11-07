using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EggmansReleaseManager.Models;

namespace EggmansReleaseManager.Services;

/// <summary>
/// GitHub's auto-generated archives: the "Source code (zip / tar.gz)" of each release, and
/// "Code > Download ZIP" snapshots of the default branch.
/// </summary>
public static class Archives
{
    public const string ZipSuffix = "_source.zip";
    public const string TarGzSuffix = "_source.tar.gz";
    public const string SnapshotsFolder = "_snapshots";
    public const string SnapshotInfoFile = "snapshot_info.txt";

    public enum SourceKind { None, Zip, TarGz }

    private static string RepoName(string repoKey) => repoKey.Split('/').Last();

    /// <summary>"Lichii-v0.4.2_source.zip"</summary>
    public static string SourceName(string repoKey, string tag, SourceKind kind) =>
        $"{RepoName(repoKey)}-{AppPaths.SafeTagFolder(tag)}{(kind == SourceKind.Zip ? ZipSuffix : TarGzSuffix)}";

    public static string SourceUrl(string repoKey, string tag, SourceKind kind)
    {
        var escapedTag = string.Join('/', tag.Split('/').Select(Uri.EscapeDataString));
        return $"https://github.com/{repoKey}/archive/refs/tags/{escapedTag}{(kind == SourceKind.Zip ? ".zip" : ".tar.gz")}";
    }

    public static GhAsset SourceAsset(string repoKey, string tag, SourceKind kind) => new()
    {
        Name = SourceName(repoKey, tag, kind),
        BrowserDownloadUrl = SourceUrl(repoKey, tag, kind),
        SizeUnknown = true,
    };

    /// <summary>
    /// Is this one of our generated source archives? With a repo key it must be "&lt;repo&gt;-*_source.zip"
    /// (so an uploaded asset that happens to end in _source.zip isn't mistaken for one).
    /// </summary>
    public static SourceKind Classify(string fileName, string? repoKey)
    {
        var kind = fileName.EndsWith(ZipSuffix, StringComparison.OrdinalIgnoreCase) ? SourceKind.Zip
                 : fileName.EndsWith(TarGzSuffix, StringComparison.OrdinalIgnoreCase) ? SourceKind.TarGz
                 : SourceKind.None;
        if (kind == SourceKind.None || repoKey is null) return kind;
        return fileName.StartsWith(RepoName(repoKey) + "-", StringComparison.OrdinalIgnoreCase) ? kind : SourceKind.None;
    }

    // ───────── default-branch snapshots ─────────

    /// <summary>"2026-11-07_14-30-05" (local time)</summary>
    public static string SnapshotStamp(DateTime local) => local.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

    /// <summary>DB tag key for a snapshot: "_snapshots/2026-11-07_14-30-05".</summary>
    public static string SnapshotTag(string stamp) => $"{SnapshotsFolder}/{stamp}";

    public static bool IsSnapshotTag(string tag) => tag.StartsWith(SnapshotsFolder + "/", StringComparison.OrdinalIgnoreCase);

    public static string SnapshotFolder(string rootFolder, string repoKey, string stamp) =>
        Path.Combine(AppPaths.RepoRoot(rootFolder, repoKey), SnapshotsFolder, stamp);

    /// <summary>Asset for the branch as of <paramref name="sha"/>: "Lichii-main.zip".</summary>
    public static GhAsset SnapshotAsset(string repoKey, string branch, string sha) => new()
    {
        Name = $"{RepoName(repoKey)}-{AppPaths.SafeTagFolder(branch.Replace('/', '-'))}.zip",
        BrowserDownloadUrl = $"https://github.com/{repoKey}/archive/{sha}.zip",
        SizeUnknown = true,
    };

    public static void WriteSnapshotInfo(string folder, string repoKey, string branch, string sha, string handle)
    {
        var sb = new StringBuilder();
        sb.Append($"Snapshot of {repoKey}\r\n");
        sb.Append($"Branch ..... {branch}\r\n");
        sb.Append($"Commit ..... {sha}\r\n");
        sb.Append($"Taken ...... {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local)\r\n");
        sb.Append($"Source ..... https://github.com/{repoKey}/tree/{sha}\r\n");
        sb.Append($"Courier .... {handle} @ EGGNET\r\n");
        File.WriteAllText(Path.Combine(folder, SnapshotInfoFile), sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Removes a snapshot folder that never got its zip (failed/stopped download).</summary>
    public static void RemoveIfEmpty(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            var left = Directory.EnumerateFileSystemEntries(folder).ToList();
            if (left.All(f => Path.GetFileName(f).Equals(SnapshotInfoFile, StringComparison.OrdinalIgnoreCase) || f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var f in left) File.Delete(f);
                Directory.Delete(folder);
            }
        }
        catch { /* leave it */ }
    }
}
