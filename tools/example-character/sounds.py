# The example sounds for Sprout: small, soft, wooden "kalimba" plucks and little glides,
# made from sine waves (nothing recorded or sampled). One clip per action:
#   poke     clicked                      a tiny two-note "pik-puik"
#   drop     let go after a drag          a soft falling "pyu" and a little bump on landing
#   hop      jumps up onto a window       a quick rising "pyon"
#   welcome  you're back after a while    a bright three-note hello
#   dance    music started                a short happy tune
#   sleep    dozes off (5 min no input)   a slow, quiet falling sigh
# usage: python3 sounds.py <out dir>
import sys, wave
import numpy as np

OUT = sys.argv[1] if len(sys.argv) > 1 else "."
SR = 44100
PEAK = 0.32                                   # keep it quiet: well under full scale

def t_(dur): return np.arange(int(SR * dur)) / SR

def pluck(freq, dur=0.35, vol=1.0):
    """a kalimba-like note: fundamental plus the bar's inharmonic overtone, quick attack, wooden decay"""
    t = t_(dur)
    env = np.minimum(1, t / 0.004) * np.exp(-t / (dur * 0.28))
    tone = np.sin(2 * np.pi * freq * t) + 0.35 * np.exp(-t / 0.05) * np.sin(2 * np.pi * freq * 2.76 * t) \
         + 0.12 * np.sin(2 * np.pi * freq * 2 * t)
    return vol * env * tone

def glide(f0, f1, dur, vol=1.0, curve=1.0, wobble=0.0):
    """a soft voice-like slide from f0 to f1 (curve > 1 bends late), with a little vibrato"""
    t = t_(dur); u = (t / dur) ** curve
    f = f0 * (f1 / f0) ** u * (1 + wobble * np.sin(2 * np.pi * 9 * t))
    ph = 2 * np.pi * np.cumsum(f) / SR
    env = np.sin(np.pi * np.minimum(1, t / dur)) ** 0.7
    return vol * env * (np.sin(ph) + 0.2 * np.sin(2 * ph))

def at(parts, total):
    """mix (start time, clip) pairs into one clip"""
    out = np.zeros(int(SR * total))
    for start, x in parts:
        i = int(SR * start); n = min(len(x), len(out) - i); out[i:i + n] += x[:n]
    return out

N = {"C5": 523.3, "G5": 784.0, "A5": 880.0, "C6": 1046.5, "D6": 1174.7, "E6": 1318.5, "G6": 1568.0}

clips = {
    "poke":    at([(0, pluck(N["E6"], 0.18, 0.8)), (0.07, pluck(N["A5"] * 2, 0.25))], 0.34),
    "drop":    at([(0, glide(1100, 420, 0.22, 0.8, curve=1.6)), (0.2, pluck(N["C5"] / 2, 0.22, 0.7))], 0.45),
    "hop":     at([(0, glide(500, 1250, 0.16, 0.8, curve=0.7))], 0.2),
    "welcome": at([(0, pluck(N["C6"], 0.4)), (0.12, pluck(N["E6"], 0.4)), (0.24, pluck(N["G6"], 0.6))], 0.85),
    "dance":   at([(0.00, pluck(N["G5"], 0.3)), (0.13, pluck(N["C6"], 0.3)), (0.26, pluck(N["E6"], 0.3)),
                   (0.39, pluck(N["D6"], 0.3)), (0.52, pluck(N["E6"], 0.3)), (0.65, pluck(N["G6"], 0.6))], 1.25),
    "sleep":   at([(0, glide(620, 330, 0.7, 0.55, curve=1.3, wobble=0.004))], 0.75),
}
for name, x in clips.items():
    x = x / max(1e-9, np.abs(x).max()) * PEAK
    x = x * np.minimum(1, (len(x) - np.arange(len(x))) / (SR * 0.01))      # no click at the end
    with wave.open(f"{OUT}/{name}.wav", "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(SR)
        w.writeframes((x * 32767).astype("<i2").tobytes())
    print(f"wrote {OUT}/{name}.wav {len(x) / SR:.2f}s")
