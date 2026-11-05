using EggmansReleaseManager.Models;
using EggmansReleaseManager.Services;
using Xunit;

namespace EggmansReleaseManager.Tests;

public class PlatformDetectorTests
{
    [Theory]
    // real asset names from the tracked repos
    [InlineData("lichii-linux-x86_64.AppImage", "linux")]
    [InlineData("lichii-linux-aarch64-pgo.tar.gz", "linux,arm")]
    [InlineData("lichii-macos-arm64.dmg", "macos,arm")]
    [InlineData("lichii-windows-x86_64-pgo.zip", "windows")]
    [InlineData("citron_nightly-3866aa6b0-linux-x86_64.AppImage.zsync", "linux")]
    [InlineData("citron_nightly-3866aa6b0-linux-aarch64.tar.zst", "linux,arm")]
    [InlineData("asphyxia-core-win-x86.zip", "windows,x86")]
    [InlineData("asphyxia-core-win-x64.zip", "windows")]
    [InlineData("asphyxia-core-arm64.zip", "arm")]
    [InlineData("asphyxia-core-armv7.zip", "arm")]
    [InlineData("NSP-Box-v4.0.0-Windows-x64.zip", "windows")]
    [InlineData("NSP-Box-v4.0.0-source.zip", "source")]
    [InlineData("SHA256SUMS.txt", "checksums")]
    [InlineData("eglantine-windows-x86_64-pgo-c08864b3fed3b6c70418af0e1305395459e266c8.zip", "windows")]
    // generic cases
    [InlineData("DiskImageTool_1.18.0.1.zip", "")]
    [InlineData("spice2x-23-03-12-0726.zip", "")]
    [InlineData("kfc-fork-7.0.1-minorfix.zip", "")]
    [InlineData("tool_x86_64-unknown-linux-gnu.tar.gz", "linux")]
    [InlineData("tool-x86_64-pc-windows-msvc.zip", "windows")]
    [InlineData("tool-aarch64-apple-darwin.tar.gz", "macos,arm")]
    [InlineData("MyApp-Setup-1.2.exe", "windows")]
    [InlineData("myapp_1.2_amd64.deb", "linux")]
    [InlineData("myapp-1.2.x86_64.rpm", "linux")]
    [InlineData("MyApp.apk", "android")]
    [InlineData("MyApp.ipa", "ios")]
    [InlineData("myapp-freebsd-amd64.tar.gz", "bsd")]
    [InlineData("myapp-1.0-1-x86_64.pkg.tar.zst", "linux")]
    [InlineData("MyApp.pdb", "debug")]
    [InlineData("myapp.tar.gz.sig", "checksums")]
    [InlineData("MyAppLinux64.zip", "linux")]
    [InlineData("apple2-emu-win64.zip", "windows")]
    [InlineData("twinkle-1.0.zip", "")]
    [InlineData("machine-learning-kit.zip", "")]
    public void Detects_expected_tags(string file, string expected)
    {
        var tags = PlatformDetector.Detect(file);
        Assert.Equal(expected, string.Join(",", tags));
    }
}

public class AssetFilterTests
{
    private static AssetFilter Exclude(params string[] platforms) =>
        new(new FilterRule { ExcludePlatforms = platforms.ToList() });

    [Fact]
    public void Empty_filter_keeps_everything()
    {
        Assert.True(AssetFilter.None.Keep("lichii-linux-x86_64.AppImage"));
        Assert.True(AssetFilter.None.IsEmpty);
    }

    [Fact]
    public void Excluding_linux_and_macos_keeps_windows_and_universal()
    {
        var f = Exclude("linux", "macos");
        Assert.False(f.Keep("lichii-linux-x86_64.AppImage"));
        Assert.False(f.Keep("lichii-macos-arm64.dmg"));
        Assert.False(f.Keep("citron_nightly-3866aa6b0-linux-x86_64.AppImage.zsync"));
        Assert.True(f.Keep("lichii-windows-x86_64.zip"));
        Assert.True(f.Keep("DiskImageTool_1.18.0.1.zip"));
        Assert.True(f.Keep("SHA256SUMS.txt"));
    }

