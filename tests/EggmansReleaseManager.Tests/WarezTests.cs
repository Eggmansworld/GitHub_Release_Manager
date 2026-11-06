using EggmansReleaseManager.Models;
using EggmansReleaseManager.Services;
using EggmansReleaseManager.ViewModels;
using Xunit;

namespace EggmansReleaseManager.Tests;

public class WarezTests
{
    [Fact]
    public void Handle_is_capped_at_wwiv_length_and_falls_back_to_windows_user()
    {
        Assert.Equal(30, Handle.MaxLength);
        Assert.Equal(new string('x', 30), Handle.Clean(new string('x', 45)));
        Assert.Equal("The Cracksman", Handle.Clean("  The Cracksman \t"));
        Assert.Equal("Eggman", Handle.For(new AppConfig { Username = "Eggman" }));
        Assert.False(string.IsNullOrWhiteSpace(Handle.For(new AppConfig { Username = "   " })));
    }

    [Fact]
    public void Diz_is_45_columns_max_10_lines_plain_ascii()
    {
        var diz = Diz.Build("someone-with-a-very-long-name/an-even-longer-repository-name", "v1.2.3-ünicode",
            "2026-11-05T10:43:09Z", 9, 112_600_000, "The Cracksman of the Northern Wastes");
        var lines = diz.TrimEnd('\r', '\n').Split("\r\n");
        Assert.InRange(lines.Length, 1, 10);
        Assert.All(lines, l => Assert.Equal(Diz.Width, l.Length));
        Assert.All(diz, c => Assert.True(c < 128));
        Assert.Contains("2026-11-05", diz);
        Assert.Contains("9 (107.4 MB)", diz);
    }

    [Fact]
    public void Leet_translates_and_is_idempotent()
    {
        var once = Leet.Apply("Welcome to the ELITE warez files, you lamer");
        Assert.Equal("W3lc0m3 70 73h 31337 w4r3z f1l3z, j00 l4m3r", once);
        Assert.Equal(once, Leet.Apply(once));
        Assert.Equal("59.4 GB", Leet.Apply("59.4 GB").Replace("G", "G")); // digits untouched
    }

    [Fact]
    public void Zipscript_lines_look_like_pzs_ng()
    {
        Assert.Equal("[EGG] - ( 107.4MB 9F - COMPLETE ) - [EGG]", Zipscript.Complete(112_620_000, 9));
        Assert.Equal("[EGG] -  44% Complete - 4 of 9 files - [EGG]", Zipscript.Racing(4, 9, 44));
        Assert.Equal("[EGG] - ( 7 of 9F - INCOMPLETE ) - [EGG]", Zipscript.Incomplete(7, 9));
    }

    [Fact]
    public void Nuke_reasons_become_dotted_tags_and_summarize()
    {
        Assert.Equal("platform.linux", Nuke.ReasonTag("platform LiNUX"));
        Assert.Equal("pattern.*-pgo*", Nuke.ReasonTag("pattern '*-pgo*'"));
        var n = new[] { "a", "b", "c" }.Select(x => new Nuke("v1", x, 1, "platform.linux"))
            .Append(new Nuke("v1", "d", 1, "kind.checksums")).ToList();
        Assert.Equal("platform.linux x3, kind.checksums x1", Nuke.Summary(n));
    }

    [Fact]
    public void Race_item_counts_to_complete_or_incomplete()
    {
        var r = new RaceItem { Repo = "o/r", Tag = "v1", TotalFiles = 2, TotalBytes = 300 };
        r.Refresh();
        Assert.True(r.IsRacing);
        r.Add(true, 100);
        Assert.False(r.IsFinishedCount);
        Assert.Contains("1 of 2 files", r.Line);
        r.Add(false, 200);
        Assert.True(r.IsFinishedCount);
        r.Finish(RaceState.Incomplete);
        Assert.True(r.IsBad);
        Assert.Contains("INCOMPLETE", r.Line);
    }

    [Fact]
    public void Top_board_ranks_by_bytes_within_the_period()
    {
        var now = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Local);   // a Friday
        Assert.Equal(new DateTime(2026, 11, 2), TopBoard.WeekStart(now).Date);
        Assert.Equal(new DateTime(2026, 11, 1), TopBoard.MonthStart(now).Date);

        string At(DateTime local) => local.ToUniversalTime().ToString(Stamp.Format, System.Globalization.CultureInfo.InvariantCulture);
        var db = new RepoDb();
        db.Ensure("a/old").Assets["v1"] = new() { ["x.zip"] = new AssetMeta { SizeBytes = 900, DownloadedAt = At(now.AddDays(-20)) } };
        db.Ensure("b/new").Assets["v1"] = new() { ["y.zip"] = new AssetMeta { SizeBytes = 100, DownloadedAt = At(now.AddHours(-2)) } };
        db.Ensure("c/scanned").Assets["v1"] = new() { ["z.zip"] = new AssetMeta { SizeBytes = 50 } };   // indexed, never downloaded

        Assert.Equal(["b/new"], TopBoard.Compute(db, TopBoard.WeekStart(now)).Select(e => e.Repo));
        Assert.Equal(["a/old", "b/new", "c/scanned"], TopBoard.Compute(db, null).Select(e => e.Repo));
    }

    [Fact]
    public void Plan_records_nukes_writes_diz_and_diz_is_not_an_orphan()
    {
        using var t = new TempDir();
        var rel = new GhRelease
        {
            TagName = "v1", PublishedAt = "2026-11-05T10:43:09Z",
            Assets =
            [
                new GhAsset { Name = "app-windows-x64.zip", Size = 10, BrowserDownloadUrl = "u" },
                new GhAsset { Name = "app-linux-x64.tar.gz", Size = 20, BrowserDownloadUrl = "u" },
            ],
        };
        var info = new RepoInfo();
        var filter = new AssetFilter(new FilterRule { ExcludePlatforms = ["linux"] });
        var plan = RepoMaintenance.Plan("o/r", [rel], info, t.Path, true, filter, "eggman");

        var nuke = Assert.Single(plan.Nukes);
        Assert.Equal("app-linux-x64.tar.gz", nuke.Name);
        Assert.StartsWith("platform.", nuke.Reason);

        var tagFolder = Path.Combine(AppPaths.RepoRoot(t.Path, "o/r"), "v1");
        var diz = File.ReadAllText(Path.Combine(tagFolder, Diz.FileName));
        Assert.Contains("courier ... eggman @ EGGNET", diz);
        Assert.Contains("files ..... 1 (10 B)", diz);
        Assert.DoesNotContain(RepoMaintenance.FindOrphans("o/r", t.Path, info), f => f.EndsWith(Diz.FileName));

        // no handle -> no DIZ
        using var t2 = new TempDir();
        RepoMaintenance.Plan("o/r", [rel], new RepoInfo(), t2.Path, true, filter);
        Assert.False(File.Exists(Path.Combine(AppPaths.RepoRoot(t2.Path, "o/r"), "v1", Diz.FileName)));
    }

    [Fact]
    public void Sound_effects_are_valid_short_wavs()
    {
        foreach (var wav in new[] { ModemSound.ZmodemBeep(), ModemSound.NoCarrier() })
        {
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
            var seconds = BitConverter.ToInt32(wav, 40) / 2.0 / ModemSound.SampleRate;
            Assert.InRange(seconds, 0.2, 1.0);
        }
    }
}
