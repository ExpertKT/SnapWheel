# SnapWheel 快照轮环 · “Maybe the best screenshot tool out there”

**English** ・ [中文说明](#中文说明) ・ [⬇️ Download v1.0.0](https://github.com/ExpertKT/SnapWheel/releases/latest)

**Screenshots that never become files.** Capture, and the shot slides into a ring in the corner of your screen — drag it straight into any app when you need it.

![SnapWheel demo](docs/demo.gif)

| | |
|---|---|
| 🎯 **Capture into the corner** | `Ctrl+Shift+S`, drag a region — no save dialog, no window switching, no file hunting |
| ↔️ **Drag out to send** | Drop a thumbnail into WeChat / Word / Explorer / any app, release, done. Copy semantics: the ring keeps a copy |
| 📌 **Pin to screen** | Click the **nail** at the right end of the capture toolbar — the shot is pinned where you framed it **and** goes into the ring |
| 📜 **Scrolling capture** | Frame an area and it scrolls + stitches a long image by itself, stopping when the page ends |
| 🔍 **OCR + translation** | Copy text out of any screenshot, translate it in one click. Works on dark, low-contrast text |
| ↩️ **Undoable** | Undo the last delete (the last 8 are kept). Nothing is written outside `%APPDATA%` |

A single exe · portable · no installer · no registry writes (autostart optional) · no model files bundled · **zero third-party dependencies**.

---

## What it is

SnapWheel is a ring of thumbnails that lives in a screen corner. Screenshots you take slide into it immediately — they stay in memory, not as files. Drag one out into any application (a chat window, a document, a folder) and it is delivered as a real image or file. Drag an image *back* onto the ring to keep it.

Multiple "wheels" are supported: long-press the universal key in the middle of the ring and drag towards one of four directions to create / switch / delete / go back.

## Quick start

1. Download `SnapWheel-v1.0.0-full.zip` from [Releases](../../releases), unpack anywhere.
2. Run `SnapWheel.exe`. It sits in the corner with a small pull-tab.
3. Press `Ctrl+Shift+S`, drag a region, release. The shot lands in the ring.
4. Drag the thumbnail into any app to use it — or click the **nail** on the capture toolbar to pin it on screen instead.

**Two build lines ship from the same source:**

| Build | What it is |
|---|---|
| `SnapWheel.exe` (full) | Includes the "universal key" dial inside the ring |
| `SnapWheel-nokey.exe` | Same app without the universal key — for people who find the dial distracting |

## How to use

| Action | How |
|---|---|
| Capture | `Ctrl+Shift+S`, drag; resize from the corners, rotate with the dial, confirm with double-click / `Enter` |
| **Carry** (keyboard) | `Ctrl+Alt+C` — lift the current thumbnail with a fake cursor, steer it with the **arrow keys** (`Shift` = faster), `Space` to drop it into whatever window you switched to, `[` `]` to switch image, `C` = copy only, `Esc` to cancel (the thumbnail flies back to the ring). **Prefer the arrow keys**: `WASD` also works but types those letters into the target window |
| Annotate | The overlay toolbar has arrow / box / mosaic / text, four colours, `Ctrl+Z` to undo. Annotations are baked into the image |
| OCR | The 字 button on the overlay toolbar copies the text inside your selection; the tray menu can also OCR the clipboard image |
| Zoom preview | Hold still on a thumbnail for ~0.3 s |
| Send it | Drag a thumbnail out to WeChat / a folder / anywhere |
| Keep an image | Drag it from anywhere onto the ring |
| Pin to screen | Click the **nail** at the right end of the capture toolbar. Or middle-click a thumbnail; scroll to zoom, drag to move, double-click / `Esc` to close |
| Browse | Scroll the wheel over the ring |
| Rename | Click the name pill |
| Import | Tray menu → Import images… |
| Scrolling capture | Open the capture overlay, frame the area, click the long-image button on the toolbar. It scrolls and stitches; `Enter` finishes early, `Esc` cancels |

## What's new in 1.0

**1.0 means: from this version on, I'm willing to stand behind the promises above.** This release adds one feature, a round of "it reacts now" polish, and fixes three bugs users reported — none of which had their cause where it looked like it was.

- **📌 Pin straight from the capture.** A nail now sits at the right end of the overlay toolbar: click it and the shot is pinned where you framed it, **and** it still goes into the ring. No more waiting for the wheel to slide out and middle-clicking a thumbnail.
- **The ring reacts now.** Dragging an image out makes its cell flinch and leaves a short trail in the drag direction (the default keeps a copy, so the cell does *not* close up — nothing actually left). A fresh capture glows then cools. A ripple spreads out when an image arrives. Switching wheels flips the name pill. The ring thickens with content, warms up in the morning, dims at night, and casts a soft shadow. Ripple / shadow / time-of-day can each be turned off in Settings → Style.
- **Settings opens 9× faster** (250 ms → 30 ms). The cause was not slow code — it was the **order of two lines** in the constructor, and both orders render *pixel-identical*.
- **No more dropped frames from cross-fading an image into itself.** The periodic screen grab usually returns exactly what we already have (you're reading a document), and the cross-fade was re-blending two full-window bitmaps every frame for nothing.
- **Three reported bugs fixed:** the green drop hint's *disappearance* had no transition (the ring's did), the name pill's **text** didn't scale with its pop animation, and the settings window opened slowly.

Each of these has a paragraph with the root cause and the measured numbers in the [v1.0.0 release notes](https://github.com/ExpertKT/SnapWheel/releases/tag/v1.0.0). Full history in [CHANGELOG.md](CHANGELOG.md).

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

`tools\build.ps1` wraps the whole thing (compile both lines, run the 23 test suites, produce the release zips).

## How it is built

- **Everything is drawn by code.** The ring is an `UpdateLayeredWindow` window painted with GDI+; buttons, rounded corners, neumorphic shading and the toolbar icons are all vector paths. There is not a single image asset in the app.
- **The frosted glass is hand-made** (screen grab → box blur → clipped to the panel shape), because a layered window cannot use the system acrylic — and acrylic would wash a grey rectangle over the whole screen corner.
- Layered rendering with a 64-bit signature decides which cached layer to reuse, so a frame normally costs a few milliseconds.
- Sources are split by responsibility in `src\` (numbered files = reading order); `tests\` holds the offline renderers and the behaviour tests.

## License

MIT — see [LICENSE](LICENSE).

---

## 中文说明

> **截完图还要先保存、再切窗口、再去文件夹里翻出来 —— 其实你只是想把这张图粘进微信。**

**SnapWheel 把截图变成了顺手的一件事**：`Ctrl+Shift+S` 框选，图**不弹保存框、直接滑进屏幕角落的环里**；要用的时候把缩略图**拖进聊天框 / 文件夹**就完事。反过来，从桌面或浏览器把图拖回环上就收着了。

绿色免安装 · 单文件 C# / WinForms · **零第三方依赖** · 一个 370 KB 的 exe，Windows 10 / 11 双击就跑。

![platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078d4?style=flat-square)
![.NET](https://img.shields.io/badge/.NET%20Framework-4.x-512bd4?style=flat-square)
![license](https://img.shields.io/badge/license-MIT-green?style=flat-square)
![version](https://img.shields.io/badge/version-v1.0.0-blue?style=flat-square)
![status](https://img.shields.io/badge/status-stable-brightgreen?style=flat-square)
![size](https://img.shields.io/badge/exe-370%20KB-lightgrey?style=flat-square)
![downloads](https://img.shields.io/github/downloads/ExpertKT/SnapWheel/total?style=flat-square)
![stars](https://img.shields.io/github/stars/ExpertKT/SnapWheel?style=flat-square)

  ![框选 → 图滑进角落的轮环 → 拖进聊天框](docs/demo.gif)
  <br><sub>框选 → 图自动滑进角落的环里 → 拖出去直接用（图还留在环上）</sub>

  <a href="https://github.com/ExpertKT/SnapWheel/releases/latest"><b>⬇️ 下载最新版</b></a>
  &nbsp;·&nbsp; <a href="https://github.com/ExpertKT/SnapWheel/releases/latest">完整版</a>（含万能键，推荐）
  &nbsp;·&nbsp; <a href="https://github.com/ExpertKT/SnapWheel/releases/latest">无万能键版</a>（0.2 线，已定稿）
  <br><sub>解压双击 <code>SnapWheel.exe</code> 即用，不需要安装；两个 zip 在同一个 Release 里</sub>

  ![SnapWheel](docs/wheel.png)

### 它是什么

屏幕角落常驻的一段**四分之一圆环**。截图不弹保存框、不落地成文件，直接变成环上的缩略图；要用的时候从环上拖到微信、文件夹、任何地方；反过来，从桌面或浏览器把图片拖到环带上就能收进来。

支持多个「轮盘」：长按环中间的万能键往四个方向拖，可以新建 / 切换 / 删除 / 返回。

### 能干什么

| | |
|---|---|
| 🎯 **截图进角落** | `Ctrl+Shift+S` 框一块 —— 不弹保存框、不切窗口、不用翻文件夹 |
| ↔️ **拖出去就是发出去** | 缩略图拖进微信 / Word / 资源管理器 / 任何程序，松手就到。**复制语义**：环上留着一份 |
| 📌 **贴到屏幕上**（1.0 新增） | 截图浮层工具条最右边那颗**钉子**：框完点它，图钉在你框的位置，**同时照常进轮环** |
| 📜 **滚动长截图** | 框一块区域，它自己滚、自己拼，翻到底自动停 |
| 🔍 **取字 + 翻译** | 圈住文字就能复制，一键翻成中文 / 英文。暗色小字也认得准 |
| ↩️ **删错了能后悔** | 撤销上一次删除（保留最近 8 次）。除了 `%APPDATA%`，不往任何地方写东西 |

绿色免安装 · 免注册表（开机自启可选）· 不打包任何模型文件 · **零第三方依赖**。

### 这次更新（v1.0.0 正式版）

**1.0 的意思是：从这一版起，上面那些承诺我敢替你担保了。** 这一版加了**一条新功能**、一批「手感」，并修掉三个用户报上来的 bug —— 而这三个的根因**都不在看起来的地方**。

- **📌 截完直接「贴」到屏幕上**：截图浮层工具条最右边多了一颗钉子，框完点它，图立刻钉在你框的那块位置，**并且照常存进轮环**。以前要"截完 → 等轮盘拉出来 → 中键点缩略图"，隔着两步。
- **环会「有反应」了**：拖出去时那一格会颤一下、朝拖的方向留一道拖痕（默认「留一份」，所以格子**不合拢** —— 图并没有走）；新截的那张亮一下再慢慢冷下去；图进来时环上扩散一圈涟漪；切轮盘时名字药丸翻一下（**药丸和上面的字一起**放大缩小）。环还会随内容变粗、早上偏暖深夜变暗、下面多一层影子。涟漪 / 影子 / 时间感都能在「设置 → 风格」里单独关掉。
- **点设置不再卡一下**：`new SettingsForm()` **250ms → 30ms**，快 9 倍。根因不是"哪段代码慢"，而是构造函数里**两行的先后顺序** —— 而两种写法**画出来逐像素一模一样**（13.5 万个采样点零差异），所以任何渲染测试都看不出来。
- **动画不再掉帧**：定时抓屏经常抓到和手上**完全相同**的画面（你在看文档时就是这样），而换底那 0.38 秒里每一帧都在把一张图**淡入到它自己身上**。现在一样就直接换上，一帧都不用重画。
- **修掉三个 bug**：绿提示的**消失**没有过渡（环是有的）、名字药丸的**字**不跟着动画缩放、设置窗口打开慢。

每一条的根因和量到的数字都写在 [v1.0.0 发布说明](https://github.com/ExpertKT/SnapWheel/releases/tag/v1.0.0)里。完整历史见 [CHANGELOG.md](CHANGELOG.md)。

## 版本历史

- 完整更新日志见 **[CHANGELOG.md](CHANGELOG.md)**
- 里程碑版本在 **[Releases](https://github.com/ExpertKT/SnapWheel/releases)** 可直接下载
- 全部 47 个版本的快照（源码 + exe + 图标 + 说明）在 **[`versions/`](versions/)**

两条产品线同源编译：`v0.3.x / v0.4.x` 是完整版（含万能键），`v0.2.x` 是无万能键变体（`/define:NO_KEY`）。

## 许可

[MIT](LICENSE) © 2026 exper7
