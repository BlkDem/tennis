"""
Synthesises the game's sounds, so the project needs no recorded audio and no third-party licences.

    python make_sounds.py <audio_dir>

Needs numpy (Blender's bundled Python has it too). Every sound is modelled from what makes it:
a string bed ringing and a ball compressing for a racket hit, a dull thud for a bounce, stick-slip
for a shoe squeak, thousands of separate claps for applause, formant-shaped noise for voices.
Writes 16-bit mono WAV files at 44.1 kHz.
"""
import os
import sys
import wave

import numpy as np

RATE = 44100
rng = np.random.default_rng(20261107)


def log(message):
    print(f"[audio] {message}", flush=True)


def time_axis(seconds):
    return np.arange(int(seconds * RATE)) / RATE


def decay(t, tau, attack=0.0005):
    """An exponential decay with a short attack, so nothing starts with a click."""
    rise = np.clip(t / attack, 0.0, 1.0) if attack > 0 else 1.0
    return rise * np.exp(-t / tau)


def ring(t, frequency, tau, phase=None):
    phase = rng.uniform(0, 2 * np.pi) if phase is None else phase
    return np.sin(2 * np.pi * frequency * t + phase) * decay(t, tau)


def band(signal, low, high, soft=0.15):
    """Band-pass in the frequency domain with soft edges. Periodic, so a loop stays seamless."""
    spectrum = np.fft.rfft(signal)
    freqs = np.fft.rfftfreq(signal.size, 1.0 / RATE)
    lo = 1.0 / (1.0 + np.exp(-(freqs - low) / max(low * soft, 1.0)))
    hi = 1.0 / (1.0 + np.exp((freqs - high) / max(high * soft, 1.0)))
    return np.fft.irfft(spectrum * lo * hi, signal.size)


def formant(signal, centres, widths, gains):
    """Shapes noise like a vowel: a few resonant peaks."""
    spectrum = np.fft.rfft(signal)
    freqs = np.fft.rfftfreq(signal.size, 1.0 / RATE)
    shape = sum(g * np.exp(-0.5 * ((freqs - c) / w) ** 2) for c, w, g in zip(centres, widths, gains))
    return np.fft.irfft(spectrum * shape, signal.size)


def smooth_noise(n, rate_hz, periodic=True):
    """Slowly varying positive noise, used to make many voices rise and fall independently."""
    spectrum = np.fft.rfft(rng.standard_normal(n))
    freqs = np.fft.rfftfreq(n, 1.0 / RATE)
    spectrum *= np.exp(-(freqs / rate_hz) ** 2)
    curve = np.fft.irfft(spectrum, n)
    curve /= np.abs(curve).max() + 1e-9
    return 0.5 + 0.5 * curve


def normalise(signal, peak=0.89):
    return signal * (peak / (np.abs(signal).max() + 1e-9))


def fade(signal, seconds=0.004):
    n = int(seconds * RATE)
    if n > 0:
        signal[-n:] *= np.linspace(1.0, 0.0, n)
    return signal


