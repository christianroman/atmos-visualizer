"""Phase 0, experiments 3 and 4: layouts and coordinates.

For each layout this computes object gains three ways:

  libear-polar  libear (C++, via ear_shim.dll): the DAMF Cartesian position is converted to
                polar with BS.2127 section 10, then panned with the polar (VBAP-style) panner.
                This is the only Cartesian route libear offers.
  ref-polar     The Python reference renderer (ear 2.1.0) doing exactly the same thing.
                Used to cross-check libear.
  ref-allo      The Python reference with cartesian=true, which is what BS.2127 specifies for
                Cartesian (room-relative) metadata: the allocentric panner. libear does not
                implement this.

Run: .venv/Scripts/python.exe tools/phase0_gains.py > docs/phase0/gains.txt
"""
import itertools
import sys
import warnings

import numpy as np

from ear_shim import Layout, Objects, Shim

from ear.core import allocentric, bs2051
from ear.core.layout import Channel as PyChannel
from ear.core.layout import Layout as PyLayout
from ear.core.metadata_input import ObjectTypeMetadata
from ear.core.objectbased.conversion import to_polar
from ear.core.objectbased.gain_calc import GainCalc
from ear.fileio.adm.elements import AudioBlockFormatObjects, ObjectCartesianPosition

warnings.simplefilter("ignore")
np.set_printoptions(precision=4, suppress=True)

# ---- custom layouts ------------------------------------------------------------------------
#
# 9.1.6 is built only from speaker positions that already exist in BS.2051 (all of them are in
# 22.2 = 9+10+3): the 4+7+0 positions plus wides M+-060 and top-middles U+-090. Allocentric
# (room-box) positions come from the reference renderer's own 22.2 table.
#
# "Room" is a stand-in for the user's room: the same 9.1 ear level (an ASSUMPTION - the real
# ear-level layout isn't known yet) plus 10 ceiling speakers in 5 rows, left and right.
# Polar: elevation 30 like every BS.2051 upper speaker, azimuths spaced 45..135.
# Allocentric: x = +-1, y = 1, 0.5, 0, -0.5, -1, z = 1.

MID_91 = [  # name, az, el, allocentric x, y, z
    ("M+030", 30, 0, -1, 1, 0), ("M-030", -30, 0, 1, 1, 0), ("M+000", 0, 0, 0, 1, 0),
    ("M+060", 60, 0, -1, 0.414214, 0), ("M-060", -60, 0, 1, 0.414214, 0),
    ("M+090", 90, 0, -1, 0, 0), ("M-090", -90, 0, 1, 0, 0),
    ("M+135", 135, 0, -1, -1, 0), ("M-135", -135, 0, 1, -1, 0),
]
LAYOUT_916 = MID_91 + [
    ("U+045", 45, 30, -1, 1, 1), ("U-045", -45, 30, 1, 1, 1),
    ("U+090", 90, 30, -1, 0, 1), ("U-090", -90, 30, 1, 0, 1),
    ("U+135", 135, 30, -1, -1, 1), ("U-135", -135, 30, 1, -1, 1),
]
ROOM_TOPS = []
for row, (az, y) in enumerate(zip([45, 67.5, 90, 112.5, 135], [1, 0.5, 0, -0.5, -1]), start=1):
    ROOM_TOPS += [("T%dL" % row, az, 30, -1, y, 1), ("T%dR" % row, -az, 30, 1, y, 1)]
LAYOUT_ROOM = MID_91 + ROOM_TOPS
LFE = ("LFE1", 45, -30, -1, 1, -1)

CUSTOM = {"9.1.6": LAYOUT_916 + [LFE], "room-9.1.10": LAYOUT_ROOM + [LFE]}


def py_custom_layout(name, chans):
    return PyLayout(name=name, channels=[
        PyChannel(name=c[0], polar_position=(c[1], c[2], 1.0), is_lfe=(c[0].startswith("LFE")))
        for c in chans])


for name, chans in CUSTOM.items():
    # Give the reference renderer's allocentric table an entry for each custom layout.
    allocentric._allo_positions[name] = {c[0]: [float(c[3]), float(c[4]), float(c[5])] for c in chans}


