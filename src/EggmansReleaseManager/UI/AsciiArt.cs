using System.Text;

namespace EggmansReleaseManager.UI;

/// <summary>Little figlet-style renderer ("ANSI Shadow" letters) so the logo is generated, not hand-aligned.</summary>
public static class AsciiArt
{
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['E'] = ["███████╗", "██╔════╝", "█████╗  ", "██╔══╝  ", "███████╗", "╚══════╝"],
        ['G'] = [" ██████╗ ", "██╔════╝ ", "██║  ███╗", "██║   ██║", "╚██████╔╝", " ╚═════╝ "],
        ['M'] = ["███╗   ███╗", "████╗ ████║", "██╔████╔██║", "██║╚██╔╝██║", "██║ ╚═╝ ██║", "╚═╝     ╚═╝"],
        ['A'] = [" █████╗ ", "██╔══██╗", "███████║", "██╔══██║", "██║  ██║", "╚═╝  ╚═╝"],
        ['N'] = ["███╗   ██╗", "████╗  ██║", "██╔██╗ ██║", "██║╚██╗██║", "██║ ╚████║", "╚═╝  ╚═══╝"],
        ['\''] = ["██╗", "╚█║", " ╚╝", "   ", "   ", "   "],
        ['S'] = ["███████╗", "██╔════╝", "███████╗", "╚════██║", "███████║", "╚══════╝"],
        [' '] = ["  ", "  ", "  ", "  ", "  ", "  "],
    };

    public static string Render(string text)
    {
        var rows = new StringBuilder[6];
        for (var i = 0; i < 6; i++) rows[i] = new StringBuilder();
        foreach (var ch in text.ToUpperInvariant())
        {
            if (!Glyphs.TryGetValue(ch, out var g)) g = Glyphs[' '];
            for (var i = 0; i < 6; i++) rows[i].Append(g[i]);
        }
        return string.Join('\n', rows.Select(r => r.ToString().TrimEnd()));
    }

    public static readonly string Logo = Render("EGGMAN'S");

    public const string Tagline = "·:·  g i T H U B   R E L E A S E   M A N A G E R  ·:·";

    public static readonly string[] ScrollerLines =
    [
        "··· EGGMAN'S GiTHUB RELEASE MANAGER v4 ··· now running on .NET + Avalonia ··· dial 1-800-EGG-MAN1 ···",
        "PRESERVATiON iS NOT A CRiME ··· tag your releases, devs, we're watching (and archiving) ···",
        "greetz fly out to: the RomVault crew ·:· every preservationist with a bigger drive than sense ·:· all devs who ship windows builds ···",
        "F5 = update selected ·:· F6 = update ALL ·:· F3 = filter control ·:· F2 = sysop setup ·:· F1 = nfo ···",
        "remember: star the repos you archive ··· support the coders ··· no lamers allowed on this node ··· ratio is 1:3, keep racing ···",
    ];

    /// <summary>Joins scroller messages with the classic wide gaps; null/empty falls back to the built-in greetz.</summary>
    public static string ScrollerJoin(IEnumerable<string>? custom)
    {
        var lines = (custom ?? []).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) lines = [.. ScrollerLines];
        return string.Join("          ", lines) + "          ";
    }

    public const string SiteRules = """
        ┌──────────────────────────────────────────────────────────────┐
        │                                                              │
        │   ░▒▓█  S i T E   R U L E S  █▓▒░      EGGNET // node 1      │
        │                                        read 'em, lamer.      │
        └──────────────────────────────────────────────────────────────┘

          01 ... NO LAMERS. archive the release, don't just hoard links.
          02 ... RATiO iS 1:3. race it in, earn your credits.
          03 ... NUKES ARE FiNAL. if your filters say linux, it's gone.
          04 ... NO DUPES. skip-existing is your friend.
          05 ... STAR THE REPOS YOU ARCHiVE. support the coders.
          06 ... RESPECT THE API LiMiT. 60/hr anon - get a token.
          07 ... VERiFY YOUR HASHES. a bad crc is a bad day.
          08 ... iDLE TOO LONG AND AUTO-UPDATE DOES iT FOR YOU.
          09 ... WHAT HAPPENS ON EGGNET STAYS ON EGGNET.
          10 ... THE SYSOP iS ALWAYS RiGHT. (the sysop is you.)

          ─────────────────────────────────────────────────────────
           break the rules and EggBot nukes you. press [I AGREE].
        """;

    public const string Nfo = """
        ▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄
        █                                                  █
        █        E G G M A N ' S   G i T H U B             █
        █        R E L E A S E   M A N A G E R             █
        █                                                  █
        ▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀

          ─────────────[ RELEASE iNFO ]─────────────
           SUPPLiER ...... Eggman
           CODE .......... Claude
           TYPE .......... Utility / Archival
           PLATFORM ...... Windows / .NET + Avalonia
           VERSiON ....... 4.x
           PROTECTiON .... None. It's MIT licensed.
           DiSKS ......... 1 (portable, lives next to its config)

          ───────────[ WHAT iT DOES ]───────────────
           Multi-repo incremental GitHub release mirror:
           resume + retry downloads, SHA-1 tracking,
           platform filters, auto-update timer, Discord
           alerts, RomVault DATs, PDF/CSV/JSON exports,
           orphan cleanup and local re-indexing.

          ─────────────[ GREETZ ]───────────────────
           The preservation community · RomVault ·
           everybody who still reads .nfo files.

          Created for the preservation community by
          Eggman, with Claude's help turning ideas
          into code.
        """;
}
