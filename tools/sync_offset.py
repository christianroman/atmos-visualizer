"""Phase 0, experiment 6 (sync): is DAMF sample 0 the same instant as MKV audio t = 0?

Decodes the MKV's TrueHD track with ffmpeg (which gives the 7.1 presentation, not objects),
takes its centre channel, and cross-correlates it with the DAMF centre object (CAF channel 8,
the object at [0, 1, 0]). The lag of the correlation peak is the offset between the two.

Method check first: a signal correlated against a copy of itself delayed by a known amount
must report exactly that delay.

usage: python tools/sync_offset.py <movie.mkv> <NAME.atmos.audio> <caf_channel_1based> [seconds]
"""
import subprocess
import sys

import numpy as np

FS = 48000


def ffmpeg_channel(path, channel_index0, seconds, map_spec):
    cmd = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-i", path, "-map", map_spec,
           "-t", str(seconds), "-af", "pan=mono|c0=c%d" % channel_index0,
           "-f", "f32le", "-acodec", "pcm_f32le", "-"]
    raw = subprocess.run(cmd, check=True, capture_output=True).stdout
    return np.frombuffer(raw, dtype="<f4").astype(np.float64)


def xcorr_lag(a, b, max_lag):
    """Lag L (samples) maximising sum a[n] * b[n + L]; positive L means b is later than a."""
    n = len(a) + len(b)
    nfft = 1 << (n - 1).bit_length()
    A = np.fft.rfft(a, nfft)
    B = np.fft.rfft(b, nfft)
    c = np.fft.irfft(np.conj(A) * B, nfft)
    lags = np.concatenate([np.arange(0, max_lag + 1), np.arange(-max_lag, 0)])
    vals = np.concatenate([c[:max_lag + 1], c[-max_lag:]])
    i = int(np.argmax(vals))
    lag = int(lags[i])
    # normalised correlation coefficient at that lag
    if lag >= 0:
        x, y = a[:len(a) - lag], b[lag:len(a)]
    else:
        x, y = a[-lag:], b[:len(a) + lag]
    m = min(len(x), len(y))
    x, y = x[:m], y[:m]
    rho = float(np.dot(x, y) / np.sqrt(np.dot(x, x) * np.dot(y, y)))
    return lag, rho


def main():
    mkv, caf, ch = sys.argv[1], sys.argv[2], int(sys.argv[3])
    seconds = float(sys.argv[4]) if len(sys.argv) > 4 else 60

    damf = ffmpeg_channel(caf, ch - 1, seconds, "0:a:0")
    print("DAMF ch%d: %d samples, RMS %.2f dBFS" % (ch, len(damf), 20 * np.log10(np.sqrt(np.mean(damf ** 2)))))

    # method check: known delays
    for d in (0, 37, -250, 1104):
        shifted = np.roll(damf, d)
        lag, rho = xcorr_lag(damf, shifted, 5000)
        print("method check: delayed copy by %+5d samples -> measured lag %+5d (rho %.4f)" % (d, lag, rho))

    mkv_c = ffmpeg_channel(mkv, 2, seconds, "0:a:0")  # 7.1 order FL FR FC LFE BL BR SL SR
    print("MKV TrueHD (ffmpeg decode, 7.1 presentation) FC: %d samples, RMS %.2f dBFS"
          % (len(mkv_c), 20 * np.log10(np.sqrt(np.mean(mkv_c ** 2)))))
    lag, rho = xcorr_lag(damf, mkv_c, 48000)
    print("RESULT: MKV centre vs DAMF centre object: lag %+d samples (%+.3f ms), correlation %.4f"
          % (lag, 1000.0 * lag / FS, rho))
    print("        (positive lag = MKV audio is later than DAMF by that much)")


if __name__ == "__main__":
    main()
