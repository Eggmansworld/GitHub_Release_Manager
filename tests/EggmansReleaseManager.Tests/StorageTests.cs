using System.Xml.Linq;
using EggmansReleaseManager.Models;
using EggmansReleaseManager.Services;
using EggmansReleaseManager.ViewModels;
using Xunit;

namespace EggmansReleaseManager.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "egrm_test_" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class StorageTests
{
    private const string V3Db = """
    {
      "repos": {
        "Digitoxin1/DiskImageTool": {
          "last_checked": "2026-11-05_10_43_09Z",
          "last_result": "Up to date",
          "assets": {
            "1.18.0.1": {
              "DiskImageTool_1.18.0.1.zip": {
                "size_bytes": 157908,
                "sha1": "1f06c5e701898e6f5f96a3e347cfec40501d98e0",
                "path": "D:/Github Downloader\\Digitoxin1_DiskImageTool\\1.18.0.1\\DiskImageTool_1.18.0.1.zip",
                "downloaded_at": "2026-08-13_23_53_18Z",
                "hash_rescanned_at": "2026-09-01_00_00_00Z",
                "some_future_field": 42
              }
            }
          }
        },
        "zortos293/Lichii": { "last_checked": null, "last_result": "", "assets": {} }
      }
    }
    """;

    private const string V3Config = """
    {
      "repo": "zortos293/Lichii",
      "root_folder": "D:/Github Downloader",
      "max_threads": 5,
      "skip_existing": true,
      "discord_webhook": "",
      "github_token": "",
      "auto_update_enabled": true,
      "auto_update_interval": 360
    }
    """;

    [Fact]
    public void Reads_v3_db_and_round_trips_unknown_fields()
    {
        using var t = new TempDir();
        AppPaths.BaseDir = t.Path;
        File.WriteAllText(AppPaths.RepoDbPath, V3Db);

        var db = Stores.LoadRepoDb();
        Assert.Equal(2, db.Repos.Count);
        var meta = db.Repos["Digitoxin1/DiskImageTool"].Assets["1.18.0.1"]["DiskImageTool_1.18.0.1.zip"];
        Assert.Equal(157908, meta.SizeBytes);
        Assert.Equal("2026-09-01_00_00_00Z", meta.HashRescannedAt);
        Assert.Null(db.Repos["zortos293/Lichii"].LastChecked);

        Stores.SaveRepoDb(db);
        var text = File.ReadAllText(AppPaths.RepoDbPath);
        Assert.Contains("\"size_bytes\": 157908", text);
        Assert.Contains("\"some_future_field\": 42", text);
        Assert.Contains("\"last_checked\": null", text);
        Assert.DoesNotContain("indexed_at", text); // null optional fields are not written

        var again = Stores.LoadRepoDb();
        Assert.Equal(meta.Sha1, again.Repos["Digitoxin1/DiskImageTool"].Assets["1.18.0.1"]["DiskImageTool_1.18.0.1.zip"].Sha1);
    }

    [Fact]
    public void Reads_v3_config_and_adds_v4_defaults()
    {
        using var t = new TempDir();
        AppPaths.BaseDir = t.Path;
        File.WriteAllText(AppPaths.ConfigPath, V3Config);

        var cfg = Stores.LoadConfig();
        Assert.Equal("D:/Github Downloader", cfg.RootFolder);
        Assert.Equal(360, cfg.AutoUpdateInterval);
        Assert.True(cfg.AutoUpdateEnabled);
        Assert.Equal("ansi", cfg.Theme);
        Assert.NotNull(cfg.Filters.Global);

        cfg.Filters.Global.ExcludePlatforms.Add("linux");
        cfg.Filters.Repos["zortos293/Lichii"] = new RepoFilterRule { UseGlobal = false, ExcludePatterns = ["*-pgo*"] };
        Stores.SaveConfig(cfg);
        var text = File.ReadAllText(AppPaths.ConfigPath);
        Assert.Contains("\"root_folder\": \"D:/Github Downloader\"", text);
        Assert.Contains("\"exclude_platforms\"", text);

        var back = Stores.LoadConfig();
        Assert.Equal(["linux"], back.Filters.Global.ExcludePlatforms);
        Assert.False(back.Filters.Repos["zortos293/Lichii"].UseGlobal);
    }

