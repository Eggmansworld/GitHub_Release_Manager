using EggmansReleaseManager.Models;
using EggmansReleaseManager.Services;
using Xunit;

namespace EggmansReleaseManager.Tests;

public class ArchiveTests
{
    private static GhRelease Rel(string tag, params string[] assets) => new()
    {
        TagName = tag,
        PublishedAt = "2026-11-05T10:43:09Z",
        Assets = assets.Select(a => new GhAsset { Name = a, Size = 10, BrowserDownloadUrl = "u" }).ToList(),
    };

    [Fact]
    public void Source_names_and_urls()
    {
        Assert.Equal("Lichii-v0.4.2_source.zip", Archives.SourceName("zortos293/Lichii", "v0.4.2", Archives.SourceKind.Zip));
        Assert.Equal("Lichii-v0.4.2_source.tar.gz", Archives.SourceName("zortos293/Lichii", "v0.4.2", Archives.SourceKind.TarGz));
        Assert.Equal("https://github.com/zortos293/Lichii/archive/refs/tags/v0.4.2.tar.gz",
            Archives.SourceUrl("zortos293/Lichii", "v0.4.2", Archives.SourceKind.TarGz));
        Assert.Equal("https://github.com/o/r/archive/refs/tags/release/1%2B2.zip",
            Archives.SourceUrl("o/r", "release/1+2", Archives.SourceKind.Zip));
        Assert.Equal("r-release_1+2_source.zip", Archives.SourceName("o/r", "release/1+2", Archives.SourceKind.Zip));
    }

    [Fact]
    public void Classify_only_matches_generated_names_when_repo_known()
    {
        Assert.Equal(Archives.SourceKind.Zip, Archives.Classify("Lichii-v1_source.zip", "zortos293/Lichii"));
        Assert.Equal(Archives.SourceKind.TarGz, Archives.Classify("Lichii-v1_source.tar.gz", "zortos293/Lichii"));
        Assert.Equal(Archives.SourceKind.None, Archives.Classify("tools_source.zip", "zortos293/Lichii"));
        Assert.Equal(Archives.SourceKind.None, Archives.Classify("Lichii-v1.zip", "zortos293/Lichii"));
    }

    [Fact]
    public void Source_switches_beat_patterns_and_kind_tags()
    {
        var rule = new FilterRule { SourceZip = true, ExcludePlatforms = ["source"], ExcludePatterns = ["*.zip"] };
        var f = new AssetFilter(rule, "o/r");
        Assert.True(f.Evaluate("r-v1_source.zip").Keep);
        Assert.False(f.Evaluate("r-v1_source.tar.gz").Keep);
        Assert.False(f.Evaluate("r-v1-win.zip").Keep);
    }

    [Fact]
    public void Repo_source_settings_inherit_global_unless_set()
    {
        var cfg = new FilterConfig { Global = new FilterRule { SourceZip = true } };
        cfg.Repos["o/a"] = new RepoFilterRule { SourceTarGz = true };                     // inherits zip
        cfg.Repos["o/b"] = new RepoFilterRule { SourceZip = false };                      // turns zip off
        cfg.Repos["o/c"] = new RepoFilterRule { UseGlobal = false };                      // own rules: nothing

        var a = AssetFilter.For(cfg, "o/a");
        Assert.True(a.SourceZip && a.SourceTarGz);
        Assert.False(AssetFilter.For(cfg, "o/b").SourceZip);
        Assert.False(AssetFilter.For(cfg, "o/c").SourceZip);
        Assert.True(AssetFilter.For(cfg, "o/none").SourceZip);
        Assert.False(new RepoFilterRule { SourceZip = false }.IsEmpty);
        Assert.False(new RepoFilterRule { Snapshot = true }.IsEmpty);
        Assert.True(new RepoFilterRule().IsEmpty);
    }

