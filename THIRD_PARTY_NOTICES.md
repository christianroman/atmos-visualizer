# Third-party notices

Atmos Visualizer includes or builds on the following software. All of it may be redistributed in
open-source and binary form under the licenses below.

| Component | Used for | License | Where |
|---|---|---|---|
| **libear** — ITU-R BS.2127 core library (EBU, IRT, BBC R&D) | Speaker layouts, bed channels (DirectSpeakers), the angle-based object panner | Apache-2.0 — Copyright 2019 The libear Authors | `external/libear`: a git submodule (unmodified), statically linked into `ear_shim.dll` |
| **EBU ADM Renderer (`ear`) 2.1.0** — the BS.2127 reference implementation | Room-relative ("allocentric") panning, ported to C++ in `native/src/allocentric.*`, because libear lacks it; also used as the reference in `tools/` checks | BSD-3-Clause-Clear — Copyright (c) 2018-2019 EBU ADM Renderer Authors | Ported code in `native/src/allocentric.hpp/.cpp`, which carry the full license text |
| **Eigen** 3.4.0 | Linear algebra inside libear | MPL-2.0 | `external/libear/submodules/eigen` (unmodified; used as headers) |
| **xsimd** 12.1.1 | SIMD inside libear | BSD-3-Clause — Copyright (c) 2016 Johan Mabille, Sylvain Corlay, Wolf Vollprecht and Martin Renou; QuantStack; Serge Guelton | `external/libear/submodules/xsimd` |
| **KISS FFT** | FFT inside libear | BSD-3-Clause — Copyright (c) 2003-2010 Mark Borgerding | `external/libear/submodules/kissfft` |
| **Boost** 1.92.0 (headers: Optional, Variant) | Required by libear | Boost Software License 1.0 | Downloaded by CMake when `ear_shim.dll` is built (headers only, build time) |
| **harletty** 0.9.0 (the `harletty` CLI of harletty-bridge) — open Dolby TrueHD, Dolby Digital Plus Atmos and DTS:X decoder, a fork of [truehdd](https://github.com/truehdd/truehdd) by Rainbaby | Decoding a movie's Atmos track into objects (DAMF); run as a separate program | Apache-2.0 — its TrueHD decoding is truehdd's `truehd` crate, Copyright 2025 Rainbaby | `UnityProject/Assets/StreamingAssets/Tools/harletty/`: `harletty.exe` from the official Windows x64 release, unmodified ([v0.9.0](https://github.com/harletty/harletty-bridge/releases/tag/v0.9.0), `harletty-cli-v0.9.0-windows-x86_64.zip`, SHA-256 `0f37246c131506707143f6b355bde47635bcc37f9e9479e2405b5bc4d7b7abdd`), with the project's `LICENSE` |

harletty.exe also contains the Rust libraries the CLI is built from (87 crates, from its
`Cargo.lock` at v0.9.0). They are under MIT or Apache-2.0, except `unicode-ident` (MIT or
Apache-2.0, plus Unicode-3.0), `subtle` (BSD-3-Clause), `aho-corasick`, `jiff` and `memchr`
(Unlicense or MIT) and `ryu` (Apache-2.0 or BSL-1.0). The harletty-bridge project also builds
GPL-3.0 decoder plug-ins; they are not part of the CLI and are not included.

Not distributed with the app:

- **Unity** — the app is built with Unity 6 (6000.3 LTS); Unity's own runtime ships inside built
  players under Unity's terms.
- **ffmpeg**, **MKVToolNix** — external tools used to prepare input files; users install them.
- **Test media.** Film excerpts, decoded audio and video proxies are copyrighted and are not part
  of this repository. The README's screenshots and its silent demo video show a film playing in
  the app; the film belongs to its rights holders.

"Dolby", "Dolby Atmos", "Dolby Digital Plus" and "TrueHD" are trademarks of Dolby Laboratories;
"DTS" and "DTS:X" are trademarks of DTS, Inc. This project is not affiliated with or endorsed by
either, and it does not implement Dolby's or DTS's renderer.

## BSD-3-Clause-Clear (EBU ADM Renderer)

```
Copyright (c) 2018-2019 EBU ADM Renderer Authors
All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted (subject to the limitations in the disclaimer below) provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.

* Neither the name of the copyright holder nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.

NO EXPRESS OR IMPLIED LICENSES TO ANY PARTY'S PATENT RIGHTS ARE GRANTED BY THIS LICENSE. THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

The Apache-2.0, MPL-2.0 and BSD-3-Clause license texts come with the libear submodule
(`external/libear` and its `submodules/` folders), and the Boost license with the Boost download.
harletty's license is next to harletty.exe.
The app's own code is under the Apache License 2.0 (`LICENSE`).
