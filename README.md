# Bifrost Recorder

A small Windows recorder for games and programs: open it, pick the game or program, press Record. Part of the Bifrost apps
(installed and updated by Bifrost Suite), and usable on its own.

- Native C# WinForms exe (C# 5), built with the C# compiler that ships with Windows: run `build.ps1`, nothing to install.
- `build.ps1 -Test` builds and runs the tests (`build\RecorderTests.exe`). The tests refuse to run while a game is open.

## Status

The current engine records with ffmpeg (Windows window capture, GPU encoding). It is moving to libobs, the engine
of OBS Studio, so game capture, the program's sound and the microphone come from the same engine OBS uses. See
`CHANGELOG.md` and `PROGRESS.md`.

## License

Bifrost Recorder is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
License as published by the Free Software Foundation, either version 2 of the License, or (at your option) any later
version. See `LICENSE`.

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied
warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

Copyright (C) 2026 TheRealCongo
