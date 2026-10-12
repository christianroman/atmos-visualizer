"""Cross-check the C# renderer (RenderDamf, the engine Unity uses) against an independent
Python implementation built on the BS.2127 reference renderer (ear 2.1.0).

Independent at every step:
  metadata  parsed with ruamel.yaml (C# uses its own MiniYaml)
  audio     decoded by ffmpeg (C# uses CafReader)
  gains     ear's GainCalc / DirectSpeakersPanner (C# uses libear via ear_shim.dll)
  mixing    numpy, float64 (C# uses float32)
Same rules as the C# side: first event applies from sample 0; each later event ramps the gains
linearly from the previous event's gains over rampLength samples.

Objects use BS.2127's room-relative (allocentric, cartesian=true) path by default, or the angle
route (to_polar, then the polar panner) with --polar, matching RenderDamf.

usage: python tools/render_check.py <NAME.atmos> <preset name> <csharp_out.wav> <startSeconds> <durationSeconds> [--polar]
"""
import os
import subprocess
import sys
import warnings

import numpy as np
from ruamel.yaml import YAML

from ear.core import allocentric, bs2051
from ear.core.layout import Channel as PyChannel
from ear.core.layout import Layout as PyLayout
from ear.core.direct_speakers.panner import DirectSpeakersPanner
from ear.core.metadata_input import DirectSpeakersTypeMetadata, ExtraData, ObjectTypeMetadata
from ear.core.objectbased.conversion import to_polar
from ear.core.objectbased.gain_calc import GainCalc
from ear.fileio.adm.elements import (AudioBlockFormatDirectSpeakers, AudioBlockFormatObjects, BoundCoordinate,
                                     DirectSpeakerPolarPosition, Frequency, ObjectCartesianPosition)

warnings.simplefilter("ignore")
FS = 48000
BED = {"LFE": ("LFE1", 45.0, -30.0)}


def load_damf(atmos_path):
    yaml = YAML(typ="safe")
    d = os.path.dirname(os.path.abspath(atmos_path))
    with open(atmos_path, encoding="utf-8") as f:
        head = yaml.load(f)
    pres = head["presentations"][0]
    elements = []  # (id, bed channel name or None)
    for bed in pres.get("bedInstances", []):
        for ch in bed["channels"]:
            elements.append((ch["ID"], ch["channel"]))
    for o in pres.get("objects", []):
        elements.append((o["ID"], None))
    with open(os.path.join(d, pres["metadata"]), encoding="utf-8") as f:
        meta = yaml.load(f)
    state = {eid: dict(active=False, pos=[0, 0, 0], size=0.0, gain=float("-inf"), rampLength=0)
             for eid, _ in elements}
    events = {eid: [] for eid, _ in elements}
    for ev in meta["events"]:
        s = dict(state[ev["ID"]])
        for k in ("active", "pos", "size", "gain", "rampLength"):
            if k in ev:
                s[k] = ev[k]
        s["samplePos"] = ev["samplePos"]
        state[ev["ID"]] = s
        lst = events[ev["ID"]]
        if lst and lst[-1]["samplePos"] == s["samplePos"]:
            lst[-1] = s
        else:
            lst.append(s)
    audio = os.path.join(d, pres["audio"])
    return elements, events, audio


def linear_gain(ev):
    g = ev["gain"]
    if not ev["active"] or (isinstance(g, float) and np.isneginf(g)) or g == "-inf":
        return 0.0
    return 10.0 ** (float(g) / 20.0)


LAYOUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "UnityProject", "Assets",
                          "StreamingAssets", "Layouts")


def load_preset(name):
    """The app's preset YAML -> (reference Layout, allocentric positions). The positions are also
    checked against the reference's own BS.2051 tables, so a typo in a preset file shows up."""
    yaml = YAML(typ="safe")
    for fn in sorted(f for f in os.listdir(LAYOUT_DIR) if f.endswith(".yaml")):   # not Unity's .meta files
        with open(os.path.join(LAYOUT_DIR, fn), encoding="utf-8") as f:
            pr = yaml.load(f)
        if str(pr["name"]) == name:
            break
    else:
        raise SystemExit("no preset named " + name)
    spk = pr["speakers"]
    known = {}
    for lay_name in ("9+10+3", "4+5+0", "2+5+0", "0+5+0"):
        for label, pos in allocentric._allo_positions[lay_name].items():
            known.setdefault(label, pos)
    for sp in spk:
        if sp.get("lfe") or sp.get("note"):
            continue   # the LFE isn't panned; a note marks a position the preset chose itself (e.g. 11.1.8)
        if sp["label"] not in known:
            raise SystemExit("%s: %s (%s) isn't in the reference tables; give it a note saying where its position comes from" % (name, sp["name"], sp["label"]))
        if [float(v) for v in sp["position"]] != [float(v) for v in known[sp["label"]]]:
            raise SystemExit("%s: %s position %s differs from the reference table %s" % (name, sp["name"], sp["position"], known[sp["label"]]))
    if pr.get("bs2051"):
        layout = bs2051.get_layout(pr["bs2051"])
        assert [c.name for c in layout.channels] == [sp["label"] for sp in spk], "speaker order differs from BS.2051"
    else:
        layout = PyLayout(name=name, channels=[PyChannel(name=sp["label"], polar_position=(float(sp["azimuth"]), float(sp["elevation"]), 1.0),
                                                         is_lfe=bool(sp.get("lfe"))) for sp in spk])
    allocentric._allo_positions[layout.name] = {sp["label"]: [float(v) for v in sp["position"]] for sp in spk}
    return layout, [sp["name"] for sp in spk]


