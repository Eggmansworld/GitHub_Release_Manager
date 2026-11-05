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
        Assert.Equal("1800344227", ModemSound.ToDigits("1-800-EGG-BBS"));

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
        Assert.InRange(seconds, 8, 16);

        Assert.Equal("ATZ", cues[0].Text);
        Assert.StartsWith("CONNECT", cues[^1].Text);
        Assert.True(cues.Zip(cues.Skip(1)).All(p => p.First.At <= p.Second.At));
        Assert.True(cues[^1].At.TotalSeconds <= seconds);
    }
}
