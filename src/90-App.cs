using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
namespace SnapWheel
{
    class AppCtx : ApplicationContext
    {
        Settings _settings;
        Store _store;
        WheelManager _wheels;
        NotifyIcon _tray;
        HotkeyForm _hotkey;
        HotkeyForm _carryKey;      // 传递模式的全局热键（轮盘是「不激活」窗口，收不到键盘，只能靠热键）
        WheelForm _wheel;
        // 贴在屏幕上的那些图钉（中键点缩略图产生），退出时一起收掉
        readonly System.Collections.Generic.List<PinForm> _pins = new System.Collections.Generic.List<PinForm>();

        public AppCtx()
        {
            _settings = Settings.Load();
            _wheels = new WheelManager(_settings);
            _wheels.LoadImagesFromDisk();
            _store = _wheels.ActiveStore;

            _hotkey = new HotkeyForm();
            _hotkey.Hotkey += new EventHandler(OnHotkey);

            _wheel = new WheelForm(_wheels, _settings);
            _wheel.SettingsRequested += new EventHandler(OnSettings);
            _wheel.CaptureRequested += new EventHandler(OnHotkey);
            _wheel.AdminHelpRequested += new EventHandler(OnAdminHelp);
            _wheel.PinRequested += new Action<Bitmap, Point>(OnPin);
            _wheel.ExitRequested += new EventHandler(delegate(object o, EventArgs e2) { Application.Exit(); });

            _tray = new NotifyIcon();
            _tray.Icon = Brand.Get();
            _tray.Text = Lang.T("SnapWheel 快照轮环", "SnapWheel");
            _tray.Visible = true;
            Err.Notify = delegate(string msg)             // 出问题时托盘冒个泡，程序继续跑
            {
                if (!_settings.ShowBalloon) return;        // 设置里可以关掉右下角通知
                try { _tray.ShowBalloonTip(4000, Lang.T("SnapWheel 快照轮环遇到一个问题（已记录）", "SnapWheel hit a problem (logged)"), msg, ToolTipIcon.Warning); }
                catch { }
            };
            Lang.Init(string.IsNullOrEmpty(_settings.UiLanguage) ? Lang.Guess() : _settings.UiLanguage);   // 界面语言：没选过就按系统语言，切换后重启生效

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(Lang.T("截图", "Screenshot"), null, new EventHandler(OnHotkey));
            menu.Items.Add(Lang.T("导入图片…", "Import images…"), null, new EventHandler(OnImport));
            menu.Items.Add(Lang.T("新手引导", "Getting started"), null, new EventHandler(OnGuide));
            menu.Items.Add(Lang.T("重播开启动画", "Replay startup animation"), null, new EventHandler(delegate(object o, EventArgs e) { _wheel.StartIntro(); }));
            if (Elev.Is)
                menu.Items.Add(Lang.T("管理员模式说明…（拖拽为什么不动）", "Admin mode… (why dragging does not work)"), null, new EventHandler(OnAdminHelp));
            menu.Items.Add(Lang.T("显示/隐藏轮盘", "Show / hide the ring"), null, new EventHandler(delegate(object o, EventArgs e) { _wheel.ToggleWheel(); }));
            menu.Items.Add(Lang.T("关掉所有贴图", "Close all pinned images"), null, new EventHandler(delegate(object o, EventArgs e) { CloseAllPins(); }));
            menu.Items.Add(Lang.T("取字：识别剪贴板里的图", "OCR the clipboard image"), null, new EventHandler(OnOcrClipboard));
            menu.Items.Add(Lang.T("撤销上一次删除", "Undo last delete"), null, new EventHandler(OnUndoDelete));
            menu.Items.Add(Lang.T("管理 Wheel…", "Manage wheels…"), null, new EventHandler(OnWheels));
            menu.Items.Add(Lang.T("反馈 / 报告问题…", "Feedback / report a problem…"), null, new EventHandler(OnFeedback));
            menu.Items.Add(Lang.T("设置…", "Settings…"), null, new EventHandler(OnSettings));
            menu.Items.Add(Lang.T("传递模式（键盘搬图）", "Carry mode (keyboard)"), null, new EventHandler(delegate(object o, EventArgs e2)
            {
                try { StartCarry(); } catch (Exception ex) { Err.Log("Carry", ex); }
            }));
            menu.Items.Add(Lang.T("检查更新", "Check for updates"), null, new EventHandler(delegate(object o, EventArgs e2)             {                 try { CheckUpdate(true); } catch { }             }));             menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Lang.T("打开项目主页", "Open project page"), null, new EventHandler(delegate(object o, EventArgs e) {
                try { System.Diagnostics.Process.Start("https://github.com/" + AppInfo.Repo); } catch { }
            }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Lang.T("退出", "Exit"), null, new EventHandler(delegate(object o, EventArgs e) { Quit(); }));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += new EventHandler(delegate(object o, EventArgs e) { _wheel.ToggleWheel(); });

            RegisterHotkeyAndNotify();
            RegisterCarryHotkey();

            // 启动后到后台检查有没有新版本（不挡启动；设置里可以关）
            if (_settings.CheckUpdate)
            {
                System.Threading.Thread th = new System.Threading.Thread(new System.Threading.ThreadStart(delegate() { CheckUpdate(false); }));
                th.IsBackground = true;
                th.Start();
            }

            if (Elev.Is && _settings.ShowBalloon)
                try
                {
                    _tray.ShowBalloonTip(6000, Lang.T("SnapWheel 快照轮环以管理员身份运行", "SnapWheel is running as administrator"),
                        Lang.T("Windows 会拦掉管理员进程和桌面/资源管理器之间的拖拽。想在轮盘上拖进拖出图片，请用普通权限运行（托盘右键 → 管理员模式说明）。", "Windows blocks dragging between an elevated process and the desktop / Explorer. To drag images in and out of the ring, run it with normal permissions (tray menu -> admin mode)."),
                        ToolTipIcon.Warning);
                }
                catch { }

            // 取字（OCR）引擎在后台焐热：第一次真用的时候就不用等引擎激活那几百毫秒
            Ocr.WarmUpAsync();

            if (_settings.ShowWheelOnStart)
            {
                // 开机一律把轮盘**展开**（带开启动画）。
                // 收起态是"用完自己收起来"的东西，不该让人一开机只看到屏幕边上一小条 ——
                // 新用户会以为没启动，老用户也得先点一下才看得到内容。
                // 收起功能没动：点关闭键（或长按它）照样能收成把手，自动隐藏也照旧。
                _wheel.ShowWheelWithIntro();
            }
            else if (_settings.CollapseMode)
            {
                _wheel.StartCollapsed();     // 就算开机不显示轮盘，也留个贴边把手，否则没法鼠标叫出来
            }

            // 新功能首次提示：中键贴图这条只在轮盘上冒一句 —— 新功能藏在托盘菜单里没人找得到。
            // （管理员那条让位：拖不动的时候会当场弹说明，不缺这一次。）
            if (!_settings.PinHintDone)
            {
                _settings.PinHintDone = true;
                _settings.Save();
                _wheel.ShowToast(Lang.T("新功能：缩略图上按鼠标中键 = 把图钉在屏幕上", "New: middle-click a thumbnail to pin that image on screen"));
            }
            else if (Elev.Is)
                _wheel.ShowToast(Lang.T("管理员模式：拖拽会被 Windows 拦（托盘右键看说明）", "Administrator mode: Windows blocks dragging (see the tray menu)"));

            // 第一次打开、或者换到没见过的版本：都自动弹一次引导（"看过就不再弹"只对同一版本成立）。
            // 需要自己去托盘里找的引导留不住人，所以升级后也主动亮一次。
            bool firstEver = !_settings.IntroSeen;
            bool newVersion = (_settings.GuideSeenVersion != AppInfo.Version);
            if (firstEver || newVersion)
            {
                _settings.IntroSeen = true;
                _settings.GuideSeenVersion = AppInfo.Version;
                _settings.Save();
                Timer g = new Timer();
                g.Interval = 900;
                g.Tick += new EventHandler(delegate(object o, EventArgs e2)
                {
                    g.Stop(); g.Dispose();
                    try { GuideForm gf = new GuideForm(firstEver); gf.ShowDialog(); } catch { }
                });
                g.Start();
            }
        }

