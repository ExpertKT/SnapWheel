# SnapWheel

**Screenshots that never become files. Capture, and the shot slides into a ring in the corner of your screen — drag it straight into any app when you need it.**

![SnapWheel demo](docs/demo.gif)

| | |
|---|---|
| 🎯 **Capture into the corner** | `Ctrl+Shift+S`, drag a region — no save dialog, no window switching, no file hunting |
| ↔️ **Drag out to send** | Drop a thumbnail into WeChat / Word / Explorer / any app, release, done. Copy semantics: the ring keeps a copy |
| 📜 **Scrolling capture** | Frame an area and it scrolls + stitches a long image by itself, stopping when the page ends *(new in 0.6.0)* |
| 🔍 **OCR + translation** | Copy text out of any screenshot, translate it in one click. Works on dark, low-contrast text *(new in 0.6.0)* |
| ↩️ **Undoable** | Undo the last delete (the last 8 are kept). Nothing is written outside `%APPDATA%` |

A single exe · portable · no installer · no registry writes (autostart optional) · no model files bundled · **zero third-party dependencies**.

---

## What it is

SnapWheel is a ring of thumbnails that lives in a screen corner. Screenshots you take slide into it immediately — they stay in memory, not as files. Drag one out into any application (a chat window, a document, a folder) and it is delivered as a real image or file. Drag an image *back* onto the ring to keep it.

Multiple "wheels" are supported: long-press the universal key in the middle of the ring and drag towards one of four directions to create / switch / delete / go back.

## Quick start

1. Download `SnapWheel-v0.6.0-full.zip` from [Releases](../../releases), unpack anywhere.
2. Run `SnapWheel.exe`. It sits in the corner with a small pull-tab.
3. Press `Ctrl+Shift+S`, drag a region, release. The shot lands in the ring.
4. Drag the thumbnail into any app to use it.

**Two build lines ship from the same source:**

| Build | What it is |
|---|---|
| `SnapWheel.exe` (full) | Includes the "universal key" dial inside the ring |
| `SnapWheel-nokey.exe` | Same app without the universal key — for people who find the dial distracting |

## How to use

| Action | How |
|---|---|
| Capture | `Ctrl+Shift+S`, drag; resize from the corners, rotate with the dial, confirm with double-click / `Enter` |
| Annotate | The overlay toolbar has arrow / box / mosaic / text, four colours, `Ctrl+Z` to undo. Annotations are baked into the image |
| OCR | The 字 button on the overlay toolbar copies the text inside your selection; the tray menu can also OCR the clipboard image |
| Zoom preview | Hold still on a thumbnail for ~0.3 s |
| Send it | Drag a thumbnail out to WeChat / a folder / anywhere |
| Keep an image | Drag it from anywhere onto the ring |
| Pin to screen | Middle-click a thumbnail; scroll to zoom, drag to move, double-click / `Esc` to close |
| Browse | Scroll the wheel over the ring |
| Rename | Click the name pill |
| Import | Tray menu → Import images… |
| Scrolling capture | Open the capture overlay, frame the area, click the long-image button on the toolbar. It scrolls and stitches; `Enter` finishes early, `Esc` cancels |

## Requirements

| | |
|---|---|
| OS | Windows 10 / 11 (OCR needs Windows 10+) |
| Runtime | .NET Framework 4.x — already part of Windows, nothing to install |
| OCR language pack | Simplified Chinese / English usually ship by default; otherwise enable the "Optical character recognition" optional feature for that language |
| Size | One exe, no installer, no third-party dependencies. Settings and logs live in `%APPDATA%\SnapWheel` |

## Build from source

The whole thing is plain C# compiled with the .NET Framework compiler that ships with Windows:

```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

# full build (with the universal key)
& $csc /nologo /optimize+ /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe src\*.cs

# no-key build (the only difference is /define:NO_KEY)
& $csc /nologo /optimize+ /define:NO_KEY /target:winexe /win32icon:snapwheel.ico `
  /out:SnapWheel-nokey.exe src\*.cs
```

`tools\build.ps1` wraps the whole thing (compile both lines, run tests, produce the release zips).

## How it is built

- **Everything is drawn by code.** The ring is an `UpdateLayeredWindow` window painted with GDI+; buttons, rounded corners, neumorphic shading and the toolbar icons are all vector paths. There is not a single image asset in the app.
- **The frosted glass is hand-made** (screen grab → box blur → clipped to the panel shape), because a layered window cannot use the system acrylic — and acrylic would wash a grey rectangle over the whole screen corner.
- Layered rendering with a 64-bit signature decides which cached layer to reuse, so a frame normally costs a few milliseconds.
- Sources are split by responsibility in `src\` (numbered files = reading order); `tests\` holds the offline renderers and the behaviour tests.

## License

MIT — see [LICENSE](LICENSE).

中文说明见 [README.md](README.md)。