class Engines:
    """All three gain calculators for one layout."""

    def __init__(self, shim, name):
        if name in CUSTOM:
            chans = CUSTOM[name]
            self.lay = Layout.custom(shim, name, [(c[0], c[1], c[2], c[0].startswith("LFE")) for c in chans])
            self.pylay = py_custom_layout(name, chans)
        else:
            self.lay = Layout.bs2051(shim, name)
            self.pylay = bs2051.get_layout(name)
        assert self.lay.names == self.pylay.channel_names, (self.lay.names, self.pylay.channel_names)
        self.names = self.lay.names
        self.obj = Objects(self.lay)
        self.gc = GainCalc(self.pylay)

    def libear_polar(self, x, y, z, size=0.0):
        return self.obj.cart_via_polar(x, y, z, size, size, size)

    def ref_polar(self, x, y, z, size=0.0):
        bf = AudioBlockFormatObjects(position=ObjectCartesianPosition(X=x, Y=y, Z=z), cartesian=True,
                                     width=size, height=size, depth=size)
        return self.gc.render(ObjectTypeMetadata(block_format=to_polar(bf))).direct

    def ref_allo(self, x, y, z, size=0.0):
        bf = AudioBlockFormatObjects(position=ObjectCartesianPosition(X=x, Y=y, Z=z), cartesian=True,
                                     width=size, height=size, depth=size)
        return self.gc.render(ObjectTypeMetadata(block_format=bf)).direct


def fmt_gains(names, g, floor=1e-4):
    """Non-zero gains as 'name gain (dB)', loudest first."""
    idx = [i for i in np.argsort(-g) if g[i] > floor]
    return ", ".join("%s %.3f (%.1f dB)" % (names[i], g[i], 20 * np.log10(g[i])) for i in idx) or "(silent)"


def section(title):
    print()
    print("=" * 100)
    print(title)
    print("=" * 100)


