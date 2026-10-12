# Atmos Visualizer

A Windows app that shows what a Dolby Atmos soundtrack does in a home theater. The film plays
on a screen in a 3D room, the Atmos objects move around as dots, and every speaker of the layout
you pick lights up with the level the renderer actually sends it. You hear a stereo render
through your PC speakers.

It renders with **libear**, the C++ core of the EBU's reference renderer for
**ITU-R BS.2127**, using BS.2127's room-relative panning. It is not Dolby's renderer, so a Dolby or
Trinnov processor may place sound somewhat differently.

## What it shows

- **Movies straight from MKV** with a Dolby TrueHD Atmos track (as on UHD Blu-ray). The Atmos
  objects are decoded once and cached; the picture plays live from the original file (GPU
  decoding, HDR10 tone-mapped).
- **Dolby Atmos masters** (`.atmos`, the DAMF format). Open a film first, then a `.atmos` mix of
  it, to see the mix with the film's picture.
- **Eleven speaker layouts**, switchable while playing: 2.0, 5.1, 7.1, 5.1.2 (top middle or front
  height), 5.1.4, 7.1.2, 7.1.4, 9.1.4, 9.1.6, 11.1.8. You can add your own (see below).
- **Speakers** glow with their rendered level. Optionally they show the level in dB, or use
  green-to-red level colors that follow the volume, like a home theater processor's display.
- **Objects** as white dots and **bed channels** (such as the LFE) as fixed purple dots, with a
  seated listener on a couch for scale.
- **Media details**: object and bed counts, format, bitrate, video, renderer.

## Requirements

- Windows 10 or 11, 64-bit. A GPU that decodes 4K HEVC (any recent NVIDIA, AMD or Intel GPU) for
  smooth 4K playback.
- Three free tools for opening movies. The app finds them on `PATH`:
  - **ffmpeg** (ffmpeg and ffprobe): `winget install Gyan.FFmpeg`
  - **MKVToolNix** (mkvextract): `winget install MoritzBunkus.MKVToolNix`
  - **truehdd**, the open TrueHD decoder: download it from
    [its releases](https://github.com/truehdd/truehdd/releases), or build it with Rust
    (`cargo build --release` in a clone of https://github.com/truehdd/truehdd).

If a tool isn't on `PATH`, give its location in
`%USERPROFILE%\AppData\LocalLow\AtmosVisualizer\Atmos Visualizer\settings.yaml`, one per line:

```yaml
truehdd: D:\tools\truehdd.exe
```

Only TrueHD Atmos can be visualized. Dolby Digital Plus Atmos (streaming services, most demo
clips) carries its objects in a form no open decoder can read; the app explains this when you
open such a file.

## Getting the code

The renderer, libear, is a git submodule (with its own submodules), so clone recursively:

```bash
git clone --recursive https://github.com/<account>/atmos-visualizer.git
```

If you already cloned without `--recursive`:

```bash
git submodule update --init --recursive
```

## Building and running

1. Install **Unity 6000.3.26f1** with Unity Hub (Windows build support is included).
2. In Unity Hub choose **Add → Add project from disk** and pick the `UnityProject` folder.
3. Open `Assets/Scenes/Viewer.unity` and press Play to try it in the editor, or build the app with
   the menu **Atmos → Viewer → Build Player**. That writes
   `UnityProject/Builds/Viewer/AtmosVisualizer.exe`; `run-viewer.cmd` starts it.

The native plugin (`ear_shim.dll`: libear plus the room-relative panner) comes prebuilt in
`UnityProject/Assets/Plugins/x86_64`. To rebuild it you need Visual Studio 2022 or later with the
**Desktop development with C++** workload. Then run:

```bash
native\build.cmd
```

It builds the plugin and copies it into the Unity project. The first run downloads the Boost
headers libear needs (about 30 MB, checked against its SHA-256).

## Using it

- **Open…** a movie or a `.atmos`. The welcome card also lists your recent films. Preparing a
  movie the first time takes about a minute per 20 minutes of film; after that it opens at once.
  The decoded audio is cached in `%LOCALAPPDATA%\AtmosVisualizer\Cache` (about 100 MB per minute
  of film); delete that folder to free the space.
- **Speaker layout**: click a card on the right, or press `[` and `]`.
- **Camera**: drag to orbit, scroll to zoom, or use Rotate and Zoom. The view buttons are
  Overview, Seat, Top, Front and Side; click Side again for the other side.
- **Display**: Show (Speakers, Objects, Labels, Couch) and Levels (dB, Colors).
- **Keys**: `Space` plays or pauses, `←` and `→` jump 5 s (30 s with `Shift`), and `Home` goes to
  the start.

### Your own speaker layout

The layouts are plain YAML files in
`UnityProject/Assets/StreamingAssets/Layouts` (in a built app,
`AtmosVisualizer_Data/StreamingAssets/Layouts`). Copy one, change the speaker names and
positions, and restart; the comments at the top of each file explain the fields.

## How it works

```
MKV ── mkvextract ──► TrueHD ── truehdd ──► Atmos objects + metadata (DAMF, cached)
 │                                                    │
 │                                    libear (ITU-R BS.2127), room-relative panning
 │                                                    │
 │                         ┌──────────────────────────┴──────────────────────────┐
 │                  speaker feeds of the chosen layout                     stereo render
 │                  (levels shown in the 3D room)                    (what you hear)
 │
 └── ffmpeg (GPU decoding) ──► the picture, synced to the audio clock
```

| Folder | What's in it |
|---|---|
| `UnityProject/Assets/AtmosEngine` | Plain C#: DAMF parsing, gains over time, audio streaming, movie preparation, video decoding |
| `UnityProject/Assets/Scripts` | The app: controller, UI (UI Toolkit), 3D room, camera |
| `native/` | `ear_shim.dll`: a C API over libear, plus BS.2127's room-relative panner ported to C++ from the EBU ADM Renderer |
| `external/libear` | libear (submodule) |
| `tools/` | Checks against the EBU ADM Renderer (`ear`, Python; see `tools/requirements.txt`) and small C# console tools |

## License

Apache License 2.0; see [LICENSE](LICENSE). Third-party components and their licenses are listed in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

"Dolby", "Dolby Atmos" and "TrueHD" are trademarks of Dolby Laboratories. This project is not
affiliated with or endorsed by Dolby.