    [Fact]
    public void Os_skip_needs_all_os_tags_excluded()
    {
        var f = Exclude("linux");
        Assert.True(f.Keep("app-win-linux.zip"));
        Assert.False(Exclude("linux", "windows").Keep("app-win-linux.zip"));
    }

    [Fact]
    public void Arch_tags_are_independent_of_os()
    {
        var f = Exclude("arm");
        Assert.False(f.Keep("lichii-linux-aarch64.AppImage"));
        Assert.True(f.Keep("lichii-linux-x86_64.AppImage"));
        Assert.False(f.Keep("asphyxia-core-arm64.zip"));
    }

    [Fact]
    public void Kind_tags_skip_on_any_match()
    {
        var f = Exclude("source", "checksums");
        Assert.False(f.Keep("NSP-Box-v4.0.0-source.zip"));
        Assert.False(f.Keep("SHA256SUMS.txt"));
        Assert.True(f.Keep("NSP-Box-v4.0.0-Windows-x64.zip"));
    }

    [Fact]
    public void Patterns_glob_regex_and_include_override()
    {
        var f = new AssetFilter(new FilterRule
        {
            ExcludePlatforms = ["linux"],
            ExcludePatterns = ["*-pgo*", "re:\\.zsync$"],
            IncludePatterns = ["*linux-x86_64.AppImage"],
        });
        Assert.False(f.Keep("lichii-windows-x86_64-pgo.zip"));                 // glob
        Assert.False(f.Keep("something.zsync"));                               // regex
        Assert.True(f.Keep("lichii-linux-x86_64.AppImage"));                   // include beats platform skip
        Assert.False(f.Keep("lichii-linux-aarch64.AppImage"));
        Assert.True(f.Keep("lichii-windows-x86_64.zip"));
    }

    [Fact]
    public void Glob_without_wildcard_means_contains_and_is_case_insensitive()
    {
        var f = new AssetFilter(new FilterRule { ExcludePatterns = ["-MACOS-"] });
        Assert.False(f.Keep("app-macos-arm64.zip"));
        Assert.True(f.Keep("app-windows.zip"));
    }

    [Fact]
    public void Invalid_regex_is_ignored_not_fatal()
    {
        var f = new AssetFilter(new FilterRule { ExcludePatterns = ["re:([unclosed"] });
        Assert.True(f.Keep("anything.zip"));
        Assert.Null(AssetFilter.TryCompile("re:([unclosed"));
    }

    [Fact]
    public void Repo_rule_merges_with_global_or_replaces_it()
    {
        var cfg = new FilterConfig
        {
            Global = new FilterRule { ExcludePlatforms = ["linux", "macos"] },
            Repos =
            {
                ["zortos293/Lichii"] = new RepoFilterRule { UseGlobal = true, ExcludePatterns = ["*-pgo*"] },
                ["citron-neo/CI"] = new RepoFilterRule { UseGlobal = false, ExcludePlatforms = ["arm"] },
            },
        };

        var lichii = AssetFilter.For(cfg, "zortos293/Lichii");
        Assert.False(lichii.Keep("lichii-linux-x86_64.AppImage"));
        Assert.False(lichii.Keep("lichii-windows-x86_64-pgo.zip"));
        Assert.True(lichii.Keep("lichii-windows-x86_64.zip"));

        var citron = AssetFilter.For(cfg, "CITRON-NEO/ci"); // case-insensitive key
        Assert.True(citron.Keep("citron_nightly-3866aa6b0-linux-x86_64.AppImage"));
        Assert.False(citron.Keep("citron_nightly-3866aa6b0-linux-aarch64.AppImage"));

        var other = AssetFilter.For(cfg, "someone/else");
        Assert.False(other.Keep("x-linux.tar.gz"));
    }
}
