"""Builds src/EggmansReleaseManager/Assets/handshake.wav from a real dial-up handshake recording.

The recording's own DTMF digits are replaced with ones that dial 1-800-EGG-MAN1 (1-800-344-6261),
using the recording's exact timing (100 ms tone every 200 ms) and levels. The audio is then
downmixed to mono, resampled to 22050 Hz and cut off with a short fade right after CONNECT
(a real modem mutes its speaker once it's connected).

    python -I tools/make_handshake.py "files/Dial Up Modem Handshake Sound.wav"

Needs numpy. Verifies the result by decoding the DTMF digits back out.
"""
import sys, wave
from pathlib import Path
import numpy as np

NUMBER = "18003446261"           # 1-800-EGG-MAN1
OUT_RATE = 22050
CUT_AT = 23.20                   # seconds; CONNECT data starts at ~22.7
FADE = 0.25
DTMF = {'1': (697, 1209), '2': (697, 1336), '3': (697, 1477), '4': (770, 1209), '5': (770, 1336),
        '6': (770, 1477), '7': (852, 1209), '8': (852, 1336), '9': (852, 1477), '0': (941, 1336)}

def load(path):
    w = wave.open(str(path))
    assert w.getsampwidth() == 2
    a = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float64) / 32768
    return a.reshape(-1, w.getnchannels()).mean(1), w.getframerate()

def splice_dtmf(a, sr):
    # window that holds the original 11 digits (0.79 .. 2.90 s)
    s0, s1 = int(0.78 * sr), int(2.95 * sr)
    n = s1 - s0
    # background: the line noise that follows the dialing (3.0 .. 5.0 s), looped
    bed = a[int(3.0 * sr):int(5.0 * sr)]
    new = np.resize(bed, n).copy()
    t = np.arange(n) / sr
    # dial tone keeps going under the first digit, then drops (as in the recording)
    dial = 0.022 * np.sin(2 * np.pi * 350 * t) + 0.091 * np.sin(2 * np.pi * 440 * t)
    env = np.clip((0.95 - (t + 0.78)) / 0.01, 0, 1)
    new += dial * env
    fade = int(0.003 * sr)
    for k, d in enumerate(NUMBER):
        lo, hi = DTMF[d]
        i0 = int((0.80 + 0.2 * k - 0.78) * sr)
        m = int(0.100 * sr)
        tt = np.arange(m) / sr
        tone = 0.11 * np.sin(2 * np.pi * lo * tt) + 0.16 * np.sin(2 * np.pi * hi * tt)
        ramp = np.ones(m); ramp[:fade] = np.linspace(0, 1, fade); ramp[-fade:] = np.linspace(1, 0, fade)
        new[i0:i0 + m] += tone * ramp
    # 10 ms crossfades into the original at both ends
    x = int(0.01 * sr)
    r = np.linspace(0, 1, x)
    new[:x] = a[s0:s0 + x] * (1 - r) + new[:x] * r
    new[-x:] = new[-x:] * (1 - r) + a[s1 - x:s1] * r
    out = a.copy()
    out[s0:s1] = new
    return out

def resample(a, sr, to):
    if sr == to:
        return a
    assert sr % to == 0, "integer decimation only"
    q = sr // to
    taps = 255
    k = np.arange(taps) - (taps - 1) / 2
    cutoff = 0.9 / q / 2                         # fraction of the input rate
    h = 2 * cutoff * np.sinc(2 * cutoff * k) * np.blackman(taps)
    h /= h.sum()
    return np.convolve(a, h, mode="same")[::q]

def decode(a, sr):
    lows, highs = [697, 770, 852, 941], [1209, 1336, 1477]
    inv = {v: k for k, v in DTMF.items()}
    digits = []
    for k in range(11):
        i = int((0.82 + 0.2 * k) * sr); seg = a[i:i + int(0.06 * sr)]
        t = np.arange(len(seg)) / sr
        g = lambda f: abs((seg * np.exp(-2j * np.pi * f * t)).sum())
        digits.append(inv[(max(lows, key=g), max(highs, key=g))])
    return "".join(digits)

def main():
    src = Path(sys.argv[1])
    out = Path(__file__).resolve().parent.parent / "src/EggmansReleaseManager/Assets/handshake.wav"
    a, sr = load(src)
    a = splice_dtmf(a, sr)
    a = resample(a, sr, OUT_RATE)
    a = a[:int(CUT_AT * OUT_RATE)]
    f = int(FADE * OUT_RATE)
    a[-f:] *= np.linspace(1, 0, f)
    a /= max(1.0, np.abs(a).max() / 0.95)
    got = decode(a, OUT_RATE)
    assert got == NUMBER, f"DTMF check failed: {got}"
    pcm = (np.clip(a, -1, 1) * 32767).round().astype("<i2")
    with wave.open(str(out), "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(OUT_RATE)
        w.writeframes(pcm.tobytes())
    print(f"wrote {out} ({len(a) / OUT_RATE:.2f}s, {out.stat().st_size:,} bytes), dials {got}")

if __name__ == "__main__":
    main()
