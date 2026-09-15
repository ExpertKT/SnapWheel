using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;

namespace SnapWheel
{
    // 取字（OCR）：用 Windows 10/11 系统自带的 Windows.Media.Ocr —— 就是系统"截图工具"里
    // 那个"文本操作"用的同一套引擎。**不引入任何第三方库**，"一个 exe、零依赖"这条不破。
    //
    // 为什么代码长这样：它是 WinRT 组件，而我们是 csc 直接编译、机器上没有 Windows SDK 的
    // winmd（没法在编译期引用那些类型）。所以：
    //   · 类型全部用 Type.GetType("…, Windows.Foundation, ContentType=WindowsRuntime") 在运行时拿
    //   · 异步用 System.Runtime.WindowsRuntime（.NET 框架自带，不是第三方）里的 AsTask 桥接成同步
    //   · 图片走 内存流(PNG) → WinRT 随机访问流 → BitmapDecoder → SoftwareBitmap
    // 任何一步失败都返回 null + 一句人话，绝不把异常抛到界面上。
    static class Ocr
    {
        static bool _probed;
        static object _engine;
        static string _lang = "";
        static string _why = "";

        public static bool Available { get { Probe(); return _engine != null; } }
        public static string Language { get { Probe(); return _lang; } }
        public static string Why { get { Probe(); return _why; } }

        static Type WinRT(string name)
        {
            try { return Type.GetType(name + ", Windows.Foundation, ContentType=WindowsRuntime"); }
            catch { return null; }
        }

        static void Probe()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                Type t = WinRT("Windows.Media.Ocr.OcrEngine");
                if (t == null) { _why = "这台系统没有 OCR 组件（需要 Windows 10 及以上）"; return; }

                // 1) 按系统/用户语言直接来一个
                MethodInfo fromUser = t.GetMethod("TryCreateFromUserProfileLanguages", BindingFlags.Public | BindingFlags.Static);
                if (fromUser != null)
                {
                    try { _engine = fromUser.Invoke(null, null); } catch { _engine = null; }
                }
                if (_engine != null) { _lang = LangOf(_engine); return; }

                // 2) 退而求其次：从系统已装的识别语言里挑一个（优先中文）
                object list = null;
                PropertyInfo prop = t.GetProperty("AvailableRecognizerLanguages", BindingFlags.Public | BindingFlags.Static);
                if (prop != null) { try { list = prop.GetValue(null, null); } catch { } }
                if (list == null) { _why = "系统没有安装任何 OCR 识别语言（设置 → 时间和语言 → 语言 → 该语言的「可选功能」里勾选「光学字符识别」）"; return; }

