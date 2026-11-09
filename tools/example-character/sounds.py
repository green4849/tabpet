# The example sounds: short, soft tones made from sine waves (nothing recorded or sampled).
# usage: python3 sounds.py <out dir>   -> poke.wav, drop.wav, welcome.wav, dance.wav
import sys, wave, numpy as np
out = sys.argv[1] if len(sys.argv) > 1 else "."
SR = 44100

def tone(f0, f1, dur, vol=0.22):
    t = np.arange(int(SR * dur)) / SR
    f = np.linspace(f0, f1, len(t))
    ph = 2 * np.pi * np.cumsum(f) / SR
    env = np.minimum(1, t / 0.008) * np.exp(-t / (dur * 0.35))      # quick attack, soft decay
    return vol * env * (np.sin(ph) + 0.25 * np.sin(2 * ph))

def seq(*parts, gap=0.03):
    sil = np.zeros(int(SR * gap)); out = []
    for p in parts: out += [p, sil]
    return np.concatenate(out)

clips = {
    "poke":    seq(tone(880, 990, 0.09), tone(1175, 1320, 0.11)),
    "drop":    seq(tone(700, 330, 0.22)),
    "welcome": seq(tone(660, 660, 0.14), tone(784, 784, 0.14), tone(988, 988, 0.30)),
    "dance":   seq(tone(523, 523, 0.12), tone(659, 659, 0.12), tone(784, 784, 0.12), tone(1047, 1047, 0.32), gap=0.05),
}
for name, x in clips.items():
    with wave.open(f"{out}/{name}.wav", "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(SR)
        w.writeframes((np.clip(x, -1, 1) * 32767).astype("<i2").tobytes())
    print("wrote", f"{out}/{name}.wav", f"{len(x)/SR:.2f}s")