    [Fact]
    public void Plan_adds_source_jobs_for_every_release_including_assetless_ones()
    {
        using var t = new TempDir();
        var filter = new AssetFilter(new FilterRule { SourceZip = true, ExcludePatterns = ["*"] }, "o/r");
        var info = new RepoInfo();
        var plan = RepoMaintenance.Plan("o/r", [Rel("v2", "app.zip"), Rel("v1")], info, t.Path, true, filter);

        Assert.Equal(["r-v2_source.zip", "r-v1_source.zip"], plan.Jobs.Select(j => j.Asset.Name));
        Assert.All(plan.Jobs, j => Assert.True(j.Asset.SizeUnknown));
        Assert.Single(plan.Nukes);                       // app.zip, not the source archive

        // already downloaded + in the DB -> nothing to do; on disk but untracked -> hash only
        var v2 = Path.Combine(AppPaths.RepoRoot(t.Path, "o/r"), "v2");
        File.WriteAllText(Path.Combine(v2, "r-v2_source.zip"), "x");
        info.Assets["v2"] = new() { ["r-v2_source.zip"] = new AssetMeta { SizeBytes = 1, Path = Path.Combine(v2, "r-v2_source.zip") } };
        var v1 = Path.Combine(AppPaths.RepoRoot(t.Path, "o/r"), "v1");
        File.WriteAllText(Path.Combine(v1, "r-v1_source.zip"), "y");
        var again = RepoMaintenance.Plan("o/r", [Rel("v2", "app.zip"), Rel("v1")], info, t.Path, true, filter);
        var job = Assert.Single(again.Jobs);
        Assert.Equal("r-v1_source.zip", job.Asset.Name);
        Assert.True(job.HashOnly);
    }

    [Fact]
    public void Source_off_by_default_changes_nothing()
    {
        using var t = new TempDir();
        var plan = RepoMaintenance.Plan("o/r", [Rel("v1", "app.zip")], new RepoInfo(), t.Path, true, new AssetFilter(new FilterRule(), "o/r"));
        Assert.Equal(["app.zip"], plan.Jobs.Select(j => j.Asset.Name));
    }

    [Fact]
    public void Snapshot_naming_and_scan_local_keeps_each_snapshot_separate()
    {
        Assert.Equal("2026-11-07_09-05-03", Archives.SnapshotStamp(new DateTime(2026, 11, 7, 9, 5, 3)));
        Assert.Equal("_snapshots/2026-11-07_09-05-03", Archives.SnapshotTag("2026-11-07_09-05-03"));
        var a = Archives.SnapshotAsset("octocat/Hello-World", "feature/x", "abc123");
        Assert.Equal("Hello-World-feature-x.zip", a.Name);
        Assert.Equal("https://github.com/octocat/Hello-World/archive/abc123.zip", a.BrowserDownloadUrl);

        using var t = new TempDir();
        foreach (var stamp in new[] { "2026-11-01_10-00-00", "2026-11-07_10-00-00" })
        {
            var dir = Archives.SnapshotFolder(t.Path, "o/r", stamp);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "r-main.zip"), stamp);
            File.WriteAllText(Path.Combine(dir, Archives.SnapshotInfoFile), "info");
        }
        var info = new RepoInfo();
        var scan = RepoMaintenance.ScanLocal("o/r", t.Path, info, null, CancellationToken.None);
        Assert.Equal(2, scan.Added);
        Assert.Contains("_snapshots/2026-11-01_10-00-00", info.Assets.Keys);
        Assert.Contains("_snapshots/2026-11-07_10-00-00", info.Assets.Keys);
        Assert.Empty(RepoMaintenance.FindOrphans("o/r", t.Path, info));
        Assert.Empty(RepoMaintenance.FindFilteredLocal(["o/r"], new FilterConfig(), t.Path));
    }

    [Fact]
    public void Failed_snapshot_folder_is_removed()
    {
        using var t = new TempDir();
        var dir = Path.Combine(t.Path, "snap");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "r-main.zip.part"), "partial");
        Archives.RemoveIfEmpty(dir);
        Assert.False(Directory.Exists(dir));
    }
}