        // 托盘入口：一键提 issue（预填环境信息）+ 复制诊断信息（0.6.0）
        void OnFeedback(object sender, EventArgs e)
        {
            FeedbackForm ff = new FeedbackForm();
            try { ff.ShowDialog(_wheel); } catch { }
            try { ff.Dispose(); } catch { }
        }

        // 弹框/截图期间让轮盘退到后面。要点两个：
        //   ① 用**计数**：多处嵌套（截图时又打开设置）时，谁也不会把对方的状态冲掉；
        //   ② 必须**真的把 TopMost 关掉** —— 只抑制"周期置顶"是不够的：轮盘本来就已经在顶层，
        //      不关掉它照样压在设置窗口/截图浮层上面（用户报的"设置跑到下面、以为没打开"）。
        int _noTop = 0;
        bool _noTopWasOn = false;

        void PushNoTopMost()
        {
            if (_noTop == 0) { try { _noTopWasOn = _wheel.TopMost; _wheel.TopMost = false; } catch { } }
            _noTop++;
        }

        void PopNoTopMost()
        {
            _noTop = Math.Max(0, _noTop - 1);
            if (_noTop == 0) { try { _wheel.TopMost = _noTopWasOn; } catch { } }
        }
        void OnGuide(object sender, EventArgs e)
        {
            try { GuideForm gf = new GuideForm(); gf.ShowDialog(); } catch { }
        }

