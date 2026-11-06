using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EggmansReleaseManager.Services;

public static class AppPaths
{
    /// <summary>Folder holding the exe and its portable config/db files.</summary>
    public static string BaseDir { get; set; } = AppContext.BaseDirectory;

    public static string ConfigPath => Path.Combine(BaseDir, "Eggmans_GitHub_Release_Manager_config.json");
    public static string RepoDbPath => Path.Combine(BaseDir, "Eggmans_GitHub_Release_Manager_repos.json");
    public static string TrackedListPath => Path.Combine(BaseDir, "tracked_repos.txt");
    public static string LogsDir => Path.Combine(BaseDir, "_logs");

    /// <summary>"owner/repo" -> "owner_repo"</summary>
    public static string RepoFolderName(string repoKey) => repoKey.Replace('/', '_').Replace('\\', '_');

    public static string RepoRoot(string rootFolder, string repoKey) =>
        Path.Combine(rootFolder, RepoFolderName(repoKey));

    /// <summary>Make a release tag safe as a Windows folder name.</summary>
    public static string SafeTagFolder(string tag)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = tag.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(s) ? "untagged" : s;
    }

    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path); } catch { return path; }
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        using var fs = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(fs, Options);
    }

    /// <summary>Write via temp file + replace so a crash never leaves a half-written DB.</summary>
    public static void SaveAtomic<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        {
            JsonSerializer.Serialize(fs, value, Options);
        }
        File.Move(tmp, path, overwrite: true);
    }
}

public static class Stamp
{
    public const string Format = "yyyy-MM-dd_HH_mm_ss'Z'";

    /// <summary>UTC timestamp in the v3 format: 2026-11-05_10_43_09Z</summary>
    public static string Now() => DateTime.UtcNow.ToString(Format, CultureInfo.InvariantCulture);

    public static DateTime? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParseExact(s, Format, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
            return d;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
            return d;
        return null;
    }

    /// <summary>"2026-11-05_10_43_09Z" -> "2026-11-05 10:43"</summary>
    public static string Pretty(string? s)
    {
        var d = Parse(s);
        return d is null ? (s ?? "") : d.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}

public static class Fmt
{
    public static string Bytes(long b)
    {
        if (b >= 1L << 40) return $"{b / (double)(1L << 40):0.0} TB";
        if (b >= 1L << 30) return $"{b / (double)(1L << 30):0.0} GB";
        if (b >= 1L << 20) return $"{b / (double)(1L << 20):0.0} MB";
        if (b >= 1L << 10) return $"{b / 1024.0:0.0} KB";
        return b > 0 ? $"{b} B" : "";
    }

    public static string Speed(double bytesPerSec) =>
        bytesPerSec >= 1 << 20 ? $"{bytesPerSec / (1 << 20):0.0} MB/s" : $"{bytesPerSec / 1024:0} KB/s";

    public static string Eta(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "--:--";
        var t = TimeSpan.FromSeconds(Math.Min(seconds, 359999));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    /// <summary>ASCII/ANSI progress bar: ██████░░░░</summary>
    public static string BytesOrZero(long b) => Bytes(b) is { Length: > 0 } s ? s : "0 B";

    public static string Bar(double fraction, int cells)
    {
        fraction = Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1);
        var full = (int)Math.Round(fraction * cells);
        return new string('█', full) + new string('░', cells - full);
    }
}