def write(directory, name, signal, peak=0.89):
    path = os.path.join(directory, name + ".wav")
    data = (np.clip(normalise(signal, peak), -1.0, 1.0) * 32767).astype(np.int16)
    with wave.open(path, "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(RATE)
        handle.writeframes(data.tobytes())
    log(f"{name}: {signal.size / RATE:.2f} s")


# ---------------------------------------------------------------- Court sounds


def racket_hit(kind):
    """
    The string bed rings at a few hundred hertz with inharmonic partials, the ball adds a short 'pock'
    and the frame a low thump. A clean hit is bright, a slice duller and brushed, a frame hit clanks.
    """
    t = time_axis(0.32)
    if kind == "frame":
        partials = [(rng.uniform(290, 330), 0.07, 0.7), (rng.uniform(840, 900), 0.05, 0.5), (rng.uniform(1580, 1680), 0.035, 0.35)]
        click = band(rng.standard_normal(t.size), 600, 5000) * decay(t, 0.004)
        sound = sum(g * ring(t, f, tau) for f, tau, g in partials) + 0.4 * click
        # The loose ball rattles against the frame.
        sound += 0.25 * band(rng.standard_normal(t.size), 1500, 6000) * decay(t, 0.02) * (np.sin(2 * np.pi * 45 * t) > 0)
        return fade(sound)

    bright = kind == "hard"
    f0 = rng.uniform(540, 640) if bright else rng.uniform(430, 500)
    strings = ring(t, f0, 0.028 if bright else 0.018) + 0.5 * ring(t, f0 * 2.31, 0.014) + 0.25 * ring(t, f0 * 3.87, 0.008)
    pock = ring(t, rng.uniform(1050, 1250) if bright else rng.uniform(850, 950), 0.010)
    thump = ring(t, rng.uniform(160, 190), 0.016)
    transient = band(rng.standard_normal(t.size), 2000 if bright else 1200, 9000 if bright else 4500) * decay(t, 0.0025, attack=0.0002)
    sound = 0.6 * strings + 0.5 * pock + 0.45 * thump + (0.6 if bright else 0.3) * transient
    if not bright:
        # A slice brushes the strings across the ball.
        sound += 0.22 * band(rng.standard_normal(t.size), 1000, 4000) * decay(t, 0.03, attack=0.004)
    return fade(sound)


def bounce():
    t = time_axis(0.18)
    thud = band(rng.standard_normal(t.size), 60, 900) * decay(t, 0.009, attack=0.0004)
    body = ring(t, rng.uniform(130, 160), 0.022)
    pock = ring(t, rng.uniform(650, 760), 0.006)
    return fade(0.8 * thud + 0.6 * body + 0.35 * pock)


def net():
    t = time_axis(0.42)
    rustle = band(rng.standard_normal(t.size), 250, 3200) * decay(t, 0.09, attack=0.008)
    rustle *= 0.6 + 0.4 * smooth_noise(t.size, 40.0)
    thump = ring(t, 95.0, 0.05)
    return fade(rustle + 0.5 * thump)


def net_cord():
    t = time_axis(0.4)
    tick = ring(t, 2250, 0.009) + 0.6 * ring(t, 3480, 0.006)
    twang = ring(t, 218, 0.09) + 0.4 * ring(t, 437, 0.06)
    return fade(0.8 * tick + 0.45 * twang)


def squeak():
    """Rubber on acrylic sticks and slips many times a second, which is the squeak's rough buzz."""
    t = time_axis(0.16)
    start, end = rng.uniform(2000, 2300), rng.uniform(2600, 3000)
    phase = 2 * np.pi * np.cumsum(np.linspace(start, end, t.size)) / RATE
    tone = np.sin(phase) + 0.45 * np.sin(2 * phase) + 0.2 * np.sin(3 * phase)
    stick_slip = 0.6 + 0.4 * np.sign(np.sin(2 * np.pi * rng.uniform(55, 75) * t))
    envelope = np.sin(np.pi * np.clip(t / t[-1], 0, 1)) ** 0.6
    return fade(tone * stick_slip * envelope)


# ---------------------------------------------------------------- Crowd


def crowd_murmur(seconds=12.0):
    """
    Thousands of people talking quietly: many vowel-coloured noise voices rising and falling at their own pace,
    over a low room rumble. Built from periodic noise, so the end runs straight back into the start.
    """
    n = int(seconds * RATE)
    total = np.zeros(n)
    vowels = [((700, 1200, 2600), (90, 110, 160)), ((400, 2000, 2550), (70, 120, 160)), ((350, 900, 2400), (60, 90, 160)),
              ((550, 1700, 2500), (80, 120, 160)), ((300, 870, 2240), (60, 90, 150))]
    for _ in range(36):
        centres, widths = vowels[rng.integers(len(vowels))]
        scale = rng.uniform(0.85, 1.25)
        voice = formant(rng.standard_normal(n), [c * scale for c in centres], widths, (1.0, 0.6, 0.25))
        voice /= np.abs(voice).max() + 1e-9
        total += voice * smooth_noise(n, rng.uniform(0.4, 2.5)) ** 2
    rumble = band(rng.standard_normal(n), 40, 300)
    total = total / (np.abs(total).max() + 1e-9) + 0.25 * rumble / (np.abs(rumble).max() + 1e-9)
    return total


def claps(seconds, density, envelope):
    """A crowd's applause: separate claps, each a short burst of noise coloured by the shape of the hands."""
    n = int(seconds * RATE)
    t = np.arange(n) / RATE
    rate = density * envelope(t)
    impulses = np.zeros(n)
    count = int(rate.sum() / RATE)
    times = rng.choice(n, size=count, p=rate / rate.sum())
    np.add.at(impulses, times, rng.uniform(0.3, 1.0, count) * rng.choice([-1.0, 1.0], count))

    kernel_t = time_axis(0.03)
    result = np.zeros(n)
    groups = np.array_split(rng.permutation(np.nonzero(impulses)[0]), 6)
    for group in groups:
        kernel = band(rng.standard_normal(kernel_t.size), rng.uniform(700, 1300), rng.uniform(2500, 5000)) * decay(kernel_t, rng.uniform(0.002, 0.005), attack=0.0003)
        part = np.zeros(n)
        part[group] = impulses[group]
        result += np.fft.irfft(np.fft.rfft(part, n + kernel.size) * np.fft.rfft(kernel, n + kernel.size), n + kernel.size)[:n]
    return result


def applause():
    seconds = 4.5

    def envelope(t):
        rise = np.clip(t / 0.25, 0, 1)
        fall = np.clip(1.0 - (t - 1.6) / 2.6, 0, 1) ** 1.5
        return rise * fall + 0.001

    return fade(claps(seconds, 900.0, envelope), 0.3)


def cheer():
    """Applause with voices: a roar shaped like an open 'ah', swelling and dying away, and a few whistles."""
    seconds = 5.5
    n = int(seconds * RATE)
    t = np.arange(n) / RATE

    def envelope(x):
        return np.clip(x / 0.2, 0, 1) * np.clip(1.0 - (x - 2.2) / 3.0, 0, 1) ** 1.3 + 0.001

    hands = claps(seconds, 1100.0, envelope)
    roar = np.zeros(n)
    for _ in range(14):
        scale = rng.uniform(0.8, 1.35)
        voice = formant(rng.standard_normal(n), (750 * scale, 1250 * scale, 2700 * scale), (110, 140, 200), (1.0, 0.7, 0.3))
        roar += voice / (np.abs(voice).max() + 1e-9) * smooth_noise(n, rng.uniform(1.0, 3.0))
    roar = roar / (np.abs(roar).max() + 1e-9) * np.clip(t / 0.35, 0, 1) * np.clip(1.0 - (t - 1.4) / 2.5, 0, 1) ** 1.6

    whistles = np.zeros(n)
    for _ in range(3):
        start = rng.uniform(0.2, 1.2)
        length = rng.uniform(0.4, 0.8)
        local = np.clip((t - start) / length, 0, 1)
        active = (t >= start) & (t <= start + length)
        frequency = rng.uniform(2200, 2800) + 500 * np.sin(np.pi * local)
        whistles += np.sin(2 * np.pi * np.cumsum(frequency) / RATE) * active * np.sin(np.pi * local)

    sound = hands / (np.abs(hands).max() + 1e-9) + 0.75 * roar + 0.12 * whistles
    return fade(sound, 0.4)


def ooh():
    """The crowd's sigh at a near miss: a rounded 'oo' that rises and falls."""
    seconds = 1.8
    n = int(seconds * RATE)
    t = np.arange(n) / RATE
    sound = np.zeros(n)
    for _ in range(18):
        scale = rng.uniform(0.85, 1.2)
        voice = formant(rng.standard_normal(n), (320 * scale, 800 * scale, 2300), (60, 90, 150), (1.0, 0.45, 0.1))
        sound += voice / (np.abs(voice).max() + 1e-9)
    swell = np.sin(np.pi * np.clip(t / seconds, 0, 1)) ** 1.5
    return fade(sound * swell, 0.2)


def main():
    directory = sys.argv[1] if len(sys.argv) > 1 else "."
    os.makedirs(directory, exist_ok=True)

    for i in range(3):
        write(directory, f"HitHard_{i}", racket_hit("hard"))
        write(directory, f"HitSoft_{i}", racket_hit("soft"))
        write(directory, f"Bounce_{i}", bounce())
    for i in range(2):
        write(directory, f"HitFrame_{i}", racket_hit("frame"))
        write(directory, f"Squeak_{i}", squeak())
    write(directory, "Net", net())
    write(directory, "NetCord", net_cord())
    write(directory, "CrowdMurmur", crowd_murmur(), peak=0.6)
    write(directory, "Applause", applause())
    write(directory, "Cheer", cheer())
    write(directory, "Ooh", ooh(), peak=0.7)
    log("done")


main()
