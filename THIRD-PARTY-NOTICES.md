# Bifrost Recorder: third-party notices

Bifrost Recorder is free software under the GNU General Public License, version 2 or (at your option) any later version
(`LICENSE`). Copyright (C) 2026 TheRealCongo.

It records with **libobs**, the engine of OBS Studio, and ships files from the official Windows release of
**OBS Studio 32.2.2** unchanged (same bytes, same code signatures). Bifrost Recorder is a separate program: it is not
part of OBS Studio, and it is not made, endorsed or supported by the OBS Project. "OBS" and "OBS Studio" are names of
the OBS Project, used here only to say where these files come from.

The ffmpeg libraries in OBS Studio's release are built with `--enable-gpl --enable-version3`, so they are licensed
under the GNU GPL version 3 or later. Taken together, the files in a Bifrost Recorder package are therefore conveyed
under the terms of the **GNU GPL version 3** (`licenses/GPL-3.0.txt`), which every GPL-2.0-or-later part allows.

## What is in the package

| Files (next to `BifrostRecorder.exe` unless noted) | Project and version | License |
|---|---|---|
| `BifrostRecorder.exe` | Bifrost Recorder | GPL-2.0-or-later |
| `obs.dll`, `libobs-d3d11.dll`, `libobs-winrt.dll`, `obs-nvenc-test.exe`, `obs-amf-test.exe`, `obs-qsv-test.exe`, `obs\obs-plugins\64bit\` (win-capture, win-wasapi, obs-ffmpeg, obs-nvenc, obs-qsv11, obs-x264, obs-outputs), `obs\data\` (with the game capture hook `graphics-hook64.dll`/`graphics-hook32.dll` and its helpers) | OBS Studio 32.2.2 | GPL-2.0-or-later (`licenses/obs-studio/COPYING`) |
| `w32-pthreads.dll` | pthreads-win32, as kept in OBS Studio's source | LGPL-2.1-or-later (`licenses/obs-studio/w32-pthreads/`) |
| inside `obs.dll`: libcaption, libnsgif | as kept in OBS Studio's source | MIT (`licenses/obs-studio/libcaption/`, `licenses/obs-studio/libnsgif/`) |
| inside `obs-outputs.dll`: librtmp | as kept in OBS Studio's source | LGPL-2.1-or-later (`licenses/obs-studio/librtmp/`) |
| inside `obs-ffmpeg.dll`: media-playback | as kept in OBS Studio's source | ISC (`licenses/obs-studio/media-playback/`) |
| `avcodec-62.dll`, `avformat-62.dll`, `avutil-60.dll`, `swscale-9.dll`, `swresample-6.dll`, `avdevice-62.dll`, `avfilter-11.dll` | FFmpeg 8.1.2 (commit 38b88335f99e76ed89ff3c93f877fdefce736c13) | GPL-3.0-or-later as built (`licenses/GPL-3.0.txt`, `licenses/obs-deps/FFmpeg/`) |
| inside the FFmpeg libraries | opus 1.5.2, libogg 1.3.5, libvorbis 1.3.7, libvpx 1.14.1, SVT-AV1 2.3.0, aom 3.13.1, libtheora 1.1.1, LAME 3.100 | BSD-style, BSD-3-Clause-Clear, BSD-2-Clause with patent grants, LGPL-2.0-or-later (LAME): `licenses/obs-deps/<name>/` |
| `libx264-164.dll` | x264 r3106 | GPL-2.0-or-later (`licenses/obs-deps/x264/`) |
| `srt.dll` | SRT 1.5.2 | MPL-2.0 (`licenses/obs-deps/srt/`) |
| `librist.dll` | librist 0.2.7 | BSD-2-Clause (`licenses/obs-deps/librist/`) |
| `zlib.dll` | zlib 1.3.1 | zlib (`licenses/obs-deps/zlib/`) |
| `libcurl.dll` | curl 8.12.1 | curl (`licenses/obs-deps/curl/`) |
| built into some of the OBS files above (OBS Studio's build links them in where it uses them) | mbedtls 3.6.4, jansson 2.14.1, nv-codec-headers 13.0.19.0, AMF 1.5.0, libvpl 2.14.0, simde 0.8.2, uthash 2.3.0, Microsoft Detours 4.0.1, WIL 1.0.240803.1, Vulkan headers 1.3.216.0, zstd 1.5.7 | Apache-2.0 / MIT / BSD / CC0, each in `licenses/obs-deps/<name>/` |

The libraries come from obs-deps release `2026-07-15`, the one OBS Studio 32.2.2 was built with.

## Where the source is

Everything below is the complete corresponding source for the files in a Bifrost Recorder package, available at no
charge.

- **Bifrost Recorder:** https://github.com/TheRealCongo/Bifrost-Recorder, at the tag of the release (`build.ps1`
  builds it, and copies the OBS files listed above out of OBS Studio's release).
- **OBS Studio 32.2.2:** `OBS-Studio-32.2.2-Sources.tar.gz`, attached to every Bifrost Recorder release on GitHub
  that contains OBS Studio 32.2.2, and at https://github.com/obsproject/obs-studio/releases/tag/32.2.2.
- **The libraries above:** https://github.com/obsproject/obs-deps/tree/2026-07-15 holds the build scripts and patches
  used, and each script names its upstream source by address and commit or checksum (for example FFmpeg at
  https://github.com/FFmpeg/FFmpeg, commit 38b88335f99e76ed89ff3c93f877fdefce736c13).

**Written offer.** If any of these addresses stops working, TheRealCongo will provide the complete corresponding
source of any file in a Bifrost Recorder package, on request through
https://github.com/TheRealCongo/Bifrost-Recorder/issues, for at least three years after the last release that
contained it, at no charge beyond the cost of physically performing the transfer.

## Modifications

None of the OBS Studio or obs-deps files is modified. Bifrost Recorder calls libobs through its public C interface
(`src/Recorder/Obs.cs`).
