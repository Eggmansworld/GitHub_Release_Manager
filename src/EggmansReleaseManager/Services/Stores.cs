using EggmansReleaseManager.Models;

namespace EggmansReleaseManager.Services;

/// <summary>Loads/saves the portable config, repo DB and tracked_repos.txt next to the exe.</summary>
public static class Stores
{
    public static AppConfig LoadConfig()
    {
        try
        {
            var cfg = Json.Load<AppConfig>(AppPaths.ConfigPath) ?? new AppConfig();
            cfg.Filters ??= new FilterConfig();
            cfg.Filters.Global ??= new FilterRule();
            cfg.Filters.Repos ??= new();
            cfg.RepoColumnWidths ??= new();
            if (cfg.MaxThreads < 1) cfg.MaxThreads = 1;
            if (cfg.AutoUpdateInterval < 5) cfg.AutoUpdateInterval = 5;
            return cfg;
        }
        catch
        {
            return new AppConfig();
        }
    }

    public static void SaveConfig(AppConfig cfg) => Json.SaveAtomic(AppPaths.ConfigPath, cfg);

    public static RepoDb LoadRepoDb()
    {
        try
        {
            var db = Json.Load<RepoDb>(AppPaths.RepoDbPath) ?? new RepoDb();
            db.Repos ??= new();
            foreach (var info in db.Repos.Values)
                info.Assets ??= new();
            return db;
        }
        catch
        {
            // Keep a copy of an unreadable DB rather than silently overwriting it later.
            try
            {
                if (File.Exists(AppPaths.RepoDbPath))
                    File.Copy(AppPaths.RepoDbPath, AppPaths.RepoDbPath + $".corrupt_{Stamp.Now()}", true);
            }
            catch { /* ignore */ }
            return new RepoDb();
        }
    }

    public static void SaveRepoDb(RepoDb db) => Json.SaveAtomic(AppPaths.RepoDbPath, db);

    /// <summary>Merge any repos listed in tracked_repos.txt into the DB. Returns how many were added.</summary>
    public static int MergeTrackedList(RepoDb db)
    {
        if (!File.Exists(AppPaths.TrackedListPath)) return 0;
        var added = 0;
        try
        {
            foreach (var raw in File.ReadAllLines(AppPaths.TrackedListPath))
            {
                var repo = raw.Trim();
                if (repo.Length == 0 || !repo.Contains('/') || db.Repos.ContainsKey(repo)) continue;
                db.Repos[repo] = new RepoInfo();
                added++;
            }
        }
        catch { /* ignore */ }
        return added;
    }

    public static void WriteTrackedList(RepoDb db)
    {
        var lines = db.Repos.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => k + "\n");
        File.WriteAllText(AppPaths.TrackedListPath, string.Concat(lines));
    }
}
