# DSH GUI

[![Awesome DSH Plugin](https://awesome-dsh-plugin.com/badge.svg)](https://awesome-dsh-plugin.com)

A one-click Windows launcher for the DeepSeek Harness (`dsh`) Web GUI with a borderless WPF startup animation (stroke-by-stroke "HARNESS" lettering inspired by SPlayer-Next). It starts `dsh web` in the background, preloads the real GUI behind the animation, and fades into it only when the GUI is actually ready. Closing the window stops the service it started.

## Quick start

### Option A: Download the portable build (recommended, no build required)

1. Download `dsh-splash-launcher-vX.Y.Z.zip` (or just the single-file `DSH-GUI.exe`) from [Releases](https://github.com/Isilsolme/dsh-splash-launcher/releases);
2. Unzip (or drop the exe) anywhere — the exe is self-contained, all splash assets are embedded, **a single file is enough**;
3. Double-click `DSH-GUI.exe`.

> If SmartScreen shows "Windows protected your PC" on first run: click **More info → Run anyway** (the exe is unsigned).

### Option B: Build from source

```bat
build.cmd
```

Produces a self-contained `DSH-GUI.exe` (black-whale icon, assets embedded as resources) using the Windows built-in `csc.exe` (no external toolchain).

## Requirements

- Windows 10/11
- Node.js + global `npm install -g @deepseek-ai/dsh`
- Microsoft Edge or Google Chrome

## Configuration

- `DSH_GUI_WORKSPACE` env var, or a `workspace.txt` next to the exe — working directory for `dsh web` (default: your user profile `%USERPROFILE%`)
- `DSH_GUI_PORT` — port (default `3080`)
- Splash assets are embedded in the exe; drop the same-named files (`splash.html`, `*.svg`, `whale.png`) next to the exe to override them (custom splash, no rebuild needed).

## Known issues

1. The taskbar briefly shows the Chrome/Edge default icon before the page favicon (black whale) loads; this is a browser-level placeholder and cannot be preset via command line.
2. Startup takes a while (dsh boot + browser cold start + plugin loading). The animation covers the whole wait and only fades out when the GUI is ready.
3. Unsigned launchers that start hidden processes / stop process trees may trigger SmartScreen or behavior-based antivirus heuristics; click "More info → Run anyway" on first run, add the folder to your AV exclusions, or code-sign the exe.

See the Chinese [README](README.md) for full details. Code is MIT; brand assets derive from DeepSeek Harness (MIT, © 2026 DeepSeek).
