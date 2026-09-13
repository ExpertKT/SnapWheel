using System;
using System.Collections;
using System.Collections.Generic;
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
            _tray.Text = "SnapWheel 快照轮环";
            _tray.Visible = true;
            Err.Notify = delegate(string msg)             // 出问题时托盘冒个泡，程序继续跑
            {
                if (!_settings.ShowBalloon) return;        // 设置里可以关掉右下角通知
                try { _tray.ShowBalloonTip(4000, "SnapWheel 快照轮环遇到一个问题（已记录）", msg, ToolTipIcon.Warning); }
                catch { }
            };
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("截图", null, new EventHandler(OnHotkey));
            menu.Items.Add("导入图片…", null, new EventHandler(OnImport));
            menu.Items.Add("新手引导", null, new EventHandler(OnGuide));
            menu.Items.Add("重播开启动画", null, new EventHandler(delegate(object o, EventArgs e) { _wheel.StartIntro(); }));
            if (Elev.Is)
                menu.Items.Add("管理员模式说明…（拖拽为什么不动）", null, new EventHandler(OnAdminHelp));
            menu.Items.Add("显示/隐藏轮盘", null, new EventHandler(delegate(object o, EventArgs e) { _wheel.ToggleWheel(); }));
            menu.Items.Add("关掉所有贴图", null, new EventHandler(delegate(object o, EventArgs e) { CloseAllPins(); }));
            menu.Items.Add("取字：识别剪贴板里的图", null, new EventHandler(OnOcrClipboard));
            menu.Items.Add("管理 Wheel…", null, new EventHandler(OnWheels));
            menu.Items.Add("设置…", null, new EventHandler(OnSettings));
            menu.Items.Add("打开项目主页", null, new EventHandler(delegate(object o, EventArgs e) {
                try { System.Diagnostics.Process.Start("https://github.com/" + AppInfo.Repo); } catch { }
            }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, new EventHandler(delegate(object o, EventArgs e) { Quit(); }));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += new EventHandler(delegate(object o, EventArgs e) { _wheel.ToggleWheel(); });

            RegisterHotkeyAndNotify();

            // 启动后到后台检查有没有新版本（不挡启动；设置里可以关）
            if (_settings.CheckUpdate)
            {
                System.Threading.Thread th = new System.Threading.Thread(new System.Threading.ThreadStart(CheckUpdate));
                th.IsBackground = true;
                th.Start();
            }

            if (Elev.Is && _settings.ShowBalloon)
                try
                {
                    _tray.ShowBalloonTip(6000, "SnapWheel 快照轮环以管理员身份运行",
                        "Windows 会拦掉管理员进程和桌面/资源管理器之间的拖拽。想在轮盘上拖进拖出图片，请用普通权限运行（托盘右键 → 管理员模式说明）。",
                        ToolTipIcon.Warning);
                }
                catch { }

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
                _wheel.ShowToast("新功能：缩略图上按鼠标中键 = 把图钉在屏幕上");
            }
            else if (Elev.Is)
                _wheel.ShowToast("管理员模式：拖拽会被 Windows 拦（托盘右键看说明）");

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
                    MessageBox.Show("剪贴板里没有图片。先复制一张图（或截图），再来点这里。",
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
                    _wheel.TopMost = false;              // 轮盘别盖在弹框上面
                    af.TopMost = true;
                    restart = (af.ShowDialog() == DialogResult.OK);
                    _wheel.TopMost = wasTop;
                }
            }
            catch { }

            if (!restart) return;
            if (Elev.RelaunchNormal()) Quit();
            else
            {
                try
                {
                    MessageBox.Show("没能自动重启。请关掉 SnapWheel，再右键 SnapWheel.exe →「以普通权限运行」。",
                        AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch { }
            }
        }

        // 托盘「导入图片…」：不想拖的时候也能从任意位置选图加进当前 wheel
        // 检查 GitHub Releases 有没有新版本：只提示，绝不自动下载/替换
        void CheckUpdate()
        {
            try
            {
                // .NET 4.0 默认只开 TLS 1.0，GitHub 会直接拒绝 —— 必须显式开 TLS 1.2
                System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072;
                System.Net.HttpWebRequest req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(
                    "https://api.github.com/repos/" + AppInfo.Repo + "/releases/latest");
                req.UserAgent = AppInfo.Name + "/" + AppInfo.Version;
                req.Timeout = 8000;
                using (System.Net.HttpWebResponse resp = (System.Net.HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                {
                    string body = sr.ReadToEnd();
                    System.Text.RegularExpressions.Match m =
                        System.Text.RegularExpressions.Regex.Match(body, "\"tag_name\"\\s*:\\s*\"v?([0-9.]+)\"");
                    if (!m.Success) return;
                    string remote = m.Groups[1].Value;
                    if (!NewerVersion(remote, AppInfo.Version)) return;
                    string msg = "有新版本 v" + remote + "（当前 v" + AppInfo.Version + "）。右键托盘图标 →「打开项目主页」可以下载。";
                    try
                    {
                        _wheel.BeginInvoke((MethodInvoker)delegate
                        {
                            try { if (_settings.ShowBalloon) _tray.ShowBalloonTip(8000, "SnapWheel 快照轮环 有新版本", msg, ToolTipIcon.Info); } catch { }
                        });
                    }
                    catch { }
                }
            }
            catch { }   // 没网 / 超时 / 被拦，都静默失败，绝不打扰使用
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
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "把图片加入轮盘";
                d.Multiselect = true;
                d.Filter = ImageIO.DialogFilter();
                d.RestoreDirectory = true;
                if (d.ShowDialog() != DialogResult.OK) return;
                List<string> files = ImageIO.Collect(d.FileNames, 50);
                _wheel.ShowWheel();
                _wheel.ImportFiles(files);
            }
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
            string tip = ok ? ("已就绪，热键 " + _settings.Hotkey) : "热键注册失败，请在设置里换一个";
            try
            {
                _tray.Text = "SnapWheel 快照轮环 (" + _settings.Hotkey + ")";
                // 热键提示只在"第一次运行"或"注册失败"时弹，平时开机不打扰
                if ((_settings.ShowBalloon && !_settings.IntroSeen) || !ok)
                    _tray.ShowBalloonTip(3000, "SnapWheel 快照轮环", tip, ToolTipIcon.Info);
            }
            catch { }
        }

        void OnWheels(object sender, EventArgs e)
        {
            _wheel.TopMost = false;
            WheelsForm f = new WheelsForm(_wheels);
            f.ShowDialog();
            _wheels.ApplySettings();
            _wheel.TopMost = _settings.AlwaysOnTop;
            _wheel.RefreshWheel();
        }

        void OnSettings(object sender, EventArgs e)
        {
            bool wasTop = _wheel.TopMost;
            _wheel.TopMost = false;            // don't float above the settings dialog
            SettingsForm f = new SettingsForm(_settings);
            DialogResult r = f.ShowDialog();
            if (r == DialogResult.OK)
            {
                _wheels.ApplySettings();
                RegisterHotkeyAndNotify();
                // 界面侧收尾都在这里：以前这句里还夹着一句 HideWheel()，
                // 结果每次点设置里的确定，轮盘都当场消失（详见 WheelForm.AfterSettingsApplied）
                _wheel.AfterSettingsApplied();
            }
            else
            {
                _wheel.TopMost = wasTop;
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

            Rectangle vs = SystemInformation.VirtualScreen;
            Bitmap shot = new Bitmap(vs.Width, vs.Height);
            try
            {
                using (Graphics g = Graphics.FromImage(shot))
                    g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);
            }
            catch { shot.Dispose(); if (wasExpanded) _wheel.ExpandWheel(); return; }

            // 第一次用截图浮层：让工具条旁边亮一次"能标注"的提示（只亮这一次）
            if (!_settings.AnnotHintDone)
            {
                _settings.AnnotHintDone = true;
                _settings.Save();
            }
            OverlayForm ov = new OverlayForm(vs, shot, _settings);
            ov.ShowDialog();
            if (ov.Result != null)
            {
                Store st = _wheels.ActiveStore;
                StoreItem ni = st.Add(ov.Result);
                _wheel.MarkNew(ni);              // only the brand-new shot plays the slide-in
                // 关键：浮层关掉之后重抓一次背景。
                // 之前是拿着"截图浮层还在时抓的"背景去显示玻璃，所以截图完轮盘是暗的，
                // 过一会儿定时刷新才突然变亮 —— 现在这里立刻换新背景（带淡入过渡）。
                try { _wheel.RequestBackdropAsync(); } catch { }
                // 截完播拉出动画（收起态拉出来最自然；原来是展开的就直接显示）
                if (_settings.CollapseMode) _wheel.ExpandWheel(true);   // 截图流程：拉出也快一点
                else _wheel.ShowWheel();
            }
            else if (wasExpanded)
            {
                _wheel.ExpandWheel(true);        // 取消了截图，也把轮盘拉回来
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
        static void Main()
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