def element_gains(layout, gc, dsp, bed, ev):
    lin = linear_gain(ev)
    n = len(layout.channels)
    if lin == 0.0:
        return np.zeros(n)
    if bed is not None:
        label, az, el = BED[bed]
        bf = AudioBlockFormatDirectSpeakers(position=DirectSpeakerPolarPosition(bounded_azimuth=BoundCoordinate(az), bounded_elevation=BoundCoordinate(el)),
                                            speakerLabel=[label])
        tm = DirectSpeakersTypeMetadata(block_format=bf, extra_data=ExtraData(channel_frequency=Frequency(lowPass=120.0)))
        return dsp.handle(tm) * lin
    x, y, z = [float(v) for v in ev["pos"]]
    s = float(ev["size"])
    bf = AudioBlockFormatObjects(position=ObjectCartesianPosition(X=x, Y=y, Z=z), cartesian=True,
                                 width=s, height=s, depth=s, gain=lin)
    if POLAR:
        bf = to_polar(bf)
    return gc.render(ObjectTypeMetadata(block_format=bf)).direct


def gains_over_time(layout, gc, dsp, bed, evs, start, count):
    """Per-sample gains, shape (count, channels), for samples start .. start+count."""
    n = np.arange(start, start + count)
    G = [element_gains(layout, gc, dsp, bed, ev) for ev in evs]
    out = np.zeros((count, len(layout.channels)))
    starts = np.array([ev["samplePos"] for ev in evs])
    idx = np.searchsorted(starts, n, side="right") - 1
    idx[idx < 0] = 0
    for i in np.unique(idx):
        sel = idx == i
        if i == 0:
            out[sel] = G[0]
            continue
        ramp = int(evs[i]["rampLength"])
        if i + 1 < len(evs):
            ramp = min(ramp, int(evs[i + 1]["samplePos"]) - int(evs[i]["samplePos"]))
        t = n[sel] - int(evs[i]["samplePos"])
        p = np.clip(t / ramp, 0.0, 1.0) if ramp > 0 else np.ones(len(t))
        p[t >= ramp] = 1.0
        out[sel] = (1 - p)[:, None] * G[i - 1][None, :] + p[:, None] * G[i][None, :]
    return out


def read_caf_ffmpeg(path, start, count, channels):
    cmd = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-i", path, "-af",
           "atrim=start_sample=%d:end_sample=%d" % (start, start + count),
           "-f", "f64le", "-acodec", "pcm_f64le", "-"]
    raw = subprocess.run(cmd, check=True, capture_output=True).stdout
    return np.frombuffer(raw, dtype="<f8").reshape(-1, channels)


def read_wav_float(path):
    with open(path, "rb") as f:
        data = f.read()
    ch = int.from_bytes(data[22:24], "little")
    assert data[36:40] == b"data"
    return np.frombuffer(data[44:], dtype="<f4").reshape(-1, ch).astype(np.float64)


def db(x):
    return 20 * np.log10(x) if x > 0 else float("-inf")


POLAR = "--polar" in sys.argv


def main():
    args = [a for a in sys.argv if not a.startswith("--")]
    atmos, preset_name, wav, t0, dur = args[1], args[2], args[3], float(args[4]), float(args[5])
    start, count = int(round(t0 * FS)), int(round(dur * FS))
    elements, events, audio = load_damf(atmos)
    layout, names = load_preset(preset_name)
    gc = GainCalc(layout)
    dsp = DirectSpeakersPanner(layout)

    x = read_caf_ffmpeg(audio, start, count, len(elements))
    ref = np.zeros((count, len(layout.channels)))
    for ch, (eid, bed) in enumerate(elements):
        if np.any(x[:, ch]):
            ref += x[:, ch:ch + 1] * gains_over_time(layout, gc, dsp, bed, events[eid], start, count)

    cs = read_wav_float(wav)
    assert cs.shape == ref.shape, (cs.shape, ref.shape)

    print("preset %s (%s objects), samples %d..%d (%.2f s from %.2f s)" % (preset_name, "angle-route" if POLAR else "room-relative",
          start, start + count, dur, t0))
    print("method check (C# output vs itself, then vs itself shifted by 1 sample):")
    for k in range(cs.shape[1]):
        same = cs[:, k] - cs[:, k]
        shifted = cs[1:, k] - cs[:-1, k]
        print("  %-6s self-null %s dB, 1-sample-shift residual %.2f dB" % (
            names[k], db(np.sqrt(np.mean(same ** 2))), db(np.sqrt(np.mean(shifted ** 2)))))
    print("C# render vs independent Python/reference render:")
    for k in range(cs.shape[1]):
        sig = np.sqrt(np.mean(ref[:, k] ** 2))
        res = np.sqrt(np.mean((cs[:, k] - ref[:, k]) ** 2))
        print("  %-6s signal %8.3f dBFS   residual %8.2f dBFS   (%.1f dB below signal)   max |diff| %.2e" % (
            names[k], db(sig), db(res), db(sig) - db(res), np.max(np.abs(cs[:, k] - ref[:, k]))))


if __name__ == "__main__":
    main()
