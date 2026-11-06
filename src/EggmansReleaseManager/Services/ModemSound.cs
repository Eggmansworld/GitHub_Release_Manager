using System.Runtime.InteropServices;

namespace EggmansReleaseManager.Services;

/// <summary>A log line to print when playback reaches <see cref="At"/>.</summary>
public sealed record ModemCue(TimeSpan At, string Text);

/// <summary>
/// The dial-up handshake: a real 56k-era recording (Assets/handshake.wav, built by tools/make_handshake.py,
/// which swaps in DTMF digits for <see cref="Number"/>), played with winmm PlaySound, plus the AT-command
/// log lines timed to what you hear.
/// </summary>
public static class ModemSound
{
    public const int SampleRate = 22050;
    public const string Number = "1-800-EGG-MAN1";

    /// <summary>Set by tools/tests to keep things quiet.</summary>
    public static bool Muted { get; set; }

    /// <summary>Phone keypad letters -> digits, so 1-800-EGG-MAN1 dials 18003446261.</summary>
    public static string ToDigits(string number) => new(number.ToUpperInvariant().Select(c => c switch
    {
        >= '0' and <= '9' => c,
        >= 'A' and <= 'C' => '2', >= 'D' and <= 'F' => '3', >= 'G' and <= 'I' => '4',
        >= 'J' and <= 'L' => '5', >= 'M' and <= 'O' => '6', >= 'P' and <= 'S' => '7',
        >= 'T' and <= 'V' => '8', >= 'W' and <= 'Z' => '9',
        _ => '\0',
    }).Where(c => c != '\0').ToArray());

    /// <summary>"1-800-344-6261" style, for the log.</summary>
    public static string Pretty(string number)
    {
        var d = ToDigits(number);
        return d.Length == 11 ? $"{d[0]}-{d[1..4]}-{d[4..7]}-{d[7..]}" : d;
    }

    // Where each stage starts in the recording (seconds).
    private static readonly (double At, string Text)[] Timeline =
    [
        (0.00, "ATZ"),
        (0.12, "OK"),
        (0.30, "ATX4DT " + Number),
        (0.80, "DiALiNG " + Pretty(Number) + " ..."),
        (3.00, "RiNGiNG"),
        (5.10, "ANSWER · V.8bis handshake"),
        (8.90, "2100Hz ANSam tone"),
        (11.00, "V.8 CM/JM negotiation · V.21 FSK"),
        (12.20, "LiNE PROBiNG ..."),
        (13.70, "TRAiNiNG V.34 equalizer ..."),
        (16.20, "TRAiNiNG echo canceller ..."),
        (20.40, "FiNAL RATE NEGOTiATiON ..."),
        (22.70, "CONNECT 33600/ARQ/V34/LAPM/V42BIS"),
    ];

    private static byte[]? _wav;

    public static (byte[] Wav, IReadOnlyList<ModemCue> Cues) Build()
    {
        _wav ??= LoadAsset();
        return (_wav, Timeline.Select(t => new ModemCue(TimeSpan.FromSeconds(t.At), t.Text)).ToList());
    }

    private static byte[] LoadAsset()
    {
        using var s = typeof(ModemSound).Assembly.GetManifestResourceStream("EggmansReleaseManager.handshake.wav")
                      ?? throw new InvalidOperationException("handshake.wav resource missing");
        using var ms = new MemoryStream((int)s.Length);
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // ───────── playback (Windows) ─────────

    private const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);

    private static GCHandle _pinned; // the buffer must stay put while Windows plays it asynchronously

    /// <summary>Starts playback without blocking. Returns false if sound isn't available.</summary>
    public static bool Play(byte[] wav)
    {
        if (Muted || !OperatingSystem.IsWindows()) return false;
        try
        {
            Stop();
            _pinned = GCHandle.Alloc(wav, GCHandleType.Pinned);
            return PlaySound(_pinned.AddrOfPinnedObject(), IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        }
        catch
        {
            return false;
        }
    }

    public static void Stop()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { PlaySound(IntPtr.Zero, IntPtr.Zero, 0); } catch { /* ignore */ }
        if (_pinned.IsAllocated) _pinned.Free();
    }
}
