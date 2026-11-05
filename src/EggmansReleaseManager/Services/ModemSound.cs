using System.Runtime.InteropServices;

namespace EggmansReleaseManager.Services;

/// <summary>A log line to print when playback reaches <see cref="At"/>.</summary>
public sealed record ModemCue(TimeSpan At, string Text);

/// <summary>
/// Synthesizes a dial-up modem handshake (no audio files shipped): off-hook dial tone, DTMF dialing,
/// ringback, the 2100 Hz answer tone with phase reversals, V.8/V.21 FSK chirps and the V.34-style
/// QAM training hiss. Output is a 16-bit mono WAV in memory, played with winmm PlaySound.
/// </summary>
public static class ModemSound
{
    public const int SampleRate = 22050;
    public const string Number = "1-800-EGG-BBS";

    /// <summary>Set by tools/tests to keep things quiet.</summary>
    public static bool Muted { get; set; }

    private static readonly Dictionary<char, (double Low, double High)> Dtmf = new()
    {
        ['1'] = (697, 1209), ['2'] = (697, 1336), ['3'] = (697, 1477),
        ['4'] = (770, 1209), ['5'] = (770, 1336), ['6'] = (770, 1477),
        ['7'] = (852, 1209), ['8'] = (852, 1336), ['9'] = (852, 1477),
        ['0'] = (941, 1336), ['*'] = (941, 1209), ['#'] = (941, 1477),
    };

    /// <summary>Phone keypad letters -> digits, so 1-800-EGG-BBS dials 18003443227.</summary>
    public static string ToDigits(string number) => new(number.ToUpperInvariant().Select(c => c switch
    {
        >= '0' and <= '9' => c,
        >= 'A' and <= 'C' => '2', >= 'D' and <= 'F' => '3', >= 'G' and <= 'I' => '4',
        >= 'J' and <= 'L' => '5', >= 'M' and <= 'O' => '6', >= 'P' and <= 'S' => '7',
        >= 'T' and <= 'V' => '8', >= 'W' and <= 'Z' => '9',
        _ => '\0',
    }).Where(c => c != '\0').ToArray());

    public static (byte[] Wav, IReadOnlyList<ModemCue> Cues) Build(int seed = 1987)
    {
        var s = new List<float>(SampleRate * 14);
        var cues = new List<ModemCue>();
        var rng = new Random(seed);
        TimeSpan Now() => TimeSpan.FromSeconds(s.Count / (double)SampleRate);
        void Cue(string text) => cues.Add(new ModemCue(Now(), text));

        Cue("ATZ");
        Silence(s, 0.25);
        Cue("OK");
        Cue("ATX4DT " + Number);
        Tones(s, 0.9, 0.22, 350, 440);                 // dial tone
        Silence(s, 0.15);
        foreach (var d in ToDigits(Number))
        {
            var (lo, hi) = Dtmf[d];
            Tones(s, 0.085, 0.3, lo, hi);
            Silence(s, 0.065);
        }
        Silence(s, 0.5);
        Cue("RINGING");
        Tones(s, 1.4, 0.18, 440, 480);                 // ringback
        Silence(s, 0.55);
        Cue("ANSWER 2100Hz / V.8 negotiation");
        AnswerTone(s, 1.9, 0.33);
        Silence(s, 0.08);
        Fsk(s, rng, 0.55, 0.28, 980, 1180, 300);       // calling modem, V.21 ch1
        Fsk(s, rng, 0.45, 0.28, 1650, 1850, 300);      // answering modem, V.21 ch2
        Silence(s, 0.06);
        Cue("TRAiNiNG V.34 ...");
        Tones(s, 0.55, 0.24, 1200, 2400);              // the "bong"
        Silence(s, 0.04);
        Screech(s, rng, 1.25, 0.3);
        Silence(s, 0.03);
        Qam(s, rng, 2.1, 0.26, 1800, 2400);            // the hiss
        Qam(s, rng, 0.6, 0.18, 1959, 2743);
        Cue("CONNECT 28800/ARQ/V34/LAPM/V42BIS");
        Silence(s, 0.2);

        return (ToWav(s), cues);
    }

    // ───────── synth building blocks ─────────

    private static void Silence(List<float> s, double sec) => s.AddRange(new float[(int)(sec * SampleRate)]);

