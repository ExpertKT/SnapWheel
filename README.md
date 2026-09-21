# “Maybe the best screenshot tool out there” — SnapWheel：快照轮环

**English** ・ [中文说明](#中文说明)

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
| **Carry** (keyboard) | `Ctrl+Alt+C` — lift the current thumbnail with a fake cursor, steer it with the **arrow keys** (`Shift` = faster), `Space` to drop it into whatever window you switched to, `[` `]` to switch image, `C` = copy only, `Esc` to cancel (the thumbnail flies back to the ring). **Prefer the arrow keys**: `WASD` also works but types those letters into the target window |
| Annotate | The overlay toolbar has arrow / box / mosaic / text, four colours, `Ctrl+Z` to undo. Annotations are baked into the image |
| OCR | The 字 button on the overlay toolbar copies the text inside your selection; the tray menu can also OCR the clipboard image |
| Zoom preview | Hold still on a thumbnail for ~0.3 s |
| Send it | Drag a thumbnail out to WeChat / a folder / anywhere |
| Keep an image | Drag it from anywhere onto the ring |
| Pin to screen | Click the **nail** at the right end of the capture toolbar — the shot is pinned where you framed it **and** goes into the ring. Or middle-click a thumbnail; scroll to zoom, drag to move, double-click / `Esc` to close |
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

---

## 它是什么

屏幕角落常驻的一段**四分之一圆环**。截图不弹保存框、不落地成文件，直接变成环上的缩略图；要用的时候从环上拖到微信、文件夹、任何地方；反过来，从桌面或浏览器把图片拖到环带上就能收进来。

**纯 C# / WinForms 实现（src\ 下按类型分文件），绿色免安装，零第三方依赖** —— 一个 370 KB 的 exe，拷到任何 Windows 10/11 上双击就能跑。

## 这次更新（v1.0.0 正式版）

**1.0 的意思是：从这一版起，上面那些承诺我敢替你担保了。** 这一轮补的是一条**新功能**、一批"手感"、和三个用户报上来但根因都不在表面上的 bug。

### 【新】截完直接「贴」到屏幕上

截图浮层工具条最右边加了一颗**钉子**：框完点它，图立刻钉在你框的那块位置，**并且照常存进轮环**。

以前贴图只能"截完 → 等轮盘拉出来 → 中键点缩略图"，中间隔着两步。现在一步到底。

那颗钉子的底色是**常亮**的 —— 这条栏上别的一律是深底细线条（只有鼠标悬停和当前选中的工具有底色），所以它是整条栏的视觉落点之一，一眼能找到。

> 图钉画了两版：第一版"圆头 + 尖针"，用户说看着像别的东西；改成"扁头 + 直杆 + 尖"的**钉子**才对。
> 中间还有一版头太宽、整体太矮，渲染出来是个字母「T」—— **钉子要比宽高（约 2.6:1）**，这个比例是渲染出来看出来的，不是想出来的。

做这条时踩了两个"看起来对、其实不对"的坑，都留了断言：

| 坑 | 后果 |
|---|---|
| 照抄了旁边「长图」的出口（自己写 `DialogResult=OK; Close()`） | `Result` 是空的 —— 图**既不进轮环也不进剪贴板**，正好把"自动保存到轮环"弄没了。必须走 `Confirm()`（= 用户按了确定） |
| 拿 `ScreenFor()` 的返回值当选区坐标算中心 | 它是**「选区在哪块屏幕上」**（返回那块屏的 Bounds），于是不管框哪儿都钉到**显示器正中央** |

第二个是**测试先红、才发现**的。

### 环会「有反应」了

这一版补的主要是反馈，让每一件事都看得出发生过：

- **拖出去**：那一格会颤一下，并向拖的方向留下一道短促的拖痕（默认「留一份」，所以格子**不合拢** —— 图并没有走）
- **新截的那张**：亮一下再慢慢冷下去，一眼就知道哪张是刚截的
- **图进来**：环上扩散一圈涟漪
- **切轮盘**：名字药丸翻一下 —— 药丸和上面的字**一起**放大缩小
- 环会**随内容变粗**，早上偏暖、深夜自己暗一点，环下面多了一层影子

涟漪 / 影子 / 时间感都能在「设置 → 风格」里单独关掉。

> 药丸那一条用户报得很准："胶囊的动画很不错，但是字不会和动画一起放大缩小"。
> 我上一版只把**药丸的矩形**改大，字还是原字号、只是被重新居中 —— 看着就是"框动字不动"。
> 现在整块内容绕中心一起缩放（一个变换），几何只有一份，不可能再一边动一边不动。
> 检查量的是**字墨迹的包围盒**：静息 189 像素 → 翻到顶 212 像素。

### 点设置不再卡一下：250ms → 30ms

用户报"点设置后窗口出现较慢"。量下来 `new SettingsForm()` 要 **250ms**，而那 250ms 里屏幕上什么都不发生。

一路拆到根因 —— **和"哪段代码慢"无关，是构造函数里两行的先后顺序**：

| 写法 | 耗时 |
|---|---|
| 先 `ShowPage(0)` 建第 1 页、再 `Controls.Add(root)` 挂树（旧，注释还写着"全部建完才挂上去：整棵树只排一次"） | **255ms** |
| 先挂树、再建页 | **29ms** |

**而两种写法画出来逐像素一模一样**（831×653 的窗口，13.5 万个采样点零差异）——
也就是说这个性能回归**任何渲染测试、任何探针都看不出来**，只有用户能感觉到"卡了一下"。
正因为它"看不出来"，专门留了一条会红的断言盯着（门槛 120ms）。

原因：页面挂在窗体上之后，布局和文字测量能走系统已经建好的那套上下文；在**还没挂到窗体**的树上布局，每个控件都要各自去建一次。实测布局耗时随控件数近似平方增长：9 个控件 101ms、17 个 278ms。

### 动画掉帧：底图没变就不做交叉淡入

用户报"动画偶尔掉帧"。从他机器上的帧日志看：**20% 的帧超过 25ms**，平均帧耗时中位数 20.6ms（正好压在预算线上）。

分段计时定位到最大的一项：**换底时每帧要把两张整窗底图混一次，3.78ms**。

而定时刷新每 3.5 秒抓一次屏，抓到的内容**经常和手上那张完全相同**（看文档、看网页、桌面没动的时候）——
于是每一帧都在把一张图**淡入到它自己身上**，还要连累控件层缓存失效、整层重画。用户 10 秒里的 67 帧几乎全是这种白干的帧。

现在换上之前先比一比**两片已经在内存里的位图**（`LockBits` 逐字节，3.5 秒才跑一次，约 1ms）；一样就直接换上、不碰过渡状态 —— 没有任何过渡要播，一帧都不用重画。

> 顺带说一个**试了但实测更慢、已撤**的方向：把混色降到半分辨率。
> 混色本身便宜了，但缩放采样比 1:1 贴图还贵 —— 3.78ms 反而涨到 5.31ms。

### 绿提示的"消失"补上了过渡

用户报："绿提示的出现消失、还有环的随之变绿复原，没有任何过渡"。他描述得很准 —— **出现有过渡、消失没有**。

根因：我把提示内部改成按进度淡入淡出，但**调用处还卡着一个 `_dropActive` 布尔**。出现时它是 true，内部还有机会淡入；消失时它已经翻了 false，**调用处直接跳过** → 硬切。环那边读的是进度、不受影响，所以他看到的是"环有过渡、提示没有"。

---

**1.0.0 的完整清单**：一条新功能（浮层贴图）、七项手感反馈、设置窗口快 9 倍、掉帧白干帧清零，以及三个用户报的 bug（绿提示消失硬切、药丸字不跟缩放、设置打开慢）。

## 版本历史

- 完整更新日志见 **[CHANGELOG.md](CHANGELOG.md)**
- 里程碑版本在 **[Releases](https://github.com/ExpertKT/SnapWheel/releases)** 可直接下载
- 全部 47 个版本的快照（源码 + exe + 图标 + 说明）在 **[`versions/`](versions/)**

两条产品线同源编译：`v0.3.x / v0.4.x` 是完整版（含万能键），`v0.2.x` 是无万能键变体（`/define:NO_KEY`）。

## 许可

[MIT](LICENSE) © 2026 exper7
