using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace SnapWheel
{
    // 非图片格怎么画：文字格 / 文件格都是"纸"。
    // 这是 §九「材质跟着内容走」的第一行 —— 截图=玻璃、文字=纸；
    // 颜色=磨砂、盯屏=暗色雷达 那两行要等 1.6 / 1.7 有那种格子了才存在。
    partial class WheelForm
    {
        // 纸的颜色**不跟着主题走**：暗色桌面上贴一张纸是这套设计里少数"实物感"的来源，
        // 反过来（深色纸）在浅色主题上会像一块补丁。文字格 / 文件格都是纸，靠顶部那条色带区分。
        internal static readonly Color PaperBg = Color.FromArgb(252, 250, 244);
        internal static readonly Color PaperInk = Color.FromArgb(44, 42, 40);
        internal static readonly Color PaperSub = Color.FromArgb(146, 144, 140);

        // 文件格按种类上色。用户实测反馈（2026-09-27）："不同文件类型辨识度还是不高，可以做出颜色的差别" ——
        // 一张 PDF 和一张压缩包原来都是同一张灰白纸，扫一眼分不出来。
        // 分组刻意粗（文档 / 表格 / 演示 / 压缩包 / 音视频 / 程序）：分得太细反而记不住，要的是"一眼分得出"。
        // 认不出来的后缀就给 PaperSub（不瞎猜），这样"不知道怎么归类"和"就是一坨文件"是同一种样子。
        internal static readonly Color KindDoc = Color.FromArgb(54, 116, 208);
        internal static readonly Color KindSheet = Color.FromArgb(36, 140, 92);
        internal static readonly Color KindSlide = Color.FromArgb(226, 120, 50);
        internal static readonly Color KindPack = Color.FromArgb(198, 150, 40);
        internal static readonly Color KindMedia = Color.FromArgb(132, 90, 202);
        internal static readonly Color KindProg = Color.FromArgb(64, 72, 86);   // 比纸深一档的冷灰 —— (96,104,116) 那档染 12% 之后跟"认不出来"的纸色几乎一样（2026-09-27 对着出图改的）

        internal static Color FileKindColor(string nameOrPath)
        {
            string ext = "";
            try { ext = Path.GetExtension(nameOrPath == null ? "" : nameOrPath).ToLowerInvariant(); } catch { }
            switch (ext)
            {
                case ".pdf": case ".doc": case ".docx": case ".rtf": case ".odt": case ".wps":
                case ".txt": case ".md": case ".log": case ".ini": case ".json": case ".xml":
                    return KindDoc;
                case ".xls": case ".xlsx": case ".csv": case ".ods":
                    return KindSheet;
                case ".ppt": case ".pptx": case ".odp":
                    return KindSlide;
                case ".zip": case ".rar": case ".7z": case ".tar": case ".gz": case ".bz2": case ".xz":
                    return KindPack;
                case ".mp3": case ".wav": case ".flac": case ".m4a": case ".ape":
                case ".mp4": case ".mkv": case ".avi": case ".mov": case ".wmv": case ".flv":
                    return KindMedia;
                case ".exe": case ".msi": case ".bat": case ".cmd": case ".ps1": case ".dll": case ".sys":
                    return KindProg;
                default:
                    return PaperSub;
            }
        }

        // 把颜色 c 按 k 混进 baseC（k=0 原样、k=1 全是 c）。只用来给纸底染一点点种类色。
        internal static Color Tint(Color baseC, Color c, float k)
        {
            if (k < 0f) k = 0f; if (k > 1f) k = 1f;
            return Color.FromArgb(baseC.A,
                (int)Math.Round(baseC.R + (c.R - baseC.R) * k),
                (int)Math.Round(baseC.G + (c.G - baseC.G) * k),
                (int)Math.Round(baseC.B + (c.B - baseC.B) * k));
        }

        // 把一段文字按宽度切成若干行。中文没有空格，只能逐字量宽度。
        // 独立成 static：测试可以直接调，不需要窗口、不需要渲染。
        // 返回值最多 maxLines 行；内容没画完时把最后一行收成省略号（"框里还有"这件事必须看得见）。
        internal static List<string> CellLines(Graphics g, Font f, string text, float maxW, int maxLines)
        {
            List<string> lines = new List<string>();
            if (g == null || f == null || string.IsNullOrEmpty(text) || maxLines <= 0 || maxW <= 1f) return lines;
            bool cut = false;
            string[] raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            StringFormat sf = StringFormat.GenericTypographic;
            for (int li = 0; li < raw.Length && lines.Count < maxLines; li++)
            {
                string s = raw[li];
                if (s.Length == 0) { lines.Add(""); continue; }
                int start = 0;
                while (start < s.Length && lines.Count < maxLines)
                {
                    int n = 1;
                    try
                    {
                        // 一个字一个字地加，加到再加一个就超宽为止（超长的那个字自己占一行，不留空）
                        while (start + n < s.Length &&
                               g.MeasureString(s.Substring(start, n + 1), f, new PointF(0, 0), sf).Width <= maxW) n++;
                    }
                    catch { n = s.Length - start; }
                    lines.Add(s.Substring(start, n));
                    start += n;
                    if (lines.Count >= maxLines && (start < s.Length || li < raw.Length - 1)) cut = true;
                }
            }
            if (cut && lines.Count > 0)
            {
                // 收省略号时要把尾部削掉几个字：直接往满行后面加"…"会顶出框外
                //（这一条是测试钉出来的 —— 加了省略号之后那一行的宽度必须还在 maxW 以内）
                string last = lines[lines.Count - 1].TrimEnd();
                while (last.Length > 1)
                {
                    string cand = last + "…";
                    float w;
                    try { w = g.MeasureString(cand, f, new PointF(0, 0), sf).Width; } catch { break; }
                    if (w <= maxW) { last = cand; break; }
                    last = last.Substring(0, last.Length - 1);
                }
                lines[lines.Count - 1] = last;
            }
            return lines;
        }

        // 画一格文字 / 文件。rr = 卡片矩形（悬浮 / 动画的缩放已经在里面），rad = 圆角，ia = 整格透明度。
        // 调用方负责别把图片格送进来（那一条走 2c3-图片）。
        void DrawPaperCell(Graphics g, StoreItem it, RectangleF rr, float rad, int ia)
        {
            if (g == null || it == null) return;
            bool isText = (it.Kind == CellKind.Text);
            // 种类色：文字格=主题色，文件格=看后缀（见 FileKindColor）。纸底也染 12% 这个色 ——
            // 只靠顶上那 3px 在缩略图上太细，整张纸带一点色才是一眼分得出。
            Color kind = isText ? AccentColor()
                                : FileKindColor(it.Name != null && it.Name.Length > 0 ? it.Name : it.FilePath);
            Color bgCol = isText ? PaperBg : Tint(PaperBg, kind, 0.12f);
            float pad = 9f;
            RectangleF inner = new RectangleF(rr.X + pad, rr.Y + pad,
                                              Math.Max(4f, rr.Width - pad * 2f), Math.Max(4f, rr.Height - pad * 2f));
            try
            {
                using (GraphicsPath card = Gfx.Round(rr, rad))
                {
                    using (SolidBrush bg = new SolidBrush(Gfx.A(bgCol, ia))) g.FillPath(bg, card);
                    g.SetClip(card);

                    // 顶部那一小条：文字格用主题色、文件格用它的种类色。两种都是纸，靠它一眼分开
                    using (SolidBrush band = new SolidBrush(Gfx.A(kind, ia)))
                        g.FillRectangle(band, inner.X, inner.Y, Math.Max(10f, rr.Width * 0.3f), 3f);

                    if (!isText)
                    {
                        // 文件格：右上角先写后缀。格子小的时候名字会被截断，后缀是最后一个能救回来的信息
                        string ext = "";
                        try { ext = Path.GetExtension(it.Name != null && it.Name.Length > 0 ? it.Name : (it.FilePath ?? "")); } catch { }
                        if (ext.Length > 0)
                        using (Font fe = new Font("Microsoft YaHei UI", 8f, FontStyle.Bold))
                        {
                            float eh = fe.GetHeight(g);
                            StringFormat rf = new StringFormat(StringFormat.GenericTypographic);
                            rf.Alignment = StringAlignment.Far;
                            using (SolidBrush sb = new SolidBrush(Gfx.A(kind, ia)))
                                g.DrawString(ext.ToLowerInvariant(), fe, sb,
                                             new RectangleF(inner.X, inner.Y - 3f, inner.Width, eh + 1f), rf);
                            inner.Y += eh; inner.Height -= eh;
                        }
                    }
                    inner.Y += 5f; inner.Height -= 5f;

                    string body = isText ? it.Text
                                         : (string.IsNullOrEmpty(it.Name) ? (it.FilePath ?? "") : it.Name);
                    using (Font f = new Font("Microsoft YaHei UI", 9f, isText ? FontStyle.Regular : FontStyle.Bold))
                    {
                        // 行高用 Font.GetHeight：量的高度 ≠ 画需要的高度（见 16-DrawKit.cs 里那条教训）
                        float lh = f.GetHeight(g);
                        int maxLines = Math.Max(1, (int)Math.Floor(inner.Height / Math.Max(1f, lh)));
                        List<string> lines = CellLines(g, f, body, inner.Width, maxLines);
                        using (SolidBrush ink = new SolidBrush(Gfx.A(PaperInk, ia)))
                            for (int i = 0; i < lines.Count; i++)
                                g.DrawString(lines[i], f, ink,
                                             new RectangleF(inner.X, inner.Y + i * lh - 1f, inner.Width + 2f, lh + 2f),
                                             StringFormat.GenericTypographic);
                    }
                    g.ResetClip();
                }
            }
            catch (Exception ex) { Err.Log("DrawPaperCell", ex); }
        }
    }
}