    [Fact]
    public void Tracked_list_merges_and_writes_sorted()
    {
        using var t = new TempDir();
        AppPaths.BaseDir = t.Path;
        File.WriteAllText(AppPaths.TrackedListPath, "b/repo\na/repo\n\nnot-a-repo\n");
        var db = new RepoDb();
        Assert.Equal(2, Stores.MergeTrackedList(db));
        Stores.WriteTrackedList(db);
        Assert.Equal("a/repo\nb/repo\n", File.ReadAllText(AppPaths.TrackedListPath));
    }

    [Fact]
    public void Stamp_parses_v3_format()
    {
        var d = Stamp.Parse("2026-11-05_10_43_09Z");
        Assert.Equal(new DateTime(2026, 11, 5, 10, 43, 9, DateTimeKind.Utc), d);
        Assert.Equal("2026-11-05 10:43", Stamp.Pretty("2026-11-05_10_43_09Z"));
    }

    [Theory]
    [InlineData("Up to date", RepoStatus.UpToDate)]
    [InlineData("Up to date (12 filtered)", RepoStatus.UpToDate)]
    [InlineData("OK=2, Failed=0", RepoStatus.Changed)]
    [InlineData("OK=0, Failed=0", RepoStatus.UpToDate)]
    [InlineData("OK=2, Failed=1", RepoStatus.Failed)]
    [InlineData("Stopped: OK=1, Failed=0, Filtered=4", RepoStatus.Changed)]
    [InlineData("Error: HTTP 404", RepoStatus.Failed)]
    [InlineData("", RepoStatus.Unchecked)]
    public void Repo_status_colours(string lastResult, RepoStatus expected) =>
        Assert.Equal(expected, RepoRow.StatusFrom(lastResult));

    [Theory]
    [InlineData("https://github.com/Eggmansworld/Datfiles", "Eggmansworld/Datfiles")]
    [InlineData("https://github.com/zortos293/Lichii/releases/tag/v1", "zortos293/Lichii")]
    [InlineData("github.com/a/b.git", "a/b")]
    [InlineData("owner/repo", "owner/repo")]
    [InlineData("nope", null)]
    public void Normalizes_repo_input(string input, string? expected) =>
        Assert.Equal(expected, GitHubApi.NormalizeRepo(input));
}

public class ExportTests
{
    [Fact]
    public void Hashes_match_known_values()
    {
        using var t = new TempDir();
        var f = Path.Combine(t.Path, "abc.bin");
        File.WriteAllText(f, "The quick brown fox jumps over the lazy dog");
        Assert.Equal("2fd4e1c67a2d28fced849ee1bb76e7391b93eb12", Hashing.Sha1File(f));
        Assert.Equal("414fa339", Hashing.Crc32File(f));
    }

    [Fact]
    public void Dat_is_valid_xml_with_escaped_names_and_relative_paths()
    {
        using var t = new TempDir();
        var root = t.Path;
        var tagDir = Path.Combine(root, "o_r", "v1&2");
        Directory.CreateDirectory(Path.Combine(tagDir, "sub"));
        var f1 = Path.Combine(tagDir, "a&b's.zip");
        var f2 = Path.Combine(tagDir, "sub", "c.bin");
        File.WriteAllText(f1, "x");
        File.WriteAllText(f2, "y");

        var info = new RepoInfo
        {
            Assets =
            {
                ["v1&2"] = new()
                {
                    ["a&b's.zip"] = new AssetMeta { SizeBytes = 1, Sha1 = Hashing.Sha1File(f1), Path = f1 },
                    ["c.bin"] = new AssetMeta { SizeBytes = 1, Sha1 = "ABC", Path = f2 },
                    ["gone.zip"] = new AssetMeta { SizeBytes = 1, Sha1 = "x", Path = Path.Combine(tagDir, "gone.zip") },
                },
            },
        };

        var dat = Exporters.GenerateDat("o/r", info, root);
        var doc = XDocument.Load(dat);
        var game = doc.Root!.Element("game")!;
        Assert.Equal("v1&2", game.Attribute("name")!.Value);
        var roms = game.Elements("rom").ToList();
        Assert.Equal(2, roms.Count);
        Assert.Equal("a&b's.zip", roms[0].Attribute("name")!.Value);
        Assert.Equal("sub/c.bin", roms[1].Attribute("name")!.Value);
        Assert.Equal("abc", roms[1].Attribute("sha1")!.Value);
        Assert.Equal(8, roms[0].Attribute("crc")!.Value.Length);
        Assert.NotNull(info.Assets["v1&2"]["a&b's.zip"].Crc); // cached
    }

