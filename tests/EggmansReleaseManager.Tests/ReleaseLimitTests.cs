using EggmansReleaseManager.Models;
using EggmansReleaseManager.Services;
using EggmansReleaseManager.ViewModels;
using Xunit;

namespace EggmansReleaseManager.Tests;

public class ReleaseLimitTests
{
    private static GhRelease R(string tag, string? published, bool draft = false, string? created = null) =>
        new() { TagName = tag, PublishedAt = published, CreatedAt = created, Draft = draft };

    [Fact]
    public void Keeps_the_newest_n_by_publish_date_and_drops_drafts()
    {
        var rels = new[]
        {
            R("v1", "2024-01-01T00:00:00Z"),
            R("v3", "2026-03-01T00:00:00Z"),
            R("draft", null, draft: true, created: "2026-12-01T00:00:00Z"),
            R("v2", "2025-06-01T00:00:00Z"),
            R("v4-unpublished-date", null, created: "2026-05-01T00:00:00Z"),
        };
        Assert.Equal(["v4-unpublished-date", "v3"], AssetFilter.ApplyReleaseLimit(rels, 2).Select(r => r.TagName));
        Assert.Equal(["v4-unpublished-date", "v3", "v2", "v1"], AssetFilter.ApplyReleaseLimit(rels, null).Select(r => r.TagName));
        Assert.Equal(4, AssetFilter.ApplyReleaseLimit(rels, 0).Count);
    }

    [Fact]
    public void Fetches_only_the_pages_it_needs()
    {
        Assert.Equal(1, AssetFilter.PagesFor(50));
        Assert.Equal(2, AssetFilter.PagesFor(95));
        Assert.Equal(6, AssetFilter.PagesFor(500));
        Assert.Equal(int.MaxValue, AssetFilter.PagesFor(null));
    }

    [Fact]
    public void Repo_limit_follows_global_unless_set()
    {
        var cfg = new FilterConfig { Global = new FilterRule { ReleaseLimit = 50 } };
        cfg.Repos["o/follow"] = new RepoFilterRule { ExcludePatterns = ["*.zsync"] };
        cfg.Repos["o/all"] = new RepoFilterRule { ReleaseLimit = 0 };
        cfg.Repos["o/ten"] = new RepoFilterRule { ReleaseLimit = 10 };
        cfg.Repos["o/own"] = new RepoFilterRule { UseGlobal = false };

        Assert.Equal(50, AssetFilter.For(cfg, "o/follow").ReleaseLimit);
        Assert.Null(AssetFilter.For(cfg, "o/all").ReleaseLimit);
        Assert.Equal(10, AssetFilter.For(cfg, "o/ten").ReleaseLimit);
        Assert.Null(AssetFilter.For(cfg, "o/own").ReleaseLimit);
        Assert.Equal(50, AssetFilter.For(cfg, "o/untouched").ReleaseLimit);
        Assert.Null(AssetFilter.For(new FilterConfig(), "o/x").ReleaseLimit);   // default: all
        Assert.False(new RepoFilterRule { ReleaseLimit = 0 }.IsEmpty);
    }

    [Fact]
    public void Editor_defaults_to_all_and_custom_50_and_round_trips()
    {
        var cfg = new FilterConfig();
        var vm = new FilterEditorViewModel(cfg, ["o/r"], null, null);
        Assert.True(vm.LimitAll);
        Assert.Equal(50, vm.LimitCount);

        vm.LimitCustom = true;                          // global: newest 50
        var saved = vm.Result();
        Assert.Equal(50, saved.Global.ReleaseLimit);

        var vm2 = new FilterEditorViewModel(saved, ["o/r"], "o/r", null);
        Assert.True(vm2.LimitFollow);                   // repo without a rule follows GLOBAL
        vm2.HasCustomRule = true;
        vm2.LimitAll = true;                            // this repo: everything
        var saved2 = vm2.Result();
        Assert.Equal(0, saved2.Repos["o/r"].ReleaseLimit);
        Assert.Null(AssetFilter.For(saved2, "o/r").ReleaseLimit);

        var vm3 = new FilterEditorViewModel(saved2, ["o/r"], "o/r", null);
        vm3.LimitCustom = true;
        vm3.LimitCount = 120;
        Assert.Equal(120, vm3.Result().Repos["o/r"].ReleaseLimit);
    }
}
