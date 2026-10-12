"""Verify the C++ port of BS.2127's room-relative (allocentric) panner against the reference.

Compares ear_shim's es_allo_gains (native/src/allocentric.cpp) with the EBU ADM Renderer
(ear 2.1.0) rendering the same object with cartesian=true, over many layouts, positions
(including outside the box, which must be clipped) and sizes.

Method check first: the comparison must report a clear mismatch for a deliberately wrong
answer (the reference gains for a slightly different position).

Run: .venv/Scripts/python.exe tools/allo_check.py > docs/phase1/allo_check.txt
"""
import itertools
import sys
import time
import warnings

import numpy as np

from ear_shim import Allo, Layout, Shim

from ear.core import allocentric, bs2051
from ear.core.layout import Channel as PyChannel
from ear.core.layout import Layout as PyLayout
from ear.core.metadata_input import ObjectTypeMetadata
from ear.core.objectbased.gain_calc import GainCalc
from ear.fileio.adm.elements import AudioBlockFormatObjects, ObjectCartesianPosition

warnings.simplefilter("ignore")

# Custom layouts: (name, az, el, allocentric x, y, z). Positions come from the reference's own
# BS.2051 tables (22.2 has every one of them).
LFE = ("LFE1", 45, -30, -1, 1, -1)
MID_51 = [("M+030", 30, 0, -1, 1, 0), ("M-030", -30, 0, 1, 1, 0), ("M+000", 0, 0, 0, 1, 0),
          ("M+110", 110, 0, -1, -1, 0), ("M-110", -110, 0, 1, -1, 0)]
MID_91 = [("M+030", 30, 0, -1, 1, 0), ("M-030", -30, 0, 1, 1, 0), ("M+000", 0, 0, 0, 1, 0),
          ("M+060", 60, 0, -1, 0.414214, 0), ("M-060", -60, 0, 1, 0.414214, 0),
          ("M+090", 90, 0, -1, 0, 0), ("M-090", -90, 0, 1, 0, 0),
          ("M+135", 135, 0, -1, -1, 0), ("M-135", -135, 0, 1, -1, 0)]
CUSTOM = {
    "5.1.2-top-middle": MID_51 + [("U+090", 90, 30, -1, 0, 1), ("U-090", -90, 30, 1, 0, 1), LFE],
    "9.1.6": MID_91 + [("U+045", 45, 30, -1, 1, 1), ("U-045", -45, 30, 1, 1, 1),
                       ("U+090", 90, 30, -1, 0, 1), ("U-090", -90, 30, 1, 0, 1),
                       ("U+135", 135, 30, -1, -1, 1), ("U-135", -135, 30, 1, -1, 1), LFE],
    "room-9.1.10": MID_91 + [("T%d%s" % (r, side), sgn * az, 30, -sgn, y, 1)
                             for r, (az, y) in enumerate(zip([45, 67.5, 90, 112.5, 135], [1, 0.5, 0, -0.5, -1]), 1)
                             for side, sgn in (("L", 1), ("R", -1))] + [LFE],
}
for name, chans in CUSTOM.items():
    allocentric._allo_positions[name] = {c[0]: [float(c[3]), float(c[4]), float(c[5])] for c in chans}

BS2051 = ["0+2+0", "0+5+0", "2+5+0", "4+5+0", "4+7+0", "9+10+3"]


def setup(shim, name):
    if name in CUSTOM:
        chans = CUSTOM[name]
        lay = Layout.custom(shim, name, [(c[0], c[1], c[2], c[0].startswith("LFE")) for c in chans])
        py = PyLayout(name=name, channels=[PyChannel(name=c[0], polar_position=(c[1], c[2], 1.0),
                                                     is_lfe=c[0].startswith("LFE")) for c in chans])
    else:
        lay = Layout.bs2051(shim, name)
        py = bs2051.get_layout(name)
    table = allocentric._allo_positions[name]
    positions = [table[n] if n in table else [0, 0, 0] for n in lay.names]   # M+SC etc. not in tables
    return lay, Allo(lay, positions), GainCalc(py)


def ref(gc, x, y, z, w, h, d, g):
    bf = AudioBlockFormatObjects(position=ObjectCartesianPosition(X=x, Y=y, Z=z), cartesian=True,
                                 width=w, height=h, depth=d, gain=g)
    return gc.render(ObjectTypeMetadata(block_format=bf)).direct


def main():
    shim = Shim()
    print(shim.version())
    rng = np.random.default_rng(2127)
    overall = 0.0

    # method check: a deliberately wrong answer must stand out
    lay, al, gc = setup(shim, "4+7+0")
    a = al.gains(-1, 0, 1)
    b = ref(gc, -1, 0.05, 1, 0, 0, 0, 1)
    print("method check: port at [-1, 0, 1] vs reference at [-1, 0.05, 1]: max |diff| %.3f (must be large)"
          % np.max(np.abs(a - b)))

    for name in BS2051 + list(CUSTOM):
        lay, al, gc = setup(shim, name)
        speakers = [tuple(allocentric._allo_positions[name][n]) for n in lay.names
                    if n in allocentric._allo_positions[name] and not n.startswith("LFE")]
        grid = list(itertools.product([-1, -0.5, 0, 0.5, 1], [-1, -0.5, 0, 0.5, 1], [-1, 0, 0.5, 1]))
        damf = [(k / 31.0 * 2 - 1, j / 31.0 * 2 - 1, i / 31.0) for k, j, i in rng.integers(0, 32, size=(100, 3))]
        wild = [tuple(v) for v in rng.uniform(-1.3, 1.3, size=(150, 3))]

        t0 = time.time()
        worst_point, n_point = 0.0, 0
        for p in grid + speakers + damf + wild:
            g = float(rng.choice([1.0, 0.5, 0.0316]))
            d = np.max(np.abs(al.gains(*p, gain=g) - ref(gc, *p, 0, 0, 0, g)))
            worst_point, n_point = max(worst_point, d), n_point + 1

        worst_ext, n_ext = 0.0, 0
        sizes = [(s, s, s) for s in (0.05, 0.2, 0.3, 0.5, 1.0, 1.5)] + [(0.1, 0.4, 0.7), (0.6, 0.0, 0.2)]
        for s in sizes:
            for p in [speakers[0], (-1, 0, 1), (0, 0, 0), (0.3, -0.7, 0.4)] + [tuple(v) for v in rng.uniform(-1.1, 1.1, size=(10, 3))]:
                d = np.max(np.abs(al.gains(*p, *s) - ref(gc, *p, *s, 1.0)))
                worst_ext, n_ext = max(worst_ext, d), n_ext + 1
        overall = max(overall, worst_point, worst_ext)
        print("%-17s %2d speakers: point sources %4d cases, max |diff| %.1e;  with size %4d cases, max |diff| %.1e  (%.0f s)"
              % (name, len(speakers), n_point, worst_point, n_ext, worst_ext, time.time() - t0))
    print("OVERALL max |port - reference| = %.2e" % overall)


if __name__ == "__main__":
    sys.exit(main())
