using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SnapWheel
{
    // ==================== 诊断信息（0.6.0 反馈闭环） ====================
    // 用户提 issue 时最怕"说不清环境"：版本、系统、DPI、设置组合一多，作者就要来回问。
    // 这里把该问的东西一次收齐，分成两档：
    //   · Brief() —— 短，塞进 GitHub issue 的 URL 里（URL 有长度上限，日志不能放）；
    //   · Full()  —— 长，带错误日志尾部，给"复制诊断信息"按钮用（用户自己找地方贴）。
    static class Diag
    {
        static string Bits() { try { return Environment.Is64BitOperatingSystem ? " 64 位" : " 32 位"; } catch { return ""; } }

        // 系统版本**必须读注册表**：这个程序没有声明"支持 Windows 10"，于是
        // Environment.OSVersion 会一直报 6.2（= Windows 8）—— 用户提 issue 时这一栏就是错的。
        // 另外 Win11 在注册表里常常仍写着 "Windows 10"，要按 build 号纠正。
        static string Os()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k != null)
                    {
                        string name = k.GetValue("ProductName") as string;
                        string disp = k.GetValue("DisplayVersion") as string;
                        string build = k.GetValue("CurrentBuildNumber") as string;
                        string ubr = k.GetValue("UBR") as string;
                        int b = 0; if (!string.IsNullOrEmpty(build)) int.TryParse(build, out b);
                        if (b >= 22000 && !string.IsNullOrEmpty(name) && name.IndexOf("Windows 10") >= 0)
                            name = name.Replace("Windows 10", "Windows 11");
                        if (!string.IsNullOrEmpty(name))
                            return name + (string.IsNullOrEmpty(disp) ? "" : " " + disp)
                                 + " (build " + build + (string.IsNullOrEmpty(ubr) ? "" : "." + ubr) + ")" + Bits();
                    }
                }
            }
            catch { }
            return Environment.OSVersion.VersionString + Bits();
        }

        static string Screen()
        {
            try
            {
                Rectangle vs = SystemInformation.VirtualScreen;
                return vs.Width + "x" + vs.Height + "（虚拟屏）";
            }
            catch { return "未知"; }
        }

        static string SettingsBrief()
        {
            try
            {
                Settings s = Settings.Load();
                if (s == null) return "（读不到设置）";
                StringBuilder b = new StringBuilder();
                b.Append("风格 ").Append(s.UiStyle);
                b.Append(" / 界面缩放 ").Append(s.UiScale == 0 ? "自动" : s.UiScale + "%");
                b.Append(" / 热键 ").Append(s.Hotkey);
                b.Append(" / 收起态 ").Append(s.CollapseMode ? "开" : "关");
                b.Append(" / 玻璃定时刷新 ").Append(s.GlassRefresh ? "开" : "关");
                b.Append(" / 省电 ").Append(s.PowerSave ? "开" : "关");
                b.Append(" / 最多 ").Append(s.MaxCount).Append(" 张");
                b.Append(" / 贴角 ").Append(s.Corner);
                return b.ToString();
            }
            catch { return "（读不到设置）"; }
        }

        public static string Brief()
        {
            StringBuilder b = new StringBuilder();
            b.Append("版本：v").Append(AppInfo.Version).Append("（").Append(AppInfo.Name).Append(" ").Append(AppInfo.CnName).Append("）\r\n");
            b.Append("系统：").Append(Os()).Append("\r\n");
            double dpi = 1.0;
            try { dpi = Native.DpiScaleOf(IntPtr.Zero); } catch { }
            b.Append("DPI：").Append((int)Math.Round(dpi * 100)).Append("%\r\n");
            b.Append("屏幕：").Append(Screen()).Append("\r\n");
            b.Append("权限：").Append(Elev.Is ? "管理员" : "普通用户").Append("\r\n");
            b.Append("设置：").Append(SettingsBrief()).Append("\r\n");
            return b.ToString();
        }

        // 带日志里那台机器最近发生了什么（只取尾部若干行，别把整份日志贴出去）
        public static string Full()
        {
            StringBuilder b = new StringBuilder();
            b.Append(Brief());
            try
            {
                string f = Err.LogPath();
                if (!string.IsNullOrEmpty(f) && File.Exists(f))
                {
                    string[] all = File.ReadAllLines(f);
                    int take = Math.Min(20, all.Length);
                    b.Append("\r\n最近日志（最后 ").Append(take).Append(" 行）：\r\n");
                    for (int i = all.Length - take; i < all.Length; i++) b.Append(all[i]).Append("\r\n");
                }
            }
            catch { }
            return b.ToString();
        }
    }

    // 反馈窗口：一键提 issue（预填好）+ 复制诊断信息。
    // ⚠️ 刻意**不内置任何 token**：客户端直传 issue 需要服务端中转，而"打开预填好的新建页"
    //    不需要任何凭据、也不碰用户的账号 —— 点一下浏览器打开、他自己点提交就行。
    // ⚠️ 国内网络打不开 github.com 是常态：所以文案里明说、并给"复制诊断信息"这条兜底路，
    //    跳转失败也要当场说清原因，绝不静默失败。
    class FeedbackForm : Form
    {
        TextBox _box;
        Label _state;

        public FeedbackForm()
        {
            Text = AppInfo.Name + " 反馈";
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(252, 252, 254);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;

            Rectangle wa = GuideForm.WorkArea();
            int winW = Ui.S(600);
            int maxW = (int)(wa.Width * 0.92) - Ui.S(16);
            if (winW > maxW) winW = Math.Max(Ui.S(360), maxW);
            int mL = Ui.S(26), mR = Ui.S(26);
            int contentW = winW - mL - mR;
            SuspendLayout();

            int y = Ui.S(22);

            Label head = new Label();
            head.Text = "遇到问题了？";
            head.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(28, 30, 36);
            head.Location = new Point(mL, y);
            Ui.Wrap(head, contentW);
            Controls.Add(head);
            y += head.PreferredSize.Height + Ui.S(8);

            Label sub = new Label();
            sub.Text = "点下面的按钮会在浏览器里打开 GitHub 的「新建 issue」页面，标题和环境信息已经帮你填好了，你只要补一句现象、点提交。\r\n"
                     + "⚠️ 国内网络经常打不开 github.com —— 打不开是正常的，不是程序坏了：把下面那段诊断信息复制出来，"
                     + "直接发给作者（QQ / 微信 / 邮件都行）效果完全一样。";
            sub.ForeColor = Color.FromArgb(110, 114, 126);
            sub.Location = new Point(mL, y);
            Ui.Wrap(sub, contentW);
            Controls.Add(sub);
            y += sub.PreferredSize.Height + Ui.S(12);

            _box = new TextBox();
            _box.Multiline = true;
            _box.ReadOnly = true;
            _box.ScrollBars = ScrollBars.Vertical;
            _box.BackColor = Color.FromArgb(244, 246, 250);
            _box.BorderStyle = BorderStyle.FixedSingle;
            _box.Font = new Font("Consolas", 9f);
            _box.Text = Diag.Full().Replace("\n", "\r\n");
            _box.Location = new Point(mL, y);
            _box.Size = new Size(contentW, Ui.S(190));
            Controls.Add(_box);
            y += _box.Height + Ui.S(12);

            int bh = Ui.S(36);
            RoundButton go = new RoundButton();
            go.Text = "在浏览器里提 issue";
            go.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            go.Fill = Color.FromArgb(0, 122, 204);
            go.FillHover = Color.FromArgb(0, 140, 232);
            go.TextColor = Color.White;
            go.Size = new Size(Ui.S(190), bh);
            go.Location = new Point(mL, y);
            go.Click += new EventHandler(OnOpen);
            Controls.Add(go);

            RoundButton cp = new RoundButton();
            cp.Text = "复制诊断信息";
            cp.Font = new Font("Microsoft YaHei UI", 10f);
            cp.Fill = Color.FromArgb(238, 240, 245);
            cp.FillHover = Color.FromArgb(226, 230, 238);
            cp.TextColor = Color.FromArgb(60, 64, 74);
            cp.Size = new Size(Ui.S(140), bh);
            cp.Location = new Point(go.Right + Ui.S(10), y);
            cp.Click += new EventHandler(OnCopy);
            Controls.Add(cp);

            RoundButton no = new RoundButton();
            no.Text = "关闭";
            no.Font = new Font("Microsoft YaHei UI", 10f);
            no.Fill = Color.FromArgb(238, 240, 245);
            no.FillHover = Color.FromArgb(226, 230, 238);
            no.TextColor = Color.FromArgb(60, 64, 74);
            no.Size = new Size(Ui.S(96), bh);
            no.Location = new Point(winW - mR - no.Width, y);
            no.Click += new EventHandler(delegate(object o, EventArgs e2) { Close(); });
            Controls.Add(no);
            CancelButton = no;
            y += bh + Ui.S(8);

            _state = new Label();
            _state.Text = "";
            _state.ForeColor = Color.FromArgb(150, 152, 160);
            _state.Location = new Point(mL, y);
            Ui.Wrap(_state, contentW);
            Controls.Add(_state);
            y += Ui.S(28);

            ClientSize = new Size(winW, Math.Min(y, wa.Height - Ui.S(24)));
            ResumeLayout();
        }

        void OnOpen(object o, EventArgs e)
        {
            string title = "反馈：" + AppInfo.Name + " v" + AppInfo.Version;
            string body = Diag.Brief() + "\r\n【我遇到的情况】\r\n（在这里写一句：做什么的时候、出现了什么）\r\n";
            string url = "https://github.com/" + AppInfo.Repo + "/issues/new?title=" +
                         Uri.EscapeDataString(title) + "&body=" + Uri.EscapeDataString(body);
            try
            {
                Process.Start(url);
                _state.ForeColor = Color.FromArgb(0, 130, 90);
                _state.Text = "已在浏览器里打开。填完点提交就行 —— 如果页面一直转圈打不开，就改用「复制诊断信息」发给作者。";
            }
            catch (Exception ex)
            {
                _state.ForeColor = Color.FromArgb(190, 90, 40);
                _state.Text = "没能打开浏览器（" + ex.Message + "）—— 请点「复制诊断信息」，把它发给作者；仓库地址：" + url;
            }
        }

        void OnCopy(object o, EventArgs e)
        {
            try
            {
                Clipboard.SetText(Diag.Full());
                _state.ForeColor = Color.FromArgb(0, 130, 90);
                _state.Text = "诊断信息已复制。可以直接发给作者，或粘到任何你能打开的地方。";
            }
            catch (Exception ex)
            {
                _state.ForeColor = Color.FromArgb(190, 90, 40);
                _state.Text = "复制失败（" + ex.Message + "）—— 可以在上面的框里手动选中复制。";
            }
        }
    }
}