                List<object> langs = new List<object>();
                System.Collections.IEnumerable en = list as System.Collections.IEnumerable;
                if (en != null) { foreach (object o in en) langs.Add(o); }
                else
                {
                    // 有些投影只给 Size/GetAt
                    PropertyInfo size = list.GetType().GetProperty("Size");
                    MethodInfo getAt = list.GetType().GetMethod("GetAt");
                    if (size != null && getAt != null)
                    {
                        int n = (int)size.GetValue(list, null);
                        for (int i = 0; i < n; i++) langs.Add(getAt.Invoke(list, new object[] { i }));
                    }
                }
                MethodInfo fromLang = t.GetMethod("TryCreateFromLanguage", BindingFlags.Public | BindingFlags.Static);
                object pick = null;
                for (int i = 0; i < langs.Count; i++)
                    if (LangOf(langs[i]).StartsWith("zh")) { pick = langs[i]; break; }
                if (pick == null && langs.Count > 0) pick = langs[0];
                if (pick == null) { _why = "系统没有安装任何 OCR 识别语言"; return; }
                if (fromLang != null) { try { _engine = fromLang.Invoke(null, new object[] { pick }); } catch { } }
                if (_engine != null) _lang = LangOf(_engine);
                else _why = "OCR 引擎创建失败（语言包可能不完整）";
            }
            catch (Exception ex)
            {
                _why = "OCR 不可用：" + ex.Message;
            }
        }

        static string LangOf(object langObj)
        {
            try
            {
                if (langObj == null) return "";
                PropertyInfo p = langObj.GetType().GetProperty("LanguageTag");
                if (p != null) return (string)p.GetValue(langObj, null) ?? "";
                PropertyInfo pl = langObj.GetType().GetProperty("RecognizerLanguage");
                if (pl != null)
                {
                    object l = pl.GetValue(langObj, null);
                    if (l != null) return (string)l.GetType().GetProperty("LanguageTag").GetValue(l, null) ?? "";
                }
            }
            catch { }
            return "";
        }

        // IAsyncOperation<T> -> 同步拿结果（用 System.Runtime.WindowsRuntime 的 AsTask 桥接）
        static object Await(object op, string resultTypeName, int timeoutMs)
        {
            if (op == null) return null;
            Type resType = WinRT(resultTypeName);
            MethodInfo asTask = null;
            foreach (MethodInfo mi in typeof(System.WindowsRuntimeSystemExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (mi.Name != "AsTask" || !mi.IsGenericMethod) continue;
                if (mi.GetGenericArguments().Length != 1) continue;
                ParameterInfo[] ps = mi.GetParameters();
                if (ps.Length != 1) continue;
                asTask = mi;
                break;
            }
            if (asTask == null) throw new Exception("找不到 AsTask 桥接方法");
            System.Threading.Tasks.Task task = (System.Threading.Tasks.Task)asTask.MakeGenericMethod(resType).Invoke(null, new object[] { op });
            if (!task.Wait(timeoutMs)) throw new Exception("识别超时");
            return task.GetType().GetProperty("Result").GetValue(task, null);
        }

        static object RandomAccessStreamOf(byte[] bytes)
        {
            // 同一程序集里的 WindowsRuntimeStreamExtensions（按程序集名 Type.GetType 解析不到，就直接从这个程序集里取）
            Type ext = typeof(System.WindowsRuntimeSystemExtensions).Assembly.GetType("System.IO.WindowsRuntimeStreamExtensions");
            if (ext == null) throw new Exception("缺少 System.Runtime.WindowsRuntime");
            MethodInfo m = ext.GetMethod("AsRandomAccessStream", new Type[] { typeof(Stream) });
            if (m == null) throw new Exception("找不到 AsRandomAccessStream");
            MemoryStream ms = new MemoryStream(bytes, false);
            return m.Invoke(null, new object[] { ms });
        }

        // 直接把像素喂给 OCR：省掉"位图→PNG→再解码"这一趟来回。
        // 这一步是给后台线程用的 —— 传进来的是已经拷好的 BGRA 字节，后台线程不碰任何 GDI 对象。
        static object SoftwareBitmapFromPixels(byte[] bgra, int w, int h)
        {
            Type bufExt = typeof(System.WindowsRuntimeSystemExtensions).Assembly
                .GetType("System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions");
            if (bufExt == null) return null;
            MethodInfo asBuffer = bufExt.GetMethod("AsBuffer", new Type[] { typeof(byte[]) });
            if (asBuffer == null) return null;
            object ibuf = asBuffer.Invoke(null, new object[] { bgra });

            Type sbT = WinRT("Windows.Graphics.Imaging.SoftwareBitmap");
            Type fmtT = WinRT("Windows.Graphics.Imaging.BitmapPixelFormat");
            Type alphaT = WinRT("Windows.Graphics.Imaging.BitmapAlphaMode");
            Type ibufT = WinRT("Windows.Storage.Streams.IBuffer");
            if (sbT == null || fmtT == null || alphaT == null || ibufT == null) return null;
            MethodInfo create = sbT.GetMethod("CreateCopyFromBuffer", new Type[] { ibufT, fmtT, typeof(int), typeof(int), alphaT });
            if (create == null) return null;
            object fmt = Enum.Parse(fmtT, "Bgra8");
            object alpha = Enum.Parse(alphaT, "Premultiplied");
            return create.Invoke(null, new object[] { ibuf, fmt, w, h, alpha });
        }

        // 把一张位图的像素拷成 BGRA 字节（在 UI 线程调用，之后可以安全地丢给后台线程）
        public static byte[] PixelsOf(Bitmap bmp, out int w, out int h)
        {
            w = bmp.Width; h = bmp.Height;
            Rectangle rc = new Rectangle(0, 0, w, h);
            BitmapData d = bmp.LockBits(rc, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int stride = d.Stride;
                byte[] raw = new byte[Math.Abs(stride) * h];
                System.Runtime.InteropServices.Marshal.Copy(d.Scan0, raw, 0, raw.Length);
                // 去掉行尾填充，拼成紧凑的 w*4 每行（WinRT 那边要求连续）
                byte[] packed = new byte[w * 4 * h];
                for (int y = 0; y < h; y++)
                    Buffer.BlockCopy(raw, y * Math.Abs(stride), packed, y * w * 4, w * 4);
                return packed;
            }
            finally { bmp.UnlockBits(d); }
        }

        // 识别已经拷好的像素（后台线程可调）
        public static string RecognizePixels(byte[] bgra, int w, int h, out string error)
        {
            error = null;
            Probe();
            if (_engine == null) { error = _why; return null; }
            try
            {
                // ① 低对比度先拉伸：暗色主题截图、半透明面板上的浅灰字最容易认错，
                //    而直方图拉开之后再交给引擎，实测能明显少错字（见 Stretch 的说明）
                bool stretched;
                byte[] pre = Stretch(bgra, w, h, out stretched);
                object sw = SoftwareBitmapFromPixels(pre, w, h);
                if (sw == null) { error = "这台系统不支持直接把像素交给 OCR"; return null; }
                float wordH;
                string txt = RecognizeSoftwareBitmap(sw, out error, out wordH);
                if (txt == null) return null;

                // 字太小就放大再认一遍 —— 这是准确率的关键。
                // 实测（900x380 合成图，字符级准确率）：14px 的字在 1x 下只有 25%，放大 2 倍到 92%；
                // 20px 是 28% -> 96%；连 32px 低对比度也是 13% -> 99%。屏幕截图里的正文多半就是
                // 14~20px，所以"不准"基本都是这个原因。
                //
                // 0.6.0 修正：这段注释原来写着"判据两条，缺一不可"，但代码里**一条都没用** ——
                // 实际上是无条件放大 2 倍、再把放大结果**无条件**当答案（量到的字高 `wordH` 声明了却从没读过）。
                // 现在：按量到的字高决定倍数，并且**择优**（放大那份只有认出的字更多才采用）。
                int chars1 = Chars(txt);
                float k;
                if (chars1 < 8) k = 3f;                                 // 几乎没认出来 -> 字高不可信，直接 3 倍
                else if (wordH > 0.5f && wordH < 12f) k = 3f;           // 很小的字
                else if (wordH > 0.5f && wordH < 18f) k = 2.5f;
                else if (wordH > 0.5f && wordH < 26f) k = 2f;           // 常见正文
                else k = 1.5f;                                          // 已经够大：只补一点点

                double area = (double)w * h;
                if (area * k * k > 8.0e6) k = (float)Math.Sqrt(8.0e6 / area);   // 放大后别超过 8M 像素
                if (k < 1f) k = 1f;
                if (k > 3f) k = 3f;
                string bigger = null;
                if (k > 1.05f)
                {
                    int nw = (int)(w * k), nh = (int)(h * k);
                    if (nw <= 10000 && nh <= 10000 && nw * nh < 40 * 1000 * 1000)
                    {
                        byte[] scaled = ScalePixels(pre, w, h, nw, nh);
                        if (scaled != null)
                        {
                            object sw2 = SoftwareBitmapFromPixels(scaled, nw, nh);
                            if (sw2 != null)
                            {
                                string e2 = null; float h2;
                                string t2 = RecognizeSoftwareBitmap(sw2, out e2, out h2);
                                // 择优：认出的字**更多**才用放大那份（原来是无条件采用，可能反而更差）
                                if (t2 != null && Chars(t2) > chars1) bigger = t2;
                            }
                        }
                    }
                }
                return bigger ?? txt;
            }
            catch (Exception ex)
            {
                Exception real = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                error = real.Message;
                try { Err.Log("Ocr", real); } catch { }
                return null;
            }
        }

        // 低对比度拉伸：只在"图确实偏灰"时才动，而且是**线性拉伸直方图**（不改颜色关系、不做二值化 ——
        // 二值化会把抗锯齿边缘咬碎，引擎反而更容易认错）。
        // 判据：取亮度直方图的 2% / 98% 分位，跨度 < 200 才拉伸（也就是"最暗的 2% 和最亮的 2% 挤在
        // 中间一小段里"）。对比度本来就好的图（跨度 >= 200）原样返回，连一次拷贝都不做。
        // 为什么要它：暗色主题的窗口、半透明面板上的浅灰字，直方图全挤在 60~140 这一段，
        // 引擎的字形分割很容易切错 —— 拉开之后错字明显变少。
        static byte[] Stretch(byte[] src, int w, int h, out bool changed)
        {
            changed = false;
            if (src == null || w <= 8 || h <= 8 || src.Length < w * h * 4) return src;

            int[] hist = new int[256];
            int n = 0;
            for (int i = 0; i + 3 < src.Length; i += 4)
            {
                int lum = (src[i] * 29 + src[i + 1] * 150 + src[i + 2] * 77) >> 8;   // BGRA 的亮度近似
                hist[lum]++;
                n++;
            }
            if (n < 2000) return src;                     // 小图不值得折腾

            // ⚠️ 这里刻意取 **0.2% 分位**而不是常说的 2%：
            // 低对比度图的绝大多数像素都是**背景色**，取 2% 分位时往下数 2% 就已经数到背景上了，
            // 于是 hi 会等于 lo、跨度算成 0，被判成"几乎是纯色，拉也白拉"而永远不拉伸。
            // （0.6.0 实测：一张灰底浅灰字（亮度 84~110）的图就是这样被跳过的，识别结果是空的。）
            // 0.2% 既能认到真正的浅色文字，又能滤掉零星噪点。
            int cut = n / 500;                            // 0.2%
            int lo = 0, hi = 255, acc = 0;
            for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= cut) { lo = i; break; } }
            acc = 0;
            for (int i = 255; i >= 0; i--) { acc += hist[i]; if (acc >= cut) { hi = i; break; } }
            if (hi - lo >= 200 || hi - lo < 16) return src;   // 对比度够好 / 几乎是纯色

            byte[] map = new byte[256];
            double sc = 255.0 / (hi - lo);
            for (int i = 0; i < 256; i++)
            {
                int v = (int)((i - lo) * sc);
                map[i] = (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
            }
            byte[] outp = new byte[src.Length];
            Buffer.BlockCopy(src, 0, outp, 0, src.Length);
            for (int i = 0; i + 3 < outp.Length; i += 4)
            {
                outp[i] = map[src[i]];                    // B
                outp[i + 1] = map[src[i + 1]];            // G
                outp[i + 2] = map[src[i + 2]];            // R
                // A 不动：截图是不透明的，动它反而会改变预乘关系
            }
            changed = true;
            return outp;
        }

        static int Chars(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int n = 0;
            for (int i = 0; i < s.Length; i++) if (!char.IsWhiteSpace(s[i])) n++;
            return n;
        }

        // 像素放大（自己算，不用 GDI 位图 —— 这样后台线程完全不碰 GDI）。
        // 双线性足够：OCR 要的是"字够大"，不是像素级完美。
        // 放大像素：用 GDI+ 的高质量双三次（自己写的双线性更糊，低对比度文字会被糊掉 —— 实测差很多）。
        // 这里在后台线程里**新建**位图、用完就扔，不碰任何别的线程的 GDI 对象，所以是安全的。
        static byte[] ScalePixels(byte[] src, int w, int h, int nw, int nh)
        {
            try
            {
                using (Bitmap small = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                {
                    BitmapData d = small.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                    try
                    {
                        int stride = d.Stride;
                        byte[] row = new byte[w * 4];
                        for (int y = 0; y < h; y++)
                        {
                            Buffer.BlockCopy(src, y * w * 4, row, 0, w * 4);
                            System.Runtime.InteropServices.Marshal.Copy(row, 0, (IntPtr)((long)d.Scan0 + (long)y * stride), w * 4);
                        }
                    }
                    finally { small.UnlockBits(d); }

                    using (Bitmap big = new Bitmap(nw, nh, PixelFormat.Format32bppPArgb))
                    {
                        using (Graphics g = Graphics.FromImage(big))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            g.DrawImage(small, new Rectangle(0, 0, nw, nh));
                        }
                        int aw, ah;
                        return PixelsOf(big, out aw, out ah);
                    }
                }
            }
            catch { return null; }
        }
        // 识别一张图里的文字。成功返回文字（可能为空串 = 图上没字），失败返回 null 并给出 error
        public static string Recognize(Bitmap bmp, out string error)
        {
            error = null;
            if (bmp == null) { error = "没有图"; return null; }
            Probe();
            if (_engine == null) { error = _why; return null; }
            try
            {
                // 引擎对超大图有上限（MaxImageDimension，一般 10000），超过就先缩一下
                Bitmap work = bmp;
                bool own = false;
                try
                {
                    int maxDim = 10000;
                    PropertyInfo mp = WinRT("Windows.Media.Ocr.OcrEngine").GetProperty("MaxImageDimension", BindingFlags.Public | BindingFlags.Static);
                    if (mp != null) { object v = mp.GetValue(null, null); if (v is int) maxDim = (int)v; }
                    if (bmp.Width > maxDim || bmp.Height > maxDim)
                    {
                        double k = Math.Min((double)maxDim / bmp.Width, (double)maxDim / bmp.Height);
                        work = new Bitmap(bmp, new Size(Math.Max(1, (int)(bmp.Width * k)), Math.Max(1, (int)(bmp.Height * k))));
                        own = true;
                    }
                }
                catch { }

                // 首选：直接把像素交过去（省掉 PNG 编码/解码那 6~20ms）
                int pw = 0, ph = 0;
                byte[] px = null;
                try { px = PixelsOf(work, out pw, out ph); } catch { px = null; }
                if (own) { try { work.Dispose(); } catch { } }
                if (px != null)
                {
                    // 0.6.0：又高又长的图（滚动长截图拼出来的那种）先**切条**再识别 ——
                    // 整张丢给引擎会被降采样，小字全糊（见 92-OcrTall.cs）。
                    if (OcrTall.ShouldSplit(pw, ph))
                    {
                        string tall = OcrTall.Recognize(px, pw, ph, out error);
                        if (tall != null) return tall;
                        error = null;      // 切条没成功就退回整张识别，至少能出点东西
                    }
                    string r = RecognizePixels(px, pw, ph, out error);
                    if (r != null || error == null) return r;
                    // 直接喂像素失败就退回老路（PNG）
                }

                byte[] png;
                using (MemoryStream ms = new MemoryStream())
                {
                    work.Save(ms, ImageFormat.Png);
                    png = ms.ToArray();
                }

                Type decT = WinRT("Windows.Graphics.Imaging.BitmapDecoder");
                MethodInfo create = decT.GetMethod("CreateAsync", BindingFlags.Public | BindingFlags.Static, null, new Type[] { WinRT("Windows.Storage.Streams.IRandomAccessStream") }, null);
                if (create == null) throw new Exception("找不到 BitmapDecoder.CreateAsync");
                object decoder = Await(create.Invoke(null, new object[] { RandomAccessStreamOf(png) }), "Windows.Graphics.Imaging.BitmapDecoder", 15000);
                MethodInfo getSb = decoder.GetType().GetMethod("GetSoftwareBitmapAsync", Type.EmptyTypes);   // 它有 4 个重载，必须指定"无参"那个
                object sw = Await(getSb.Invoke(decoder, null), "Windows.Graphics.Imaging.SoftwareBitmap", 15000);
                float mh;
                return RecognizeSoftwareBitmap(sw, out error, out mh);
            }
            catch (Exception ex)
            {
                Exception real = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                error = real.Message;
                try { Err.Log("Ocr", real); } catch { }
                return null;
            }
        }

        static string RecognizeSoftwareBitmap(object sw, out string error, out float medianWordHeight)
        {
            error = null;
            medianWordHeight = 0f;
            try
            {
                MethodInfo rec = _engine.GetType().GetMethod("RecognizeAsync", new Type[] { WinRT("Windows.Graphics.Imaging.SoftwareBitmap") });
                object result = Await(rec.Invoke(_engine, new object[] { sw }), "Windows.Media.Ocr.OcrResult", 30000);
                try { ((IDisposable)sw).Dispose(); } catch { }

                // 按行拼（比整段 Text 更接近原文排版），顺便量一下文字框高度（判断"字有多小"）
                StringBuilder sb = new StringBuilder();
                System.Collections.Generic.List<float> hs = new System.Collections.Generic.List<float>();
                object lines = null;
                PropertyInfo lp = result.GetType().GetProperty("Lines");
                if (lp != null) lines = lp.GetValue(result, null);
                System.Collections.IEnumerable le = lines as System.Collections.IEnumerable;
                if (le != null)
                {
                    foreach (object line in le)
                    {
                        PropertyInfo tp = line.GetType().GetProperty("Text");
                        object t = tp == null ? null : tp.GetValue(line, null);
                        if (t != null) sb.AppendLine(((string)t).TrimEnd());

                        PropertyInfo wp = line.GetType().GetProperty("Words");
                        object words = wp == null ? null : wp.GetValue(line, null);
                        System.Collections.IEnumerable we = words as System.Collections.IEnumerable;
                        if (we == null) continue;
                        foreach (object word in we)
                        {
                            PropertyInfo bp = word.GetType().GetProperty("BoundingRect");
                            object box = bp == null ? null : bp.GetValue(word, null);
                            if (box == null) continue;
                            PropertyInfo hp = box.GetType().GetProperty("Height");
                            if (hp == null) continue;
                            object hv = hp.GetValue(box, null);
                            if (hv is float) hs.Add((float)hv);
                            else if (hv is double) hs.Add((float)(double)hv);
                        }
                    }
                }
                if (hs.Count > 0)
                {
                    hs.Sort();
                    medianWordHeight = hs[hs.Count / 2];
                }

                string text = sb.ToString().Trim();
                if (text.Length == 0)
                {
                    PropertyInfo tp = result.GetType().GetProperty("Text");
                    if (tp != null) { object t = tp.GetValue(result, null); if (t != null) text = ((string)t).Trim(); }
                }
                return TightenCjk(text);
            }
            catch (Exception ex)
            {
                Exception real = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                error = real.Message;
                try { Err.Log("Ocr", real); } catch { }
                return null;
            }
        }

        // 开机后台热身：第一次取字经常要几百毫秒（引擎要激活），先在后台认一张小图把它焐热，
        // 用户第一次真用的时候就是 20ms 级别了。失败就失败，不影响任何功能。
        public static void WarmUpAsync()
        {
            try
            {
                System.Threading.Thread th = new System.Threading.Thread(new System.Threading.ThreadStart(delegate()
                {
                    try
                    {
                        if (!Available) return;
                        using (Bitmap b = new Bitmap(240, 64, PixelFormat.Format32bppPArgb))
                        {
                            using (Graphics g = Graphics.FromImage(b))
                            {
                                g.Clear(Color.White);
                                using (Font f = new Font("Microsoft YaHei UI", 14f))
                                using (SolidBrush br = new SolidBrush(Color.Black))
                                    g.DrawString("warm up 热身", f, br, 6, 6);
                            }
                            string e;
                            Recognize(b, out e);
                        }
                    }
                    catch { }
                }));
                th.IsBackground = true;
                try { th.Priority = System.Threading.ThreadPriority.BelowNormal; } catch { }
                th.Start();
            }
            catch { }
        }

        static bool IsCjk(char c)
        {
            return (c >= 0x3000 && c <= 0x303F)     // CJK 标点
                || (c >= 0x3400 && c <= 0x4DBF)     // 扩展 A
                || (c >= 0x4E00 && c <= 0x9FFF)     // 基本区
                || (c >= 0xF900 && c <= 0xFAFF)     // 兼容
                || (c >= 0xFF00 && c <= 0xFFEF);    // 全角
        }

        // Windows OCR 认中文时会逐字插空格（"本 周 报 告 已 发 出"），用的时候太难看，得拼回去。
        // 规则：空格两边只要有一边是中日韩字符（或标点）就去掉；数字之间也去掉（"1 2" -> "12"）；
        // 纯英文单词之间的空格保留（"Deadline is Friday"）。
        static string TightenCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '\u3000')
                {
                    char p = sb.Length > 0 ? sb[sb.Length - 1] : '\0';
                    char n = (i + 1 < s.Length) ? s[i + 1] : '\0';
                    if (IsCjk(p) || IsCjk(n)) continue;
                    if (char.IsDigit(p) && char.IsDigit(n)) continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
