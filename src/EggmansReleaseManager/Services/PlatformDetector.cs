using System.Text.RegularExpressions;

namespace EggmansReleaseManager.Services;

/// <summary>
/// Guesses which platform(s)/kind(s) a release asset is for, from its filename only
/// (GitHub has no platform field on release assets).
///
/// The filename is lower-cased, "x86_64"/"x86-64"/"amd64" are folded to "x64", then it is split into
/// tokens on anything that isn't a letter or digit:  "lichii-linux-x86_64-pgo.AppImage"
///   -> [lichii, linux, x64, pgo, appimage]
/// A tag matches when a token is in its token list, the name ends with one of its extensions,
/// or (for a few long, unambiguous words) the word appears anywhere, e.g. "MyAppLinux64.zip".
/// An asset can carry several tags (linux + arm), and an asset with no tag is treated as universal.
/// </summary>
public static partial class PlatformDetector
{
    public sealed record PlatformTag(
        string Id,
        string Label,
        string Description,
        string[] Tokens,
        string[] Extensions,
        string[] Substrings,
        TagKind Kind = TagKind.Os);

    public enum TagKind
    {
        /// <summary>Operating system. Skipped only when ALL of an asset's OS tags are excluded.</summary>
        Os,
        /// <summary>CPU architecture. Skipped only when ALL of an asset's arch tags are excluded.</summary>
        Arch,
        /// <summary>Asset kind (source, checksums, debug). Skipped when ANY excluded kind matches.</summary>
        Kind,
    }

    public static readonly IReadOnlyList<PlatformTag> All =
    [
        new("windows", "WiNDOWS", "win, win32/64, windows, msvc, mingw, .exe .msi .msix .appx",
            ["win", "win32", "win64", "windows", "msvc", "mingw", "mingw32", "mingw64", "w64", "w32", "winx64", "winx86", "win10", "win11", "win7"],
            [".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle"],
            ["windows", "win32", "win64"]),

        new("linux", "LiNUX", "linux, ubuntu, debian, fedora, musl, steamdeck, .AppImage .deb .rpm .flatpak .snap",
            ["linux", "linux32", "linux64", "ubuntu", "debian", "fedora", "centos", "rhel", "archlinux", "alpine", "musl",
             "manylinux", "glibc", "appimage", "flatpak", "snap", "deb", "rpm", "zsync", "steamdeck", "steamos", "gnulinux"],
            [".appimage", ".appimage.zsync", ".deb", ".rpm", ".flatpak", ".flatpakref", ".snap", ".pkg.tar.zst", ".pkg.tar.xz"],
            ["linux", "appimage"]),

        new("macos", "MacOS", "mac, macos, osx, darwin, universal2, .dmg .pkg .app",
            ["mac", "macos", "macosx", "osx", "darwin", "macintosh", "universal2", "dmg"],
            [".dmg", ".pkg", ".app.zip", ".app.tar.gz"],
            ["macos", "darwin", "macosx"]),

        new("android", "ANDROiD", "android, .apk .aab .xapk",
            ["android", "apk", "aab"],
            [".apk", ".aab", ".xapk", ".apks"],
            ["android"]),

        new("ios", "iOS", "ios, iphone, ipad, tvos, .ipa",
            ["ios", "iphone", "iphoneos", "ipad", "ipados", "tvos"],
            [".ipa"],
            []),

        new("bsd", "*BSD", "freebsd, openbsd, netbsd, dragonfly",
            ["freebsd", "openbsd", "netbsd", "dragonfly", "dragonflybsd", "bsd"],
            [],
            ["freebsd", "openbsd", "netbsd"]),

        new("source", "SOURCE", "src, source, sources (source archives uploaded as assets)",
            ["src", "source", "sources", "sourcecode"],
            [],
            [],
            Kind: TagKind.Kind),

        new("checksums", "CHECKSUMS", "SHA256SUMS, checksums.txt, .sha256 .md5 .sig .asc .minisig",
            ["checksums", "checksum", "sha256sums", "sha512sums", "sha1sums", "md5sums", "hashes"],
            [".sha256", ".sha256sum", ".sha512", ".sha512sum", ".sha1", ".md5", ".sig", ".asc", ".minisig", ".pem", ".sbom.json", ".intoto.jsonl"],
            [],
            Kind: TagKind.Kind),

        new("debug", "DEBUG/SYMBOLS", "debug, dbg, symbols, pdb, dsym, .pdb .debug",
            ["debug", "dbg", "symbols", "pdb", "pdbs", "dsym"],
            [".pdb", ".debug", ".dsym", ".dsym.zip"],
            [],
            Kind: TagKind.Kind),

        new("arm", "ARM", "arm, arm64, aarch64, armv7, armhf (any OS)",
            ["arm", "arm32", "arm64", "aarch64", "armv6", "armv7", "armv7l", "armv8", "armhf", "armel", "armeabi"],
            [],
            ["aarch64", "arm64"],
            Kind: TagKind.Arch),

        new("x86", "x86 (32-BiT)", "x86, i386, i686, 32bit (any OS)",
            ["x86", "i386", "i486", "i586", "i686", "ia32", "32bit", "x32"],
            [],
            [],
            Kind: TagKind.Arch),

        new("riscv", "RiSC-V", "riscv, riscv64",
            ["riscv", "riscv64", "rv64"],
            [],
            ["riscv"],
            Kind: TagKind.Arch),
    ];

    public static PlatformTag? Find(string id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"x86[_\-]?64|amd64|x64_64", RegexOptions.IgnoreCase)]
    private static partial Regex X64Regex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SplitRegex();

    public static string[] Tokenize(string fileName)
    {
        var s = X64Regex().Replace(fileName.ToLowerInvariant(), "x64");
        return SplitRegex().Split(s).Where(t => t.Length > 0).ToArray();
    }

    /// <summary>Returns the ids of every tag that matches the filename (empty = universal / unknown).</summary>
    public static IReadOnlyList<string> Detect(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        var tokens = new HashSet<string>(Tokenize(fileName));
        var result = new List<string>();

        foreach (var tag in All)
        {
            if (tag.Tokens.Any(tokens.Contains)
                || tag.Extensions.Any(e => lower.EndsWith(e, StringComparison.Ordinal))
                || tag.Substrings.Any(s => lower.Contains(s, StringComparison.Ordinal)))
            {
                result.Add(tag.Id);
            }
        }

        // ".pkg.tar.zst" is an Arch Linux package, not a macOS installer.
        if (result.Contains("macos") && result.Contains("linux") &&
            (lower.EndsWith(".pkg.tar.zst") || lower.EndsWith(".pkg.tar.xz")) &&
            !tokens.Overlaps(["mac", "macos", "macosx", "osx", "darwin", "apple"]))
        {
            result.Remove("macos");
        }

        return result;
    }
}
