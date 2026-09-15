# -*- coding: utf-8 -*-
"""生成 18/24/30 s 三段长音频，用于标定强切边界的实际容差。"""
import wave, math
import numpy as np
SR = 16000
rng = np.random.default_rng(7)
for dur in (18, 24, 30):
    n = int(SR * dur)
    t = np.arange(n) / SR
    sig = 0.3 * np.sin(2 * math.pi * 440 * t) + 0.05 * rng.standard_normal(n)
    data = (np.clip(sig, -1, 1) * 32767).astype("<i2")
    path = r"E:\MyTools\VRCTranslate\artifacts\vad-probe-%d.wav" % dur
    with wave.open(path, "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(SR)
        w.writeframes(data.tobytes())
    print(path, dur, "s", n, "samples")