    [Fact]
    public void Pdf_summary_is_a_pdf()
    {
        using var t = new TempDir();
        var info = new RepoInfo();
        info.Assets["v1"] = Enumerable.Range(0, 200).ToDictionary(i => $"file{i}.zip", i => new AssetMeta { SizeBytes = i, Sha1 = "deadbeef" });
        var path = Exporters.GenerateSummaryPdf("o/r", info, t.Path);
        var bytes = File.ReadAllBytes(path);
        Assert.StartsWith("%PDF-1.4", System.Text.Encoding.ASCII.GetString(bytes, 0, 8));
        Assert.EndsWith("%%EOF\n", System.Text.Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Orphans_ignore_release_notes_and_known_files()
    {
        using var t = new TempDir();
        var tag = Path.Combine(t.Path, "o_r", "v1");
        Directory.CreateDirectory(tag);
        var known = Path.Combine(tag, "known.zip");
        File.WriteAllText(known, "k");
        File.WriteAllText(Path.Combine(tag, "release_notes.txt"), "n");
        File.WriteAllText(Path.Combine(tag, "stray.zip"), "s");
        var info = new RepoInfo { Assets = { ["v1"] = new() { ["known.zip"] = new AssetMeta { Path = known.Replace('\\', '/') } } } };

        var orphans = RepoMaintenance.FindOrphans("o/r", t.Path, info);
        Assert.Single(orphans);
        Assert.EndsWith("stray.zip", orphans[0]);
    }

    [Fact]
    public void Plan_applies_filter_and_skip_rules()
    {
        using var t = new TempDir();
        var root = t.Path;
        var releases = new List<GhRelease>
        {
            new()
            {
                TagName = "v2", Body = "notes",
                Assets =
                [
                    new GhAsset { Name = "app-windows-x64.zip", Size = 10, BrowserDownloadUrl = "u1" },
                    new GhAsset { Name = "app-linux-x64.tar.gz", Size = 10, BrowserDownloadUrl = "u2" },
                ],
            },
            new()
            {
                TagName = "v1-linux-only",
                Assets = [new GhAsset { Name = "app-linux-x64.AppImage", Size = 10, BrowserDownloadUrl = "u3" }],
            },
        };
        var info = new RepoInfo();
        var filter = new AssetFilter(new FilterRule { ExcludePlatforms = ["linux"] });

        var plan = RepoMaintenance.Plan("o/r", releases, info, root, skipExisting: true, filter);
        Assert.Equal(3, plan.TotalSeen);
        Assert.Equal(2, plan.Filtered);
        Assert.Single(plan.Jobs);
        Assert.Equal("app-windows-x64.zip", plan.Jobs[0].Asset.Name);
        Assert.True(File.Exists(Path.Combine(root, "o_r", "v2", "release_notes.txt")));
        Assert.False(Directory.Exists(Path.Combine(root, "o_r", "v1-linux-only"))); // fully filtered release: no folder

        // complete file + DB entry => nothing to do
        File.WriteAllBytes(plan.Jobs[0].FilePath, new byte[10]);
        info.Assets["v2"] = new() { ["app-windows-x64.zip"] = new AssetMeta { SizeBytes = 10, Path = plan.Jobs[0].FilePath } };
        var plan2 = RepoMaintenance.Plan("o/r", releases, info, root, skipExisting: true, filter);
        Assert.Empty(plan2.Jobs);
    }
}