    private static void Tones(List<float> s, double sec, double amp, params double[] freqs)
    {
        var n = (int)(sec * SampleRate);
        var start = s.Count;
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)SampleRate;
            var v = freqs.Sum(f => Math.Sin(2 * Math.PI * f * t)) / freqs.Length;
            s.Add((float)(v * amp));
        }
        Fade(s, start, n);
    }

    /// <summary>ITU-T V.25 answer tone: 2100 Hz with a 180° phase reversal every 450 ms (the "clicks").</summary>
    private static void AnswerTone(List<float> s, double sec, double amp)
    {
        var n = (int)(sec * SampleRate);
        var start = s.Count;
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)SampleRate;
            var flip = (int)(t / 0.45) % 2 == 0 ? 0 : Math.PI;
            s.Add((float)(Math.Sin(2 * Math.PI * 2100 * t + flip) * amp));
        }
        Fade(s, start, n);
    }

    /// <summary>Binary FSK with continuous phase, random data.</summary>
    private static void Fsk(List<float> s, Random rng, double sec, double amp, double mark, double space, int baud)
    {
        var n = (int)(sec * SampleRate);
        var start = s.Count;
        var perBit = SampleRate / baud;
        double phase = 0, f = mark;
        for (var i = 0; i < n; i++)
        {
            if (i % perBit == 0) f = rng.Next(2) == 0 ? mark : space;
            phase += 2 * Math.PI * f / SampleRate;
            s.Add((float)(Math.Sin(phase) * amp));
        }
        Fade(s, start, n);
    }

    /// <summary>Line probing: chirpy multi-tone sweeps plus noise, the harsh middle part.</summary>
    private static void Screech(List<float> s, Random rng, double sec, double amp)
    {
        var n = (int)(sec * SampleRate);
        var start = s.Count;
        double p1 = 0, p2 = 0, lp = 0;
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)SampleRate;
            var f1 = 600 + 2600 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 3.1 * t));
            var f2 = 3000 - 1800 * (t / sec);
            p1 += 2 * Math.PI * f1 / SampleRate;
            p2 += 2 * Math.PI * f2 / SampleRate;
            lp += 0.35 * ((rng.NextDouble() * 2 - 1) - lp);       // softened noise
            var v = 0.45 * Math.Sin(p1) + 0.3 * Math.Sign(Math.Sin(p2)) * 0.6 + 0.5 * lp;
            s.Add((float)(Math.Clamp(v, -1, 1) * amp));
        }
        Fade(s, start, n);
    }

    /// <summary>Random-symbol QAM on a carrier: sounds exactly like the classic broadband hiss.</summary>
    private static void Qam(List<float> s, Random rng, double sec, double amp, double carrier, double baud)
    {
        var n = (int)(sec * SampleRate);
        var start = s.Count;
        double i0 = 1, q0 = 0, iT = 1, qT = 0;
        var perSym = SampleRate / baud;
        double next = 0;
        int[] levels = [-3, -1, 1, 3];
        for (var k = 0; k < n; k++)
        {
            if (k >= next)
            {
                next += perSym;
                iT = levels[rng.Next(4)] / 3.0;
                qT = levels[rng.Next(4)] / 3.0;
            }
            i0 += 0.6 * (iT - i0);   // a little pulse shaping
            q0 += 0.6 * (qT - q0);
            var t = k / (double)SampleRate;
            var v = i0 * Math.Cos(2 * Math.PI * carrier * t) - q0 * Math.Sin(2 * Math.PI * carrier * t);
            s.Add((float)(v * 0.7 * amp));
        }
        Fade(s, start, n);
    }

    private static void Fade(List<float> s, int start, int n, double ms = 6)
    {
        var f = Math.Min((int)(ms / 1000 * SampleRate), n / 2);
        for (var i = 0; i < f; i++)
        {
            var g = i / (float)f;
            s[start + i] *= g;
            s[start + n - 1 - i] *= g;
        }
    }

    private static byte[] ToWav(List<float> samples)
    {
        var data = samples.Count * 2;
        using var ms = new MemoryStream(44 + data);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(SampleRate); w.Write(SampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data);
        foreach (var v in samples) w.Write((short)Math.Round(Math.Clamp(v, -1f, 1f) * 32000));
        w.Flush();
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
