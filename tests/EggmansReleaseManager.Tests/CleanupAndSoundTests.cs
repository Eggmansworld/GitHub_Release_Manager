using EggmansReleaseManager.Models;
using EggmansReleaseManager.Services;
using Xunit;

namespace EggmansReleaseManager.Tests;

public class FilterCleanupTests
{
    private static string Touch(string path, int bytes = 4)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void Finds_and_moves_files_matching_skip_rules_and_updates_db()
    {
        using var t = new TempDir();
        var root = t.Path;
        var repoRoot = Path.Combine(root, "zortos293_Lichii");
        var linux = Touch(Path.Combine(repoRoot, "v1", "lichii-linux-x86_64.AppImage"));
        var win = Touch(Path.Combine(repoRoot, "v1", "lichii-windows-x86_64.zip"));
        Touch(Path.Combine(repoRoot, "v1", "release_notes.txt"));
        var linuxOnly = Touch(Path.Combine(repoRoot, "v0", "lichii-linux-aarch64.tar.gz"));
        Touch(Path.Combine(repoRoot, "v0", "release_notes.txt"));
        Touch(Path.Combine(repoRoot, "orphans", "v9", "old-linux.zip"));    // reserved folder: ignored
        Touch(Path.Combine(root, "other_repo", "v1", "x-linux.zip"));        // repo not passed in: ignored

        var db = new RepoDb();
        var info = db.Ensure("zortos293/Lichii");
        info.Assets["v1"] = new()
        {
            ["lichii-linux-x86_64.AppImage"] = new AssetMeta { SizeBytes = 4, Path = linux.Replace('\\', '/') },
            ["lichii-windows-x86_64.zip"] = new AssetMeta { SizeBytes = 4, Path = win },
        };
        info.Assets["v0"] = new() { ["lichii-linux-aarch64.tar.gz"] = new AssetMeta { SizeBytes = 4, Path = linuxOnly } };

        var filters = new FilterConfig { Global = new FilterRule { ExcludePlatforms = ["linux"] } };
        var found = RepoMaintenance.FindFilteredLocal(["zortos293/Lichii"], filters, root);
        Assert.Equal(2, found.Count);
        Assert.All(found, f => Assert.Contains("linux", Path.GetFileName(f.Path)));

        var (done, failed, folders) = RepoMaintenance.CleanupFiltered(found, db, root, RepoMaintenance.CleanupMode.MoveToFiltered);
        Assert.Equal(2, done);
        Assert.Equal(0, failed);
        Assert.Equal(1, folders);                                   // v0 only had release notes left
        Assert.False(Directory.Exists(Path.Combine(repoRoot, "v0")));
        Assert.True(File.Exists(win));
        Assert.True(File.Exists(Path.Combine(repoRoot, RepoMaintenance.FilteredFolder, "v1", "lichii-linux-x86_64.AppImage")));
        Assert.True(File.Exists(Path.Combine(repoRoot, RepoMaintenance.FilteredFolder, "v0", "lichii-linux-aarch64.tar.gz")));

        Assert.Single(info.Assets);                                  // v0 tag dropped from DB
        Assert.Equal(["lichii-windows-x86_64.zip"], info.Assets["v1"].Keys);

        // moved files are not orphans, and a second pass finds nothing
        Assert.Empty(RepoMaintenance.FindOrphans("zortos293/Lichii", root, info).Where(p => p.Contains("_filtered")));
        Assert.Empty(RepoMaintenance.FindFilteredLocal(["zortos293/Lichii"], filters, root));
    }

    [Fact]
    public void Empty_filter_finds_nothing()
    {
        using var t = new TempDir();
        Touch(Path.Combine(t.Path, "o_r", "v1", "app-linux.zip"));
        Assert.Empty(RepoMaintenance.FindFilteredLocal(["o/r"], new FilterConfig(), t.Path));
    }
}

public class ModemSoundTests
{
    [Fact]
    public void Keypad_letters_map_to_digits() =>
        Assert.Equal("18003446261", ModemSound.ToDigits("1-800-EGG-MAN1"));

    [Fact]
    public void Number_is_a_full_nanp_toll_free_number()
    {
        var d = ModemSound.ToDigits(ModemSound.Number);
        Assert.Equal(11, d.Length);                 // 1 + 10-digit NANP
        Assert.StartsWith("1800", d);
        Assert.Equal("1-800-344-6261", ModemSound.Pretty(ModemSound.Number));
    }

