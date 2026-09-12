# SnapWheel 截图轮盘

> 贴在屏幕角落的截图工具：截完自动滑进环里，拖出去就用，也能把图拖回来收着。

![platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078d4?style=flat-square)
![.NET](https://img.shields.io/badge/.NET%20Framework-4.x-512bd4?style=flat-square)
![license](https://img.shields.io/badge/license-MIT-green?style=flat-square)
![version](https://img.shields.io/badge/version-v0.4.6-blue?style=flat-square)
![status](https://img.shields.io/badge/status-BETA-orange?style=flat-square)
![size](https://img.shields.io/badge/exe-114%20KB-lightgrey?style=flat-square)

<p align="center">
  <img src="docs/wheel.png" width="330" alt="SnapWheel">
</p>

---

## 它是什么

屏幕角落常驻的一段**四分之一圆环**。截图不弹保存框、不落地成文件，直接变成环上的缩略图；要用的时候从环上拖到微信、文件夹、任何地方；反过来，从桌面或浏览器把图片拖到环带上就能收进来。

**单文件 C# 实现，绿色免安装，零第三方依赖** —— 一个 114 KB 的 exe，拷到任何 Windows 10/11 上双击就能跑。

## 为什么用它

| 常规流程 | 用 SnapWheel |
|---|---|
| 截图 → 保存框选路径（或只留在剪贴板）→ 粘贴 → 想存还得再去找文件 | 敲热键框选 → **自动滑进角落的环上** |
| 素材分散在下载夹、桌面、聊天记录里 | 按 **Wheel（项目）** 分栏，各自有名字和颜色 |
| 常用图每次都要去文件管理器翻 | 常年挂在环上，**拖出去即用，拖回来即存** |
| 发图给微信/群里：截图 → 保存 → 切窗口 → 选文件 | 缩略图**直接拖进输入框** |

## 特性

- **截图**：热键（默认 `Ctrl+Shift+S`）→ 拖框选 → 四角缩放 / 拖旋转键转角度 / 宽高输入 / 角度归零 → 双击或回车确认
- **轮盘交互**：滚轮翻图、按住缩略图看大图（倍数可调）、拖出去用、从外部拖回来、拖放时整条环变绿提示
- **收起状态**：不用的时候缩成屏幕边上一个**小把手**（像贴边小球），点它就用彩虹动画把轮盘拉出来；展开时另一条边上有「收起」把手。不想要可以在设置里关掉
- **剪贴板自动收纳**：任何地方复制一张图（截图工具 / 网页右键 / 微信），自动滑进轮盘，不用手动拖
- **毛玻璃不过期**：轮盘挂久了会自动重抓背景，玻璃里始终是当前桌面；而且轮盘**不会出现在你的截图里**
- **多 Wheel**：长按**万能键**弹出四分区圆盘 —— 上=新建 / 右=下一个 / 下=删除 / 左=上一个；删除走「左半绿=取消 / 右半红=确认」
- **常见图片格式全覆盖**：png / jpg / jpeg / bmp / gif / tif / **ico** / cur / webp / heic / avif / jxl / jxr / dds / 相机 RAW…；拖文件夹也行（自动取第一层）
- **智能存盘**：真透明存 PNG，照片存 JPEG(q92)；超过 4096px 自动等比缩小
- **外观可调**：新拟态+毛玻璃 / 纯扁平 / 高对比三档风格，主题色、玻璃不透明度、圆角、阴影、动画速度、标签开关
- **分辨率自适应**：每显示器 DPI 感知 v2，整块轮盘按 DPI 等比缩放（含字体与图标），也可手动指定 80%~250%
- **开启动画**：环像彩虹一样扫出 → 图片沿弧线排队滑落 → 万能键/按钮/文字从屏幕外滑入渐显
- **新手引导**：首次打开自动出现，之后随时可从托盘或设置里叫出来

<p align="center">
  <img src="docs/glass.png" width="300" alt="磨砂玻璃细节">
  &nbsp;&nbsp;
  <img src="docs/menu.png" width="300" alt="万能键圆盘">
</p>

<p align="center">
  <img src="docs/collapsed.png" width="215" alt="收起状态：屏幕边上的小把手">
  &nbsp;&nbsp;
  <img src="docs/collapse-anim.png" width="470" alt="彩虹拉出 / 收起">
</p>

## 快速开始

### 该下哪个？两条产品线

同一份源码用编译开关产出两条线，功能同步推进（当前：v0.4.6 ↔ v0.2.16）：

| 你想要的 | 下这个 | 区别 |
|---|---|---|
| 长按**万能键**切 Wheel / 新建 / 删除 | **v0.4.x 完整版** ⭐ 推荐 | 多一个摇杆圆盘交互 |
| 不想有那个圆盘，只要多 Wheel + 缩放/锁定 | **v0.2.x 无万能键版** | 其余功能完全一样 |

到 [Releases](https://github.com/ExpertKT/SnapWheel/releases) 选版本下载（完整版是 Latest）；
也可以直接下 [`tools\build.ps1 -Package`](#从源码构建) 打出来的两个 zip。

### 从源码构建

不想碰命令行？**双击 `tools\一键编译.bat`** 就行，编两条线 + 跑全部测试 + 打包，一步到位。

```powershell
.\tools\build.ps1                # 一键编译两条线到 build\
.\tools\build.ps1 -Test          # 顺便跑全部测试
.\tools\build.ps1 -Package       # 再打包成分发 zip（含使用说明）
.\tools\build.ps1 -Clean         # 先清空 build\ 再编
```

输出：

```
build\SnapWheel.exe                 完整版（含万能键）
build\SnapWheel-nokey.exe           无万能键版
build\SnapWheel-v0.4.6-full.zip     完整版分发包
build\SnapWheel-v0.2.16-nokey.zip   无万能键版分发包
```

<details>
<summary>不想用脚本？手动编译（两条线就一行参数的区别）</summary>

```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

# 完整版（含万能键）
& $csc /nologo /optimize+ /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe SnapWheel.cs

# 无万能键版（就是多一个 /define:NO_KEY）
& $csc /nologo /optimize+ /define:NO_KEY /target:winexe /win32icon:snapwheel.ico `
  /out:SnapWheel-nokey.exe SnapWheel.cs
```
</details>

## 怎么用

| 操作 | 说明 |
|---|---|
| 截图 | `Ctrl+Shift+S` 拖框选；四角缩放、旋转键转角、双击/回车确认 |
| 存到轮盘 | 截完自动滑入，成为环上的一张缩略图 |
| 看大图 | 缩略图上**按住不动**约 0.3 秒放大预览 |
| 拖出来用 | 从缩略图往外拖到微信 / 文件夹 / 任何地方 |
| 拖回去 | 从桌面或任何地方把图片拖到**环带上**松手 |
| 翻页 | 鼠标放在环上滚滚轮 |
| 换 Wheel | 长按**万能键**（环内侧圆盘）→ 上下左右四个分区 |
| 改名字 | 鼠标停在名字药丸上点一下（最多 12 字） |
| 导入图片 | 托盘右键 → 「导入图片…」（可多选） |
| 显示/隐藏 | 托盘双击，或托盘菜单 |

## 外观与适配

<p align="center">
  <img src="docs/settings.png" width="620" alt="设置">
</p>

| 设置项 | 可选值 |
|---|---|
| 界面风格 | 新拟态 + 毛玻璃 / 纯扁平 / 高对比 |
| 主题色 | 跟随 Wheel 颜色，或 8 色统一 |
| 玻璃不透明度 | 20–100（越低越透，能看到背后的桌面被糊在面板里） |
| 圆角 / 阴影强度 | 0–30% / 0–100 |
| 动画速度 | 慢 / 标准 / 快（统一作用到所有动效） |
| 界面缩放 | 自动（按显示器 DPI）/ 80% ~ 250% |
| 收起状态 | 开 / 关（关掉就是原来的"直接隐藏"，不留把手） |
| 其它 | 剪贴板自动收纳、毛玻璃定时刷新、环半径、缩略图大小、弧上张数、序号字号、长按放大倍数、贴哪个角、热键、删除方式、名称/计数标签开关、开机自启 |

> **关于毛玻璃**：轮盘本体是 `UpdateLayeredWindow` 的分层窗口，用不了系统 acrylic（那会给整个窗口矩形蒙一层灰、把屏幕角落糊成一个方块）。
> 所以是**自己抓屏 + 盒式模糊 + 裁进面板形状**实现的真毛玻璃。抓取时机是「每次显示前 + 隐藏后各一次」，因此轮盘长时间挂着不动时，玻璃里是那一刻的桌面快照。

## 数据放在哪

| 内容 | 位置 |
|---|---|
| 设置 | `%APPDATA%\SnapWheel\settings.ini` |
| Wheel 列表 | `%APPDATA%\SnapWheel\wheels.ini` |
| 出错日志 | `%APPDATA%\SnapWheel\error.log` |
| 截图落盘（可选） | 设置里的「保存目录」，默认 `我的图片\SnapWheel` |

想恢复默认：删掉 `%APPDATA%\SnapWheel` 整个目录，重开即可（等于全新安装）。

## 常见问题

**拖图片进去没反应？**
1. 确认轮盘是**显示状态**（隐藏时没有窗口，接不住）
2. 确认**不是用「以管理员身份运行」**启动的 —— Windows 会拦掉管理员进程与资源管理器之间的拖拽。以管理员启动时托盘会气泡提醒，右键托盘有「以普通权限重启」

**界面太大 / 太小？**
设置 → 外观 → 界面缩放。默认「自动」跟着显示器 DPI 走。

**觉得玻璃太透或不够透？**
设置 → 风格 → 玻璃不透明度，20（很透）~ 100（基本不透明）。

**轮盘不见了，只剩屏幕边上一小条？**
那是**收起状态**（默认开启）。鼠标移到那小条上点一下，轮盘就会用彩虹动画拉出来；展开后另一条屏幕上有个「收起」把手，点一下就收回去。不想要这个模式：设置 → 行为 → 关掉「收起状态」。

**复制了图片没进轮盘？**
检查设置里的「复制图片后自动收进轮盘」是否开着；同一张图不会重复收。若轮盘正处于收起状态，会改用托盘气泡提示。

**快捷键冲突？**
设置 → 快捷键 → 截图热键，内置 5 个候选；注册失败会自动顺延。

## 项目结构

根目录只放一个核心源码文件，其余都收在子目录里：

```
SnapWheel.cs            单文件源码（约 5100 行，全部逻辑都在这）
snapwheel.ico           图标
tools/                  构建与发布工具（不想碰命令行，双击里面的 .bat 即可）
  一键编译.bat            编译两条线 + 跑全部测试 + 打好分发 zip
  发布新版本.bat          改版本号 + 编译 + 归档到 versions\
  build.ps1             上面那个 .bat 实际调用的脚本
  record-version.ps1    版本归档脚本
tests/                  8 套可复跑的测试与工具
  resize-geometry-test.cs   缩放几何仿真（角度 × 比例 × 四角 × 摆位）
  io-test.cs                图片格式解析 / 导入落盘
  render-smoke.cs           绘制状态矩阵 + 风格组合 + DPI 缩放 + 淡出
  drop-test.cs              真实 OLE 拖放 + 处理器级校验
  probe-test.cs             窗口命中测试探针
  uipi-drag-test.cs         权限隔离（UIPI）拖放复现
  ui-shot.cs                把各状态渲染成 PNG，离线看设计效果
  promo-shot.cs             生成宣传图（合成假桌面，不泄露真实屏幕）
docs/                   README 用的界面截图
dist/                  给用户的使用说明
versions/              35 个历史版本快照（源码 + exe + 图标 + 说明）
CHANGELOG.md           完整更新日志（含两条产品线说明）
```

## 开发

' 平时用 `tools\build.ps1 -Test` 就够了；下面是单独跑某一套：

```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

# 缩放几何仿真
& $csc /nologo /out:$env:TEMP\t1.exe tests\resize-geometry-test.cs ; & $env:TEMP\t1.exe

# 图片格式 / 导入
& $csc /nologo /target:exe /main:SnapWheel.IoTest /out:$env:TEMP\t2.exe SnapWheel.cs tests\io-test.cs ; & $env:TEMP\t2.exe

# 绘制 / 风格 / 毛玻璃 / 改名 / 缩放 / 淡出
& $csc /nologo /target:exe /main:SnapWheel.RenderSmoke /out:$env:TEMP\t3.exe SnapWheel.cs tests\render-smoke.cs ; & $env:TEMP\t3.exe

# 拖放（会模拟鼠标真的拖拽，注意别碰电脑）
& $csc /nologo /target:exe /main:SnapWheel.DropTest /out:$env:TEMP\t4.exe SnapWheel.cs tests\drop-test.cs ; & $env:TEMP\t4.exe

# 看设计效果（不弹窗、不动鼠标，输出到 %TEMP%\snapwheel_ui）
& $csc /nologo /target:exe /main:SnapWheel.UiShot /out:$env:TEMP\uishot.exe SnapWheel.cs tests\ui-shot.cs ; & $env:TEMP\uishot.exe
```

## 版本历史

- 完整更新日志见 **[CHANGELOG.md](CHANGELOG.md)**
- 里程碑版本在 **[Releases](https://github.com/ExpertKT/SnapWheel/releases)** 可直接下载
- 全部 35 个版本的快照（源码 + exe + 图标 + 说明）在 **[`versions/`](versions/)**

两条产品线同源编译：`v0.3.x / v0.4.x` 是完整版（含万能键），`v0.2.x` 是无万能键变体（`/define:NO_KEY`）。

## 许可

[MIT](LICENSE) © 2026 exper7