        // 贴图（图钉）：轮盘中键点了一张缩略图 -> 在这儿开一个 PinForm 钉在屏幕上。
        // at 是鼠标的屏幕坐标，PinForm 自己会以它为中心摆好、并夹进屏幕范围。
        void OnPin(Bitmap img, Point at)
        {
            try
            {
                PinForm p = new PinForm(img, at);
                p.FormClosed += new FormClosedEventHandler(delegate(object o, FormClosedEventArgs e2)
                {
                    try { _pins.Remove(p); } catch { }
                });
                _pins.Add(p);
                p.Show();
                p.BringToFront();
            }
            catch (Exception ex) { Err.Log("Pin", ex); }
        }

        void CloseAllPins()
        {
            // 复制一份再遍历：FormClosed 里会从 _pins 里移除
            PinForm[] arr = _pins.ToArray();
            for (int i = 0; i < arr.Length; i++) { try { arr[i].Close(); } catch { } }
            _pins.Clear();
        }

        // 撤销上一次删除（后悔药）：把刚删掉/刚清空的那些图放回轮盘。
        // 图一直在工作内存里，所以这里是"立刻"生效的，不需要任何目录或索引。
        void OnUndoDelete(object sender, EventArgs e)
        {
            try
            {
                if (!Undo.CanUndo)
                {
                    MessageBox.Show("没有可撤销的删除。\n\n（只记得住本次运行中最近 " + Undo.MaxBatches + Lang.T(" 次删除，退出程序就清空 —— 需要长期保存的图请拖到文件夹里存好。）", " deletions; cleared when the app exits - drag images to a folder to keep them.)"),
                        AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                string desc = Undo.LastDesc;
                string wheel = "";
                int n = Undo.UndoLast(_wheels, out wheel);
                if (n <= 0)
                {
                    MessageBox.Show(Lang.T("没能放回去（原轮盘可能已经被删掉了）。", "Could not put it back (the original wheel may have been deleted)."), AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                _wheel.RefreshWheel();
                _wheel.ShowToast(Lang.T("已放回 ", "Restored ") + n + Lang.T(" 张到「", " item(s) to \"") + wheel + "」" + (string.IsNullOrEmpty(desc) ? "" : "（" + desc + "）"));
            }
            catch (Exception ex) { Err.Log("UndoDelete", ex); }
        }

        // 取字（OCR）：把剪贴板里的图认成文字。懒得截图时最顺手 —— 微信里复制一张图直接取字。
        void OnOcrClipboard(object sender, EventArgs e)
        {
            Bitmap img = null;
            try
            {
                if (Clipboard.ContainsImage()) img = Clipboard.GetImage() as Bitmap;
            }
            catch (Exception ex) { Err.Log("OcrClipboard", ex); }
            if (img == null)
            {
                try
                {
                    MessageBox.Show(Lang.T("剪贴板里没有图片。先复制一张图（或截图），再来点这里。", "No image in the clipboard. Copy one (or take a screenshot) and try again."),
                        AppInfo.Name + " 取字", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch { }
                return;
            }
            string err = null, txt = null;
            Cursor prev = null;
            try { prev = Cursor.Current; Cursor.Current = Cursors.WaitCursor; } catch { }
            try { txt = Ocr.Recognize(img, out err); }
            catch (Exception ex) { err = ex.Message; }
            finally { try { Cursor.Current = prev; } catch { } try { img.Dispose(); } catch { } }

            if (txt == null)
            {
                try { MessageBox.Show(err ?? "识别失败了", AppInfo.Name + " 取字", MessageBoxButtons.OK, MessageBoxIcon.Information); } catch { }
                return;
            }
            try
            {
                using (OcrForm of = new OcrForm(txt)) { of.ShowDialog(); }
            }
            catch (Exception ex) { Err.Log("OcrForm", ex); }
        }

        // 管理员模式说明框：托盘菜单、"拖不动"的那一刻都走这里。
        // 点「以普通权限重启」= 让资源管理器拉起自己（拿到 Medium 完整性级别）然后退出当前实例。
        void OnAdminHelp(object sender, EventArgs e)
        {
            bool restart = false;
            try
            {
                using (AdminForm af = new AdminForm())
                {
                    bool wasTop = _wheel.TopMost;
                    PushNoTopMost();
                    af.TopMost = true;
                    restart = (af.ShowDialog() == DialogResult.OK);
                    PopNoTopMost();
                }
            }
            catch { }

            if (!restart) return;
            if (Elev.RelaunchNormal()) Quit();
            else
            {
                try
                {
                    MessageBox.Show(Lang.T("没能自动重启。请关掉 SnapWheel，再右键 SnapWheel.exe →「以普通权限运行」。", "Could not restart automatically. Close SnapWheel, then right-click SnapWheel.exe and choose \"Run as a normal user\"."),
                        AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch { }
            }
        }

        // 托盘「导入图片…」：不想拖的时候也能从任意位置选图加进当前 wheel
        // 检查 GitHub Releases 有没有新版本：只提示，绝不自动下载/替换
        // 检查更新。
        // manual=true：用户点了托盘菜单 —— 必须有反馈（已是最新 / 发现新版 / 下载 / 失败）。
        // manual=false：启动时的静默检查 —— 只在真有新版时提一句，其余一律不打扰。
        // 检查、下载都在后台线程，界面操作统一切回 UI 线程（Ui()）。
        // 传递模式：把轮盘上"当前这张"用键盘搬到别的窗口去。
        // 流程：取图 → 生成吸附用的小图 → 开假光标窗口 → 用户自己切屏、WASD 移动、Enter 放下。
        void StartCarry()
        {
            Store st = _wheels.ActiveStore;
            int idx = _wheel.CurrentIndex;
            if (st == null || idx < 0 || idx >= st.Items.Count)
            {
                MessageBox.Show(_wheel,
                    Lang.T("轮盘上没有可以传递的图。先截一张，或者用滚轮选一张。",
                           "There is nothing on the ring to carry. Capture something, or pick one with the wheel."),
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            StoreItem item = st.Items[idx];
            Bitmap thumb = MakeCarryThumb(item.Image, 132, 99);
            if (thumb == null) return;

            // 起点：轮盘上那张缩略图大致所在的位置。模拟拖放时要从这里"按下"，
            // 目标程序才会认为图是从轮盘里拖出来的。
            // 起点：那张缩略图在屏幕上的真实位置。模拟拖放要从这里「按下」——
            // 有些程序要求按下点确实落在图上，用窗口中心当起点它们不认。
            Rectangle itemRect = _wheel.ItemScreenRect(idx);
            Point origin = (itemRect.Width > 2)
                ? new Point(itemRect.X, itemRect.Y)                                            // Rectangle.X/Y 存的是中心
                : new Point(_wheel.Left + _wheel.Width / 2, _wheel.Top + _wheel.Height / 2);   // 拿不到就退回中心

            CarryForm cf = new CarryForm(thumb, origin);
            try { cf.ShowDialog(_wheel); }
            finally { try { thumb.Dispose(); } catch { } }
        }

        // 生成"吸附在假光标上"的小图：按比例填满目标框、居中裁切，不变形
        static Bitmap MakeCarryThumb(Bitmap src, int w, int h)
        {
            if (src == null) return null;
            try
            {
                Bitmap dst = new Bitmap(w, h);
                using (Graphics g = Graphics.FromImage(dst))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    float k = Math.Max((float)w / src.Width, (float)h / src.Height);
                    int sw = (int)Math.Min(src.Width, Math.Ceiling(w / k));
                    int sh = (int)Math.Min(src.Height, Math.Ceiling(h / k));
                    int sx = (src.Width - sw) / 2, sy = (src.Height - sh) / 2;
                    g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(sx, sy, sw, sh), GraphicsUnit.Pixel);
                }
                return dst;
            }
            catch { return null; }
        }

        void CheckUpdate(bool manual)
        {
            try
            {
                System.Threading.ThreadPool.QueueUserWorkItem(delegate(object st)
                {
                    Update.Found f = Update.Check();
                    if (f == null)
                    {
                        if (manual) Ui(delegate
                        {
                            MessageBox.Show(_wheel,
                                Lang.T("已经是最新版本（v", "You are up to date (v") + AppInfo.Version + Lang.T("）。", ")."),
                                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        });
                        return;
                    }

                    // 后台静默检查：只提醒，把决定权留给用户
                    if (!manual)
                    {
                        Ui(delegate { BalloonUpdate(f.Version); });
                        return;
                    }

                    // 手动检查：问要不要现在下载
                    bool go = false;
                    Ui(delegate
                    {
                        go = MessageBox.Show(_wheel,
                            Lang.T("发现新版本 v", "New version found: v") + f.Version +
                            Lang.T("（当前 v", " (current v") + AppInfo.Version + Lang.T("）。", ").") + "\\r\\n\\r\\n" +
                            Lang.T("现在下载并安装吗？程序会自动重启一次，你的轮盘和设置都不会丢。",
                                   "Download and install now? The app restarts once; your ring and settings are kept."),
                            AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                    });
                    if (!go) return;

                    string dir = Update.Download(f, null);
                    if (dir == null)
                    {
                        Ui(delegate
                        {
                            MessageBox.Show(_wheel,
                                Lang.T("下载失败。可能是网络问题，也可以右键托盘 →「打开项目主页」手动下载。",
                                       "Download failed. This may be a network issue; you can also use tray > Open project page to download manually."),
                                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        });
                        return;
                    }

                    bool apply = false;
                    Ui(delegate
                    {
                        apply = MessageBox.Show(_wheel,
                            Lang.T("下载完成，现在重启并安装 v", "Downloaded. Restart and install v") + f.Version + Lang.T("？", "?"),
                            AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                    });
                    if (!apply) return;

                    // 启动"更新器模式"的自己，然后把当前进程关掉 —— 由它覆盖文件并重新启动
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath,
                            "--apply-update \"" + dir + "\" " + Process.GetCurrentProcess().Id);
                        psi.UseShellExecute = false;
                        Process.Start(psi);
                    }
                    catch { }
                    Ui(delegate { Application.Exit(); });
                });
            }
            catch { }   // 没网 / 超时 / 被拦，一律静默，绝不打扰使用
        }

        // 把一段界面操作切回 UI 线程执行（后台线程不能直接碰控件）
        void Ui(MethodInvoker a)
        {
            try { _wheel.Invoke(a); } catch { }
        }

        // 有新版时的提示气泡
        void BalloonUpdate(string ver)
        {
            try
            {
                if (!_settings.ShowBalloon) return;
                string msg = Lang.T("有新版本 v", "New version available: v") + ver +
                             Lang.T("。右键托盘图标 →「检查更新」可以下载并安装（程序会重启一次，轮盘和设置都保留）。",
                                    ". Right-click the tray icon and choose \"Check for updates\" to download and install (the app restarts once; your ring and settings are kept).");
                _tray.ShowBalloonTip(9000, Lang.T("SnapWheel 快照轮环 有新版本", "SnapWheel has an update"), msg, ToolTipIcon.Info);
            }
            catch { }
        }

        static bool NewerVersion(string a, string b)
        {
            try
            {
                string[] x = a.Split('.');
                string[] y = b.Split('.');
                for (int i = 0; i < 3; i++)
                {
                    int xi = 0, yi = 0;
                    if (i < x.Length) int.TryParse(x[i], out xi);
                    if (i < y.Length) int.TryParse(y[i], out yi);
                    if (xi != yi) return xi > yi;
                }
            }
            catch { }
            return false;
        }

        void OnImport(object sender, EventArgs e)
        {
            using (System.Windows.Forms.OpenFileDialog d = new System.Windows.Forms.OpenFileDialog())
            {
                d.Title = Lang.T("把图片加入轮盘", "Add images to the ring");
                d.Multiselect = true;
                d.Filter = ImageIO.DialogFilter();
                d.RestoreDirectory = true;
                if (d.ShowDialog() != DialogResult.OK) return;
                List<string> files = ImageIO.Collect(d.FileNames, 50);
                _wheel.ShowWheel();
                _wheel.ImportFiles(files);
            }
        }

        // 传递模式的全局热键：Ctrl+Alt+C。
        // 为什么必须有它：轮盘窗口是用 SWP_NOACTIVATE 显示的（为了不抢走别的程序的焦点），
        // 所以它**收不到键盘输入** —— 想在轮盘上用键盘选图、进传递模式，只能靠全局热键。
        void RegisterCarryHotkey()
        {
            try
            {
                if (_carryKey == null)
                {
                    _carryKey = new HotkeyForm();
                    _carryKey.Hotkey += new EventHandler(delegate(object o, EventArgs e)
                    {
                        try { StartCarry(); } catch (Exception ex) { Err.Log("Carry", ex); }
                    });
                }
                _carryKey.Register(Native.MOD_CONTROL | Native.MOD_ALT, (uint)Keys.C);
            }
            catch { }
        }

        void RegisterHotkeyAndNotify()
        {
            uint m, v;
            bool ok = HotkeyUtil.TryParse(_settings.Hotkey, out m, out v) && _hotkey.Register(m, v);
            if (!ok)
            {
                for (int i = 0; i < HotkeyUtil.Names.Length; i++)
                {
                    string name = HotkeyUtil.Names[i];
                    if (name == _settings.Hotkey) continue;
                    if (HotkeyUtil.TryParse(name, out m, out v) && _hotkey.Register(m, v))
                    { _settings.Hotkey = name; _settings.Save(); ok = true; break; }
                }
            }
            string tip = ok ? (Lang.T("已就绪，热键 ", "Ready, hotkey ") + _settings.Hotkey) : Lang.T("热键注册失败，请在设置里换一个", "Hotkey registration failed - pick another one in settings");
            try
            {
                _tray.Text = Lang.T("SnapWheel 快照轮环 (", "SnapWheel (") + _settings.Hotkey + ")";
                // 热键提示只在"第一次运行"或"注册失败"时弹，平时开机不打扰
                if ((_settings.ShowBalloon && !_settings.IntroSeen) || !ok)
                    _tray.ShowBalloonTip(3000, Lang.T("SnapWheel 快照轮环", "SnapWheel"), tip, ToolTipIcon.Info);
            }
            catch { }
        }

        void OnWheels(object sender, EventArgs e)
        {
            PushNoTopMost();
            WheelsForm f = new WheelsForm(_wheels);
            f.ShowDialog();
            _wheels.ApplySettings();
            PopNoTopMost();
            _wheel.RefreshWheel();
        }

        void OnSettings(object sender, EventArgs e)
        {
            bool wasTop = _wheel.TopMost;
            PushNoTopMost();
            SettingsForm f = new SettingsForm(_settings);
            DialogResult r = f.ShowDialog();
            if (r == DialogResult.OK)
            {
                _wheels.ApplySettings();
                RegisterHotkeyAndNotify();
            RegisterCarryHotkey();
                // 界面侧收尾都在这里：以前这句里还夹着一句 HideWheel()，
                // 结果每次点设置里的确定，轮盘都当场消失（详见 WheelForm.AfterSettingsApplied）
                _wheel.AfterSettingsApplied();
            }
            else
            {
                PopNoTopMost();
            }
        }

        void OnHotkey(object sender, EventArgs e) { CaptureRegion(); }

        void CaptureRegion()
        {
            bool wasExpanded = _wheel.Visible && _wheel.IsExpanded;
            if (wasExpanded && _settings.CollapseMode)
            {
                // 先播收起动画，收完了再弹截图浮层（有过程感，也不挡浮层）
                _wheel.CollapseWheel(true);
                for (int i = 0; i < 90 && !_wheel.IsCollapsed; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(8); }
            }
            else if (_wheel.Visible) _wheel.Hide();   // don't let the topmost wheel sit over the capture overlay

            // 第一次用截图浮层：让工具条旁边亮一次"能标注"的提示（只亮这一次）
            if (!_settings.AnnotHintDone)
            {
                _settings.AnnotHintDone = true;
                _settings.Save();
            }
            Rectangle vs = SystemInformation.VirtualScreen;
            Bitmap shot = new Bitmap(vs.Width, vs.Height);
            try
            {
                using (Graphics g = Graphics.FromImage(shot))
                    g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);
            }
            catch { shot.Dispose(); if (wasExpanded) _wheel.ExpandWheel(); return; }

            OverlayForm ov = new OverlayForm(vs, shot, _settings);
            // 浮层必须是前台：轮盘每 2 秒的周期置顶会把它压下去（用户报的"截图时页面不在最顶层"），
            // 所以先让轮盘退出置顶，截完再恢复。
            // 截图/弹框期间抑制轮盘的周期置顶（见 WheelForm.SuppressTopMost）
            PushNoTopMost();
            try { ov.ShowDialog(); }
            finally { PopNoTopMost(); }
            if (ov.WantLongShot)
            {
                // 0.6.0：在截图浮层里点了「长图」—— 带着他框的那块区域去跑滚动长截图
                Rectangle reg = ov.LongShotRegion;
                try { ov.Dispose(); } catch { }
                RunLongShot(reg, wasExpanded);
                return;
            }
            if (ov.Result != null)
            {
                Store st = _wheels.ActiveStore;
                StoreItem ni = st.Add(ov.Result);
                // 关键：浮层关掉之后重抓一次背景。
                // 之前是拿着"截图浮层还在时抓的"背景去显示玻璃，所以截图完轮盘是暗的，
                // 过一会儿定时刷新才突然变亮 —— 现在这里立刻换新背景（带淡入过渡）。
                try { _wheel.RequestBackdropAsync(); } catch { }
                // 截完播拉出动画（收起态拉出来最自然；原来是展开的就直接显示）
                if (_settings.CollapseMode) _wheel.ExpandWheel(true);   // 截图流程：拉出也快一点
                else _wheel.ShowWheel();
                // MarkNew 必须放在"拉出 / 显示"**之后**：收起态那条路走的是 ExpandWheel → StartIntro，
                // 而 StartIntro 会把 _enterT0 清空、重排成"0.45 + i*0.13 秒"的错峰出场表（开机彩虹扫出用的就是它）。
                // 反过来的话，刚截这张的"现在就滑进来"会被那张错峰表覆盖 —— 要等 1 秒多才动，
                // 而且这期间视口还没跟过去（见 MarkNew），用户看到的就是"动画和位置合不上、突然闪现"。
                _wheel.MarkNew(ni);              // only the brand-new shot plays the slide-in
            }
            else if (wasExpanded)
            {
                _wheel.ExpandWheel(true);        // 取消了截图，也把轮盘拉回来
            }
        }

        // ==================== 滚动长截图（0.6.0） ====================
        // 托盘 / 菜单进来的入口。用户拍板的交互是"他自己滚，程序跟着无缝拼接" ——
        // 所以这里只做三件事：把轮盘让开、抓好第一屏、把 LongShotForm 摆上去。
        // 拼接本身在 58-LongShot.cs（引擎）和 59-LongShotForm.cs（提示条 + 定时抓帧）里。
        // 入口在截图浮层的工具条上（用户要求：长截图从截图页面选，而不是托盘）

        void RunLongShot(Rectangle region, bool wasExpanded)
        {
            // 轮盘在进浮层时已经让开了；这里只负责长图本身
            if (_settings.CollapseMode && _wheel.Visible)
            {
                _wheel.CollapseWheel(true);
                for (int i = 0; i < 90 && !_wheel.IsCollapsed; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(8); }
            }
            else if (_wheel.Visible) _wheel.Hide();

            LongShotForm lf = new LongShotForm(region);
            lf.ShowDialog();
            Bitmap result = lf.Result;
            try { lf.Dispose(); } catch { }



            if (result != null)
            {
                Store st = _wheels.ActiveStore;
                StoreItem ni = st.Add(result);
                try { _wheel.RequestBackdropAsync(); } catch { }
                if (_settings.CollapseMode) _wheel.ExpandWheel(true);
                else _wheel.ShowWheel();
                _wheel.MarkNew(ni);      // 和普通截图一样：刚出的这张要有Lang.T("滑进来", "slides in")的动画
            }
            else if (wasExpanded)
            {
                _wheel.ExpandWheel(true);        // 取消了长截图，也把轮盘拉回来
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
                if (_hotkey != null) { try { Native.UnregisterHotKey(_hotkey.Handle, Native.HOTKEY_ID); } catch { } _hotkey.Dispose(); }
            }
            base.Dispose(disposing);
        }

        void Quit()
        {
            try { CloseAllPins(); } catch { }     // 贴图不是主窗口，不留着它们挡住桌面
            try { _tray.Visible = false; } catch { }
            if (_wheel != null && _wheel.Visible)
            {
                _wheel.HideWheel();                     // play the fade-out first
                Timer t = new Timer();
                t.Interval = 650;
                t.Tick += new EventHandler(delegate(object o, EventArgs e2) { t.Stop(); t.Dispose(); ExitThread(); });
                t.Start();
            }
            else ExitThread();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
            // 更新器模式：由"下载更新"启动的第二个自己。等主进程退出后覆盖文件、再把人重新拉起来。             // 走这条路就完全不碰界面和轮盘，做完就退出。             if (Update.IsApplyMode(args)) { Update.RunApply(args); return; } 
        {
            bool createdNew;
            System.Threading.Mutex mtx = new System.Threading.Mutex(true, "SnapWheel_SingleInstance", out createdNew);
            if (!createdNew)
            {
                // already running: ask the existing instance to show its wheel, then quit quietly
                Native.PostMessage((IntPtr)0xFFFF, WheelForm.ShowMsg, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            try { Native.SetDpiAwarenessBest(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 全局兜底：任何没被接住的异常都只记日志（+偶尔提醒一次），绝不再让「.NET Framework 未处理异常」把程序打死
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += new System.Threading.ThreadExceptionEventHandler(delegate(object o, System.Threading.ThreadExceptionEventArgs ea)
            {
                Err.Log("UI线程", ea.Exception);
            });
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(delegate(object o, UnhandledExceptionEventArgs ea)
            {
                Err.Log("非UI线程", ea.ExceptionObject as Exception);
            });

            Application.Run(new AppCtx());
            GC.KeepAlive(mtx);
        }
    }
}
