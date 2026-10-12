"""ctypes binding for native/build/Release/ear_shim.dll.

ctypes is Python's equivalent of C#'s P/Invoke, so these tests exercise the same C entry
points Unity will call.
"""
import ctypes as C
import os

import numpy as np

_HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_DLL = os.path.join(_HERE, "..", "native", "build", "Release", "ear_shim.dll")

_D = C.c_double
_PD = C.POINTER(C.c_double)


class EarShimError(RuntimeError):
    pass


class Shim:
    def __init__(self, path=DEFAULT_DLL):
        self.lib = lib = C.CDLL(os.path.abspath(path))
        lib.es_version.restype = C.c_char_p
        lib.es_last_error.restype = C.c_char_p
        lib.es_layout_bs2051.restype = C.c_void_p
        lib.es_layout_bs2051.argtypes = [C.c_char_p]
        lib.es_layout_custom.restype = C.c_void_p
        lib.es_layout_custom.argtypes = [C.c_char_p, C.c_int, C.POINTER(C.c_char_p), _PD, _PD,
                                         C.POINTER(C.c_int)]
        lib.es_layout_free.argtypes = [C.c_void_p]
        lib.es_layout_channel_count.argtypes = [C.c_void_p]
        lib.es_layout_channel_name.argtypes = [C.c_void_p, C.c_int, C.c_char_p, C.c_int]
        lib.es_layout_channel_polar.argtypes = [C.c_void_p, C.c_int, _PD, _PD, _PD]
        lib.es_layout_channel_is_lfe.argtypes = [C.c_void_p, C.c_int]
        lib.es_objects_new.restype = C.c_void_p
        lib.es_objects_new.argtypes = [C.c_void_p]
        lib.es_objects_free.argtypes = [C.c_void_p]
        lib.es_objects_gains_polar.argtypes = [C.c_void_p] + [_D] * 7 + [_PD]
        lib.es_objects_gains_cart_via_polar.argtypes = [C.c_void_p] + [_D] * 7 + [_PD]
        lib.es_point_cart_to_polar.argtypes = [_D, _D, _D, _PD, _PD, _PD]
        lib.es_extent_cart_to_polar.argtypes = [_D] * 6 + [_PD] * 6
        lib.es_direct_new.restype = C.c_void_p
        lib.es_direct_new.argtypes = [C.c_void_p]
        lib.es_direct_free.argtypes = [C.c_void_p]
        lib.es_direct_gains.argtypes = [C.c_void_p, C.c_char_p, _D, _D, _D, _D, _PD]
        lib.es_allo_new.restype = C.c_void_p
        lib.es_allo_new.argtypes = [C.c_void_p, _PD]
        lib.es_allo_free.argtypes = [C.c_void_p]
        lib.es_allo_gains.argtypes = [C.c_void_p] + [_D] * 7 + [_PD]

    def version(self):
        return self.lib.es_version().decode()

    def _check(self, rc):
        if rc < 0:
            raise EarShimError(self.lib.es_last_error().decode())
        return rc

    def _check_handle(self, h):
        if not h:
            raise EarShimError(self.lib.es_last_error().decode())
        return h

    def point_cart_to_polar(self, x, y, z):
        az, el, d = _D(), _D(), _D()
        self._check(self.lib.es_point_cart_to_polar(x, y, z, C.byref(az), C.byref(el), C.byref(d)))
        return az.value, el.value, d.value

    def extent_cart_to_polar(self, x, y, z, w, h, dp):
        out = [_D() for _ in range(6)]
        self._check(self.lib.es_extent_cart_to_polar(x, y, z, w, h, dp, *[C.byref(o) for o in out]))
        return tuple(o.value for o in out)


class Layout:
    def __init__(self, shim, handle):
        self.shim = shim
        self.h = handle
        lib = shim.lib
        n = shim._check(lib.es_layout_channel_count(self.h))
        self.names, self.polar, self.is_lfe = [], [], []
        buf = C.create_string_buffer(64)
        for i in range(n):
            shim._check(lib.es_layout_channel_name(self.h, i, buf, 64))
            self.names.append(buf.value.decode())
            az, el, d = _D(), _D(), _D()
            shim._check(lib.es_layout_channel_polar(self.h, i, C.byref(az), C.byref(el), C.byref(d)))
            self.polar.append((az.value, el.value, d.value))
            self.is_lfe.append(shim._check(lib.es_layout_channel_is_lfe(self.h, i)) == 1)

    @classmethod
    def bs2051(cls, shim, name):
        return cls(shim, shim._check_handle(shim.lib.es_layout_bs2051(name.encode())))

    @classmethod
    def custom(cls, shim, name, channels):
        """channels: list of (name, azimuth, elevation, is_lfe)."""
        n = len(channels)
        names = (C.c_char_p * n)(*[c[0].encode() for c in channels])
        az = (C.c_double * n)(*[c[1] for c in channels])
        el = (C.c_double * n)(*[c[2] for c in channels])
        lfe = (C.c_int * n)(*[1 if c[3] else 0 for c in channels])
        return cls(shim, shim._check_handle(shim.lib.es_layout_custom(name.encode(), n, names, az, el, lfe)))

    def __len__(self):
        return len(self.names)

    def free(self):
        if self.h:
            self.shim.lib.es_layout_free(self.h)
            self.h = None


class Objects:
    def __init__(self, layout):
        self.shim = layout.shim
        self.n = len(layout)
        self.h = self.shim._check_handle(self.shim.lib.es_objects_new(layout.h))
        self._out = (C.c_double * self.n)()

    def polar(self, az, el, dist=1.0, width=0.0, height=0.0, depth=0.0, gain=1.0):
        self.shim._check(self.shim.lib.es_objects_gains_polar(self.h, az, el, dist, width, height,
                                                              depth, gain, self._out))
        return np.array(self._out[:])

    def cart_via_polar(self, x, y, z, width=0.0, height=0.0, depth=0.0, gain=1.0):
        self.shim._check(self.shim.lib.es_objects_gains_cart_via_polar(self.h, x, y, z, width, height,
                                                                       depth, gain, self._out))
        return np.array(self._out[:])

    def free(self):
        if self.h:
            self.shim.lib.es_objects_free(self.h)
            self.h = None


class Direct:
    def __init__(self, layout):
        self.shim = layout.shim
        self.n = len(layout)
        self.h = self.shim._check_handle(self.shim.lib.es_direct_new(layout.h))
        self._out = (C.c_double * self.n)()

    def gains(self, label, az, el, dist=1.0, lfe_lowpass_hz=0.0):
        self.shim._check(self.shim.lib.es_direct_gains(self.h, label.encode(), az, el, dist,
                                                       lfe_lowpass_hz, self._out))
        return np.array(self._out[:])


class Allo:
    """Room-relative (allocentric) object gains: the BS.2127 cartesian=true path, ported."""

    def __init__(self, layout, positions):
        """positions: one (x, y, z) per layout channel (ignored for LFE channels)."""
        self.shim = layout.shim
        self.n = len(layout)
        flat = (C.c_double * (3 * self.n))(*[float(v) for p in positions for v in p])
        self.h = self.shim._check_handle(self.shim.lib.es_allo_new(layout.h, flat))
        self._out = (C.c_double * self.n)()

    def gains(self, x, y, z, width=0.0, height=0.0, depth=0.0, gain=1.0):
        self.shim._check(self.shim.lib.es_allo_gains(self.h, x, y, z, width, height, depth, gain, self._out))
        return np.array(self._out[:])

    def free(self):
        if self.h:
            self.shim.lib.es_allo_free(self.h)
            self.h = None
