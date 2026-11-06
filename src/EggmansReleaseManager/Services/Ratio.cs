namespace EggmansReleaseManager.Services;

/// <summary>A user class on the "site", reached once your archive is at least <see cref="MinBytes"/>.</summary>
public sealed record UserClass(string Name, long MinBytes);

/// <summary>
/// Topsite-style 1:3 ratio bookkeeping. Your archive is the site: every byte you race in from GitHub
/// counts as an upload and earns 3 bytes of credit; every byte pulled down (re-downloads included) costs one.
/// </summary>
public static class Ratio
{
    public const int Multiplier = 3;
    public const string Display = "1:3";

    private const long GB = 1L << 30;

    public static readonly IReadOnlyList<UserClass> Classes =
    [
        new("NEWBiE", 0),
        new("USER", 1 * GB),
        new("ViP", 10 * GB),
        new("ELiTE", 50 * GB),
        new("SiTEOP", 250 * GB),
        new("LEGEND", 1024 * GB),
    ];

    public static long Credits(long upBytes, long downBytes) => upBytes * Multiplier - downBytes;

    /// <summary>Current class, the next one (null at the top) and progress towards it (0..1).</summary>
    public static (UserClass Current, UserClass? Next, double Progress) Rank(long upBytes)
    {
        var i = 0;
        while (i + 1 < Classes.Count && upBytes >= Classes[i + 1].MinBytes) i++;
        if (i + 1 >= Classes.Count) return (Classes[i], null, 1);
        var cur = Classes[i];
        var next = Classes[i + 1];
        return (cur, next, Math.Clamp((upBytes - cur.MinBytes) / (double)(next.MinBytes - cur.MinBytes), 0, 1));
    }
}