def main():
    shim = Shim()
    print("ear_shim:", shim.version())
    import importlib.metadata as md
    print("reference: ear", md.version("ear"), "/ numpy", np.__version__)

    layouts = ["0+2+0", "2+5+0", "4+7+0", "9.1.6", "room-9.1.10"]
    eng = {n: Engines(shim, n) for n in layouts}
    print("All layouts constructed in libear and the reference (including both custom ones).")

    # ---- 0. method check on a known answer --------------------------------------------------
    section("0. Method check: libear polar panner at exact BS.2051 speaker angles (4+7+0)")
    e = eng["4+7+0"]
    worst = 0.0
    for i, (n, pol) in enumerate(zip(e.names, e.lay.polar)):
        if n.startswith("LFE"):
            continue
        g = e.obj.polar(pol[0], pol[1])
        expect = np.zeros(len(g))
        expect[i] = 1.0
        worst = max(worst, np.max(np.abs(g - expect)))
        print("  az %6.1f el %5.1f -> %s" % (pol[0], pol[1], fmt_gains(e.names, g)))
    print("  max |gain - expected| over all speakers: %.2e" % worst)

    # ---- 4. coordinates: single-object tests on 7.1.4 ---------------------------------------
    section("4. Coordinates: DAMF [x, y, z] -> BS.2127 section 10 polar, single objects on 4+7+0")
    tests = [("centre", (0, 1, 0)), ("front left", (-1, 1, 0)), ("front right", (1, 1, 0)),
             ("side left", (-1, 0, 0)), ("side right", (1, 0, 0)),
             ("rear left", (-1, -1, 0)), ("rear right", (1, -1, 0)),
             ("ceiling left (RPO)", (-1, 0, 1)), ("ceiling right (RPO)", (1, 0, 1))]
    for label, p in tests:
        az, el, d = shim.point_cart_to_polar(*p)
        print("  %-20s %-14s -> az %7.2f el %5.2f d %.2f" % (label, str(list(p)), az, el, d))
        print("      libear-polar: %s" % fmt_gains(e.names, e.libear_polar(*p)))
        print("      ref-allo:     %s" % fmt_gains(e.names, e.ref_allo(*p)))

    # ---- cross-check libear against the reference ---------------------------------------------
    section("Cross-check: libear-polar vs ref-polar (same algorithm, C++ vs Python)")
    rng = np.random.default_rng(1)
    for n in layouts:
        e = eng[n]
        worst, count = 0.0, 0
        pts = list(itertools.product([-1, -0.5, 0, 0.5, 1], [-1, -0.5, 0, 0.5, 1], [0, 0.5, 1]))
        pts += [tuple(v) for v in rng.uniform([-1, -1, -0.2], [1, 1, 1], size=(300, 3))]
        for p in pts:
            for size in (0.0, 0.3):
                a = e.libear_polar(*p, size=size)
                b = e.ref_polar(*p, size=size)
                worst = max(worst, np.max(np.abs(a - b)))
                count += 1
        print("  %-12s %4d cases (point and size 0.3): max |libear - reference| = %.2e" % (n, count, worst))

    # ---- 3. layouts: sanity of custom layouts --------------------------------------------------
    section("3. Layouts: are the panners sane on the custom layouts?")
    grid = np.linspace(-1, 1, 41)
    for n in layouts:
        e = eng[n]
        for mode in ("libear_polar", "ref_allo"):
            fn = getattr(e, mode)
            pw_min, pw_max, max_jump, silent = 9.0, 0.0, 0.0, 0
            # walls and ceiling of the room box, sampled on a 0.05 grid
            for fixed_axis, fixed_val in [(0, -1), (0, 1), (1, -1), (1, 1), (2, 1)]:
                prev_row = None
                for a in grid:
                    row = []
                    for b in grid:
                        p = [0.0, 0.0, 0.0]
                        free = [ax for ax in range(3) if ax != fixed_axis]
                        p[fixed_axis] = fixed_val
                        p[free[0]] = a
                        p[free[1]] = (b + 1) / 2 if free[1] == 2 else b  # z only 0..1
                        g = fn(*p)
                        pw = float(np.sum(g ** 2))
                        pw_min, pw_max = min(pw_min, pw), max(pw_max, pw)
                        silent += pw < 1e-6
                        if row:
                            max_jump = max(max_jump, float(np.max(np.abs(g - row[-1]))))
                        if prev_row is not None:
                            max_jump = max(max_jump, float(np.max(np.abs(g - prev_row[len(row)]))))
                        row.append(g)
                    prev_row = row
            print("  %-12s %-13s power sum(g^2) in [%.4f, %.4f], silent points %d, "
                  "max gain step between neighbours %.3f" % (n, mode, pw_min, pw_max, silent, max_jump))

    section("3. Each speaker's own position lights only that speaker (custom layouts)")
    for n in ["9.1.6", "room-9.1.10"]:
        e = eng[n]
        chans = [c for c in CUSTOM[n] if not c[0].startswith("LFE")]
        for mode in ("polar", "allo"):
            worst = 0.0
            for c in chans:
                i = e.names.index(c[0])
                g = e.obj.polar(c[1], c[2]) if mode == "polar" else e.ref_allo(c[3], c[4], c[5])
                expect = np.zeros(len(g))
                expect[i] = 1.0
                worst = max(worst, np.max(np.abs(g - expect)))
            print("  %-12s %-6s max |gain - expected| = %.2e" % (n, mode, worst))

    # ---- the acceptance-test positions ---------------------------------------------------------
    section("Mid-ceiling objects (RPO: [-1, 0, 1]) on each layout")
    for n in layouts:
        e = eng[n]
        print("  %s" % n)
        print("      libear-polar: %s" % fmt_gains(e.names, e.libear_polar(-1, 0, 1)))
        print("      ref-allo:     %s" % fmt_gains(e.names, e.ref_allo(-1, 0, 1)))

    section("Ceiling8 positions (left side; x = -1, z = 1) after DEE quantisation")
    for n in ["4+7+0", "9.1.6", "room-9.1.10"]:
        e = eng[n]
        print("  %s" % n)
        for y in [1, 0.32258, -0.32258, -1]:
            print("    y %+.5f libear-polar: %s" % (y, fmt_gains(e.names, e.libear_polar(-1, y, 1))))
            print("    %9s ref-allo:     %s" % ("", fmt_gains(e.names, e.ref_allo(-1, y, 1))))

    section("Size: RPO_s0x0 ceiling-left object [-1, 0, 1], width = height = depth = size")
    for n in ["4+7+0", "9.1.6"]:
        e = eng[n]
        print("  %s" % n)
        for size in [0.0, 0.2, 0.3, 0.5, 1.0]:
            print("    size %.1f libear-polar: %s" % (size, fmt_gains(e.names, e.libear_polar(-1, 0, 1, size))))
            print("    %8s ref-allo:     %s" % ("", fmt_gains(e.names, e.ref_allo(-1, 0, 1, size))))


if __name__ == "__main__":
    sys.exit(main())
