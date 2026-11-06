using System.Globalization;
using System.Text;
using EggmansReleaseManager.Models;

namespace EggmansReleaseManager.Services;

/// <summary>BBS handle rules. WWIV stores user names in a 31-byte field, so 30 characters is the limit.</summary>
public static class Handle
{
    public const int MaxLength = 30;

    public static string Clean(string? name)
    {
        var s = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length > MaxLength ? s[..MaxLength].TrimEnd() : s;
    }

    /// <summary>The configured handle, or the Windows user name when none is set.</summary>
    public static string For(AppConfig cfg) =>
        Clean(cfg.Username) is { Length: > 0 } h ? h : Clean(Environment.UserName.ToLowerInvariant());
}

/// <summary>An asset skipped by the filters, shown as a nuke.</summary>
public sealed record Nuke(string Tag, string Name, long Size, string Reason)
{
    /// <summary>"platform LiNUX" -> "platform.linux", "pattern '*-pgo*'" -> "pattern.*-pgo*"</summary>
    public static string ReasonTag(string reason) =>
        reason.Replace("'", "").Trim().ToLowerInvariant().Replace(' ', '.');

    /// <summary>"platform.linux x12, kind.checksums x3"</summary>
    public static string Summary(IEnumerable<Nuke> nukes, int max = 4)
    {
        var groups = nukes.GroupBy(n => n.Reason).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).ToList();
        var parts = groups.Take(max).Select(g => $"{g.Key} x{g.Count()}").ToList();
        if (groups.Count > max) parts.Add($"+{groups.Count - max} more");
        return string.Join(", ", parts);
    }
}

/// <summary>FILE_ID.DIZ: the classic 45-column, max-10-line description file, plain ASCII.</summary>
public static class Diz
{
    public const string FileName = "FILE_ID.DIZ";
    public const int Width = 45;

    public static string Build(string repoKey, string tag, string? publishedAt, int files, long bytes, string handle)
    {
        var date = Stamp.Parse(publishedAt) is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "????-??-??";
        string Row(string s)
        {
            s = Ascii(s);
            if (s.Length > Width - 4) s = s[..(Width - 5)] + "~";
            return "| " + s.PadRight(Width - 4) + " |";
        }
        var bar = new string('-', Width - 2);
        var lines = new[]
        {
            "." + bar + ".",
            Row(repoKey),
            Row(tag),
            "|" + bar + "|",
            Row($"date ...... {date}"),
            Row($"files ..... {files} ({Fmt.BytesOrZero(bytes)})"),
            Row("source .... GitHub release"),
            Row($"courier ... {handle} @ EGGNET"),
            "`" + bar + "'",
        };
        return string.Join("\r\n", lines) + "\r\n";
    }

    private static string Ascii(string s) => new(s.Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());

    /// <summary>Writes the DIZ only when its content changed. Returns true if written.</summary>
    public static bool Write(string tagFolder, string content)
    {
        var path = Path.Combine(tagFolder, FileName);
        if (File.Exists(path) && File.ReadAllText(path, Encoding.ASCII) == content) return false;
        File.WriteAllText(path, content, Encoding.ASCII);
        return true;
    }
}

/// <summary>pzs-ng / glftpd zipscript style status lines.</summary>
public static class Zipscript
{
    public const string Group = "EGG";

    public static string Racing(int done, int total, double pct) =>
        $"[{Group}] - {pct,3:0}% Complete - {done} of {total} files - [{Group}]";

    public static string Complete(long bytes, int files) =>
        $"[{Group}] - ( {Compact(bytes)} {files}F - COMPLETE ) - [{Group}]";

    public static string Incomplete(int done, int total, string what = "INCOMPLETE") =>
        $"[{Group}] - ( {done} of {total}F - {what} ) - [{Group}]";

    /// <summary>"107.4MB" - the space-less size zipscripts use.</summary>
    public static string Compact(long bytes) => Fmt.BytesOrZero(bytes).Replace(" ", "");
}

/// <summary>"site wkup / monthup / allup" style top-uploader boards, ranked by repo.</summary>
public static class TopBoard
{
    public sealed record Entry(string Repo, long Bytes, int Files);

    /// <summary>Monday 00:00 of the current week.</summary>
    public static DateTime WeekStart(DateTime nowLocal) =>
        nowLocal.Date.AddDays(-(((int)nowLocal.DayOfWeek + 6) % 7));

    public static DateTime MonthStart(DateTime nowLocal) => new(nowLocal.Year, nowLocal.Month, 1, 0, 0, 0, nowLocal.Kind);

    /// <summary>Bytes/files downloaded at or after <paramref name="sinceLocal"/> (null = everything archived).</summary>
    public static List<Entry> Compute(RepoDb db, DateTime? sinceLocal, int top = 10)
    {
        var sinceUtc = sinceLocal?.ToUniversalTime();
        var list = new List<Entry>();
        foreach (var (repo, info) in db.Repos)
        {
            long bytes = 0;
            var files = 0;
            foreach (var m in info.Assets.Values.SelectMany(t => t.Values))
            {
                if (sinceUtc is not null && (Stamp.Parse(m.DownloadedAt) is not { } at || at < sinceUtc)) continue;
                bytes += m.SizeBytes;
                files++;
            }
            if (files > 0) list.Add(new Entry(repo, bytes, files));
        }
        return list.OrderByDescending(e => e.Bytes).ThenBy(e => e.Repo, StringComparer.OrdinalIgnoreCase).Take(top).ToList();
    }
}

/// <summary>Elite speak. Idempotent: running it twice gives the same text.</summary>
public static class Leet
{
    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["elite"] = "31337", ["the"] = "teh", ["you"] = "j00", ["your"] = "j00r", ["cool"] = "kewl",
        ["hacker"] = "h4x0r", ["files"] = "filez", ["warez"] = "w4r3z", ["owned"] = "pwn3d", ["please"] = "plz",
    };

    public static string Apply(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            if (char.IsLetter(s[i]))
            {
                var end = i;
                while (end < s.Length && char.IsLetter(s[end])) end++;
                var word = s[i..end];
                sb.Append(Words.TryGetValue(word, out var rep) ? rep : word);
                i = end;
            }
            else sb.Append(s[i++]);
        }
        var chars = sb.ToString().ToCharArray();
        for (var k = 0; k < chars.Length; k++)
            chars[k] = chars[k] switch
            {
                'a' or 'A' => '4', 'e' or 'E' => '3', 'i' or 'I' => '1', 'o' or 'O' => '0',
                't' or 'T' => '7', 's' or 'S' => '5', _ => chars[k],
            };
        return new string(chars);
    }
}
