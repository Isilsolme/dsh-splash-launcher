# DSH GUI

A one-click Windows launcher for the DeepSeek Harness (`dsh`) Web GUI with a borderless WPF startup animation (stroke-by-stroke "HARNESS" lettering inspired by SPlayer-Next). It starts `dsh web` in the background, preloads the real GUI behind the animation, and fades into it only when the GUI is actually ready. Closing the window stops the service it started.

## Build

```bat
build.cmd
```

Produces `DSH-GUI.exe` using the Windows built-in `csc.exe` (no external toolchain).

## Requirements

- Windows 10/11
- Node.js + global `npm install -g @deepseek-ai/dsh`
- Microsoft Edge or Google Chrome

## Configuration

- `DSH_GUI_WORKSPACE` env var, or a `workspace.txt` next to the exe — working directory for `dsh web` (default `D:\VSCode`)
- `DSH_GUI_PORT` — port (default `3080`)

## Known issues

1. The taskbar briefly shows the Chrome/Edge default icon before the page favicon (black whale) loads; this is a browser-level placeholder and cannot be preset via command line.
2. Startup takes a while (dsh boot + browser cold start + plugin loading). The animation covers the whole wait and only fades out when the GUI is ready.
3. Unsigned launchers that start hidden processes / stop process trees may trigger behavior-based antivirus heuristics; add the folder to your AV exclusions or code-sign the exe.

See the Chinese [README](README.md) for full details. Code is MIT; brand assets derive from DeepSeek Harness (MIT, © 2026 DeepSeek).
