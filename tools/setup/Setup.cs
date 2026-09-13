// SnapWheel 一键安装器（自包含，无第三方依赖，单文件 exe）
// 功能：安装（复制到 LocalAppData + 建桌面/开始菜单快捷方式 + 写卸载项）、卸载
// 编译：csc /target:winexe /out:SnapWheelSetup.exe tools\setup\Setup.cs
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

static class Setup
{
    const string AppName = "SnapWheel 快照轮环";
    const string ExeName = "SnapWheel 快照轮环.exe";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SnapWheel";

    static string InstallDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapWheel"); }
    }
    static string InstalledExe { get { return Path.Combine(InstallDir, ExeName); } }
    static bool IsInstalled { get { return File.Exists(InstalledExe); } }

    static string SourceExe()
    {
        // 安装器通常和 SnapWheel.exe 放在同一个文件夹里（发布 zip 的结构）
        string me = Assembly.GetExecutingAssembly().Location;
        string dir = Path.GetDirectoryName(me);
        string[] cand = { Path.Combine(dir, "SnapWheel.exe"), Path.Combine(dir, ExeName) };
        for (int i = 0; i < cand.Length; i++) if (File.Exists(cand[i])) return cand[i];
        return null;
    }

    static void KillRunning()
    {
        try
        {
            Process[] ps = Process.GetProcessesByName("SnapWheel");
            for (int i = 0; i < ps.Length; i++) { try { ps[i].Kill(); ps[i].WaitForExit(2000); } catch { } }
            Process[] p2 = Process.GetProcessesByName("SnapWheel 快照轮环");
            for (int i = 0; i < p2.Length; i++) { try { p2[i].Kill(); p2[i].WaitForExit(2000); } catch { } }
        }
        catch { }
    }

    // 建快捷方式：用 WScript.Shell（系统自带，不用引第三方库）
    static void Shortcut(string lnk, string target, string workDir)
    {
        try
        {
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            object link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            Type lt = link.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { workDir });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { AppName + " · 贴在屏幕角落的截图轮盘" });
            lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { target + ",0" });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }
        catch (Exception ex) { throw new Exception("创建快捷方式失败: " + ex.Message); }
    }

    static string DoInstall()
    {
        string src = SourceExe();
        if (src == null) return "没找到 SnapWheel.exe。\n请把安装器和 SnapWheel.exe 放在同一个文件夹里再运行。";
        if (!Directory.Exists(InstallDir)) Directory.CreateDirectory(InstallDir);
        KillRunning();
        System.Threading.Thread.Sleep(400);
        File.Copy(src, InstalledExe, true);
        if (!File.Exists(InstalledExe)) return "复制文件失败（可能被占用）。请先退出正在运行的 SnapWheel 再试。";

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Shortcut(Path.Combine(desktop, AppName + ".lnk"), InstalledExe, InstallDir);
        string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SnapWheel");
        if (!Directory.Exists(startMenu)) Directory.CreateDirectory(startMenu);
        Shortcut(Path.Combine(startMenu, AppName + ".lnk"), InstalledExe, InstallDir);

        try
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayIcon", InstalledExe);
                k.SetValue("DisplayVersion", "0.4.8");
                k.SetValue("Publisher", "exper7");
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + Application.ExecutablePath + "\" --uninstall");
                k.SetValue("NoModify", 1);
                k.SetValue("NoRepair", 1);
            }
        }
        catch { }

        try { Process.Start(InstalledExe); } catch { }
        return null;
    }

    static string DoUninstall()
    {
        KillRunning();
        System.Threading.Thread.Sleep(500);
        try { File.Delete(InstalledExe); } catch { return "删除程序失败（可能还在运行）。"; }
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            File.Delete(Path.Combine(desktop, AppName + ".lnk"));
            string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SnapWheel");
            File.Delete(Path.Combine(startMenu, AppName + ".lnk"));
            if (Directory.Exists(startMenu) && Directory.GetFiles(startMenu).Length == 0) Directory.Delete(startMenu);
        }
        catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        try { if (Directory.Exists(InstallDir) && Directory.GetFiles(InstallDir).Length == 0) Directory.Delete(InstallDir); } catch { }
        return null;
    }

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--uninstall")
        {
            string e = DoUninstall();
            MessageBox.Show(e == null ? "已卸载完成。" : e, AppName, MessageBoxButtons.OK,
                e == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Form f = new Form();
        f.Text = AppName + " 安装";
        f.ClientSize = new Size(430, 240);
        f.FormBorderStyle = FormBorderStyle.FixedDialog;
        f.MaximizeBox = false; f.MinimizeBox = false;
        f.StartPosition = FormStartPosition.CenterScreen;
        f.Font = new Font("Microsoft YaHei UI", 9.5f);
        f.BackColor = Color.FromArgb(250, 250, 252);
        try { f.Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }

        Label title = new Label();
        title.Text = AppName;
        title.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
        title.ForeColor = Color.FromArgb(30, 32, 38);
        title.AutoSize = true;
        title.Location = new Point(24, 20);
        f.Controls.Add(title);

        Label sub = new Label();
        sub.Text = IsInstalled ? "已经装过了。重新安装会覆盖成当前版本。" : "贴在屏幕角落的截图轮盘 · 单文件绿色版";
        sub.ForeColor = Color.FromArgb(110, 116, 128);
        sub.AutoSize = true;
        sub.Location = new Point(26, 56);
        f.Controls.Add(sub);

        Label info = new Label();
        info.Text = "安装位置：" + InstallDir + "\n会创建桌面和开始菜单快捷方式，可以在「设置 → 应用」里卸载。";
        info.ForeColor = Color.FromArgb(90, 96, 108);
        info.AutoSize = false;
        info.Size = new Size(380, 50);
        info.Location = new Point(26, 84);
        f.Controls.Add(info);

        Button ok = new Button();
        ok.Text = IsInstalled ? "重新安装" : "一键安装";
        ok.Size = new Size(120, 38);
        ok.Location = new Point(24, 160);
        ok.FlatStyle = FlatStyle.System;
        Button cancel = new Button();
        cancel.Text = "取消";
        cancel.Size = new Size(90, 38);
        cancel.Location = new Point(154, 160);
        cancel.FlatStyle = FlatStyle.System;
        Button un = new Button();
        un.Text = "卸载";
        un.Size = new Size(90, 38);
        un.Location = new Point(254, 160);
        un.FlatStyle = FlatStyle.System;
        un.Enabled = IsInstalled;
        f.Controls.Add(ok); f.Controls.Add(cancel); f.Controls.Add(un);

        ok.Click += delegate(object s, EventArgs e)
        {
            ok.Enabled = false; cancel.Enabled = false; un.Enabled = false;
            string err = DoInstall();
            if (err == null)
            {
                MessageBox.Show("安装完成！\n\n已经帮你启动了，右下角托盘里能找到它。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                f.Close();
            }
            else
            {
                MessageBox.Show(err, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ok.Enabled = true; cancel.Enabled = true; un.Enabled = true;
            }
        };
        cancel.Click += delegate(object s, EventArgs e) { f.Close(); };
        un.Click += delegate(object s, EventArgs e)
        {
            if (MessageBox.Show("确定卸载？\n（不会删除你收藏的截图）", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            string err = DoUninstall();
            MessageBox.Show(err == null ? "已卸载。" : err, AppName, MessageBoxButtons.OK,
                err == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            f.Close();
        };

        Application.Run(f);
    }
}