    [Fact]
    public void Builds_a_valid_wav_with_ordered_cues()
    {
        var (wav, cues) = ModemSound.Build();
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(ModemSound.SampleRate, BitConverter.ToInt32(wav, 24));
        var dataBytes = BitConverter.ToInt32(wav, 40);
        Assert.Equal(wav.Length - 44, dataBytes);
        var seconds = dataBytes / 2.0 / ModemSound.SampleRate;
        Assert.InRange(seconds, 20, 26);

        Assert.Equal("ATZ", cues[0].Text);
        Assert.StartsWith("CONNECT", cues[^1].Text);
        Assert.True(cues.Zip(cues.Skip(1)).All(p => p.First.At <= p.Second.At));
        Assert.True(cues[^1].At.TotalSeconds <= seconds);
    }

    [Fact]
    public void Recording_actually_dials_the_number()
    {
        var (wav, _) = ModemSound.Build();
        var sr = ModemSound.SampleRate;
        double Sample(int i) => BitConverter.ToInt16(wav, 44 + i * 2) / 32768.0;
        double Power(int start, int n, double f)
        {
            double re = 0, im = 0;
            for (var i = 0; i < n; i++)
            {
                var a = 2 * Math.PI * f * i / sr;
                re += Sample(start + i) * Math.Cos(a);
                im += Sample(start + i) * Math.Sin(a);
            }
            return re * re + im * im;
        }
        int[] lows = [697, 770, 852, 941], highs = [1209, 1336, 1477];
        var keys = new[,] { { '1', '2', '3' }, { '4', '5', '6' }, { '7', '8', '9' }, { '*', '0', '#' } };
        var dialed = "";
        for (var k = 0; k < 11; k++)
        {
            var start = (int)((0.82 + 0.2 * k) * sr);
            var n = (int)(0.06 * sr);
            var lo = Enumerable.Range(0, 4).MaxBy(j => Power(start, n, lows[j]));
            var hi = Enumerable.Range(0, 3).MaxBy(j => Power(start, n, highs[j]));
            dialed += keys[lo, hi];
        }
        Assert.Equal(ModemSound.ToDigits(ModemSound.Number), dialed);
    }
}

public class RatioAndScrollerTests
{
    private const long GB = 1L << 30;

    [Fact]
    public void Credits_are_three_per_byte_up_minus_down() =>
        Assert.Equal(3 * GB - GB / 2, Ratio.Credits(GB, GB / 2));

    [Theory]
    [InlineData(0L, "NEWBiE", "USER")]
    [InlineData(5L, "USER", "ViP")]
    [InlineData(59L, "ELiTE", "SiTEOP")]
    [InlineData(5000L, "LEGEND", null)]
    public void Class_follows_archive_size(long gb, string cls, string? next)
    {
        var (cur, nxt, progress) = Ratio.Rank(gb * GB);
        Assert.Equal(cls, cur.Name);
        Assert.Equal(next, nxt?.Name);
        Assert.InRange(progress, 0, 1);
    }

    [Fact]
    public void Scroller_uses_custom_lines_and_falls_back_to_default()
    {
        Assert.StartsWith("greetz to the crew", EggmansReleaseManager.UI.AsciiArt.ScrollerJoin(["  greetz to the crew ", "", "hi"]));
        Assert.Equal(EggmansReleaseManager.UI.AsciiArt.ScrollerJoin(null), EggmansReleaseManager.UI.AsciiArt.ScrollerJoin(["", "  "]));
        Assert.Equal(["a", "b"], EggmansReleaseManager.ViewModels.SettingsViewModel.SplitLines("a\r\n\r\n  b  \n"));
    }

    [Fact]
    public void Settings_store_null_when_greetz_are_unchanged()
    {
        var cfg = new AppConfig();
        var vm = new EggmansReleaseManager.ViewModels.SettingsViewModel(cfg);
        vm.ApplyTo(cfg);
        Assert.Null(cfg.ScrollerText);
        vm.ScrollerText = "shoutz to #warez\nlamers go home";
        vm.ApplyTo(cfg);
        Assert.Equal(["shoutz to #warez", "lamers go home"], cfg.ScrollerText);
    }
}
