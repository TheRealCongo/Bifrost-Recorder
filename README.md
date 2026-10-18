# Bifrost Recorder

A small Windows recorder for games and programs: open it, pick the game or program, press Record. Part of the Bifrost apps
(installed and updated by Bifrost Suite), and usable on its own.

- Native C# WinForms exe (C# 5), built with the C# compiler that ships with Windows: run `build.ps1`, nothing to install.
- It records with **libobs**, the engine of OBS Studio: game capture (OBS's own signed hook), window or screen capture, the
  program's sound and the microphone on separate tracks, NVENC / AMF / Quick Sync or x264, written as MP4.
- `build.ps1` downloads OBS Studio 32.2.2's official Windows release once (into `deps\`, checked against its published
  SHA-256) and copies the parts the Recorder uses, unmodified, next to `BifrostRecorder.exe`.
- `build.ps1 -Test` builds and runs the tests (`build\RecorderTests.exe`). The tests refuse to run while a game is open.
- Windows 10 or 11, 64-bit, with the Microsoft Visual C++ Redistributable (x64), which most games install.
- Releases: a `v*` tag (e.g. `v0.2.0`) runs `.github/workflows/release.yml`. It builds, runs the unit tests, makes a GitHub
  release with the program and OBS Studio's matching source, and publishes `BifrostRecorder.zip` to the TFH Armory, where
  Bifrost Suite installs it (secret `ARMORY_RECORDER_PUBLISH_TOKEN`). Run the full `build.ps1 -Test` on a PC with a GPU
  first: the workflow can't run the capture tests.

Bifrost Recorder is a separate program from OBS Studio. It is not made, endorsed or supported by the OBS Project.
`THIRD-PARTY-NOTICES.md` lists every OBS file and library it ships, their licences and where their source is.

## License

Bifrost Recorder is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
License as published by the Free Software Foundation, either version 2 of the License, or (at your option) any later
version. See `LICENSE`.

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied
warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

Copyright (C) 2026 TheRealCongo
