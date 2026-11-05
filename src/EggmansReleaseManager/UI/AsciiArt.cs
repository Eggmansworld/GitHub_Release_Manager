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
        "··· EGGMAN'S GiTHUB RELEASE MANAGER v4.0 ··· now running on .NET + Avalonia ···",
        "PRESERVATiON iS NOT A CRiME ··· tag your releases, devs, we're watching (and archiving) ···",
        "greetz fly out to: the RomVault crew ·:· every preservationist with a bigger drive than sense ·:· all devs who ship windows builds ···",
        "F5 = update selected ·:· F6 = update ALL ·:· F3 = filter control ·:· F2 = sysop setup ·:· F1 = nfo ···",
        "remember: star the repos you archive ··· support the coders ··· no lamers allowed on this node ···",
    ];

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
           VERSiON ....... 4.0
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
