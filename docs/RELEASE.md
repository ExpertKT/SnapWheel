# 发布约定

> 这份文档存在的理由：**已经漂过好几次了。**
>
> · Release 标题出现过 4 种格式；
> · README 的体积徽章写着 343 KB，实际构建是 346 KB；
> · 仓库简介里写着 `274 KB`，那是好几个版本以前的数，而且**简介在仓库外面，谁都不会顺手去改**。
>
> 同一个事实写在两个地方，它们迟早不一致 —— 这就是反例 #1（度量与绘制同源）。
> 所以下面每一条都尽量做到：**只有一个来源，另一处由程序同步。**

---

## 一、发一版要跑什么

```powershell
# 1. 改版本号（src\00-AppInfo.cs 里完整版那一行）
# 2. 写发布说明（build\release-notes-vX.Y.Z.md）
# 3. 跑：编译 + 测试 + 打包 + 部署到桌面
.\tools\build.ps1 -Test -Package -Deploy

# 4. 提交并推送
git add -A ; git commit -m "发版 vX.Y.Z：一句话干了什么"
git push origin main ; git tag -a vX.Y.Z -m "vX.Y.Z" ; git push origin vX.Y.Z

# 5. 建 Release（标题和正文都用下面规定的格式）
gh release create vX.Y.Z --title "vX.Y.Z — 一句话" --notes-file build\release-notes-vX.Y.Z.md `
  "build\SnapWheel-vX.Y.Z-full.zip" "build\SnapWheel-v0.2.22-nokey.zip"
```

`-Package` 会**自动**把 README 里的版本徽章、体积徽章、正文体积同步成实测值
（`tools\sync-readme-facts.ps1`）。不用手改，也不该手改 —— 手改就是漂移的来源。

---

## 二、Release 标题格式（统一）

```
vX.Y.Z — 一句话说清这一版干了什么
```

- **不要**再写 `SnapWheel 快照轮环 vX.Y.Z` 这种前缀：Release 列表里仓库名本来就显示在上面，
  加前缀只会让标题变长、容易被截断。**历史上 20 个标题已经统一过一次**，别再回去。
- 分隔符统一用 **一个破折号 `—`**（不是 `--`，也不是 `-`）。
- 一句话讲**结果**，不讲过程。例：
  - ✅ `v0.9.5 — 引导整理：那个【新】标记一直是假的`
  - ❌ `v0.9.5 — 修改了 80-Dialogs.cs 并且重构了 GuideForm`

## 三、Release 说明的结构

`build\release-notes-vX.Y.Z.md`，按这个顺序：

```markdown
## 一句话结论（或者一个⚠️提示，如果这版有需要用户注意的事）

正文。**能给出证据的就把证据放上来** —— 实测数字、命令、输出，
比"已修复"三个字有用得多。

---

## 其余改动（如果不止一件事）

---

**完整版** `SnapWheel-vX.Y.Z-full.zip` ｜ **无万能键版** `SnapWheel-v0.2.22-nokey.zip`

绿色免安装，解压双击即可，零第三方依赖。
```

三条规矩：

1. **结尾两行附件清单固定**，用户一眼知道该下哪个。
2. **有需要用户先做一件事的，放在最顶端加 ⚠️**。例：v0.9.4 修的是自动更新本身，
   老版本收不到推送，必须在最前面写「请手动下载」。
3. **写进去的数字必须是量出来的**。写说明时把「右键长按 0.6 秒」写错过的教训还热着 ——
   往用户能看见的地方写数字之前，先去源码里读出来。

---

## 四、仓库简介（About）怎么写

**不要放会过期的数字。**

简介在仓库外面，改 README 的时候不会想起来它，于是它是全项目最容易腐烂的一处：
实测漂到了「274 KB」，而实际早就是 346 KB。

- ✅ `A single portable exe, no installer, zero dependencies, MIT.`
- ❌ `One 346 KB exe, ...` ← 下次构建就错了

体积、版本号这类会变的数字放在 **README 徽章**里（构建自动同步），不要放进简介。

---

## 五、以后加东西时，顺手检查这四样

| 要改的 | 在哪里 | 谁同步 |
|---|---|---|
| 版本号 | `src\00-AppInfo.cs` | 手动（两条产品线各一行） |
| 版本徽章 / 体积徽章 / 正文体积 | `README.md` | **自动**（`build.ps1 -Package`） |
| 发布说明 | `build\release-notes-vX.Y.Z.md` | 手动，格式见上 |
| 更新日志 | `CHANGELOG.md` + README「这次更新」 | 手动 |

**另外**：如果这版**新增了引导条目**，记得在 `80-Dialogs.cs` 的 `AddTip` 里把
`since` 写成**这一版的版本号** —— 只有比用户看过的版本新的条目才会标【新】，
写错了用户就会看到一条"假的新功能"。
（`tests\ui-probe.cs` 里有断言盯着这件事，跑 `-Test` 就能发现。）
