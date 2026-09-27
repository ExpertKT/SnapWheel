using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace SnapWheel
{
    // 取字（OCR）的 **Win7 兜底引擎**：lw.OpenCVDNN.PPOCR —— PP-OCR 的纯 CPU 原生库。
    //
    // 为什么需要它：系统自带的 Windows.Media.Ocr 是 Windows 10 才有的组件，Win7 上
    // 56-Ocr.cs 里那条路整条拿不到（Type.GetType 返回 null）。也就是说，**Win7 上"取字"
    // 本来是直接不可用的**，而取字是快照轮环的刚需之一（DIRECTIONS.md §12 的 Win7 节点）。
    //
    // 为什么选它（选型与实测数字见 DIRECTIONS.md §13.5）：
    //   · MIT、纯 CPU、静态链接（不需要另外装 VC 运行库）、官方 CI 就在校验
    //     "Win7 PE subsystem 6.01 + 静态依赖 + 导出 ABI"，并且**同时出 x64 / x86 两个包** ——
    //     我们是 AnyCPU，32 位 Win7 上跑的是 x86 进程，两个位数都得能配上。
    //   · 它是 **C ABI**（`extern "C"` + `__cdecl`），不是 C++ 类库，P/Invoke 能直接对上，
    //     既不用写 C++ shim，也不用托管包装。
    //   · 我们**不改它一个字节**，只把 DLL + 模型当外部引擎用（"搬"的粒度是 ABI，不是源码）。
    //
    // 为什么用 LoadLibrary + 函数指针，而不是 DllImport("lw.OpenCVDNN.PPOCR.dll")：
    //   DllImport 的名字只能是编译期写死的字符串，靠进程搜索路径去找文件；而这里要的是
    //   "**exe 旁边那个 ocr 目录里有什么就用什么**" —— Win10/11 用户根本不装这个目录
    //   （他们有系统自带 OCR，更好也更小），所以"目录不在"必须是**一条安静的降级路径**，
    //   不能变成一个 TypeInitializationException。
    //   手写函数指针还顺手解决了第二件事：配错位数（32 位进程配了 x64 的 DLL）时
    //   LoadLibrary 会返回 ERROR_BAD_EXE_FORMAT(193)，这条能变成一句人话，
    //   而不是一句"找不到入口点"。
    //
    // 线程：DLL 自己保证"一个 handle 上串行"（头文件原话：A handle serializes its own OCR calls），
    // 所以我们不需要额外加锁；但我们**绝不**在识别进行中销毁 handle。
    static class OcrNative
    {
        // SDK 规定的文件名，不能改（换成别的名字就不是这套 ABI 了）
        const string DllName = "lw.OpenCVDNN.PPOCR.dll";
        const int ErrorBadExeFormat = 193;
        const int ErrorCapacity = 1024;
        const int CpuThreads = 2;          // 给界面留核：识别是后台线程跑的，别把机器吃满

        // 检测（找文字框）那一步的**长边上限**。引擎自己的默认值是 960，而 960 会把
        // 大截图里的字先缩小再检测 —— 一张 5120×1600 的双屏截图会被压掉 5.3 倍，
        // 12~14 px 的界面小字缩到 2~3 px，检测器直接看不见（实测：同一张图
        // 长边 960 → 认出 8 个文本框 / 39 字，原分辨率 → 27 个 / 79 字，
        // 且「老老实实地遵守嘱咐」这类整行在 960 下被截成半句）。
        // 2560 是实测选出来的折中：5120×1600 的真截图，2560 与 4096 认出的内容
        // 逐行比对只差标点/噪点变体（没有哪句真文字只在 4096 出现），但
        //   本机 x64    2560 → 3.6 s / 1485 字   4096 → 5.1 s / 1586 字
        //   Win7 x86 VM 2560 → 4.3 s / 472 MB    4096 → 5.8 s / 961 MB
        // 内存是按"缩过之后"的尺寸算的（与输入多大无关），32 位进程里 961 MB 太贴
        // 上限，所以默认压到 2560：常见屏幕（≤2560 宽）仍然原样进检测，不缩。
        // 需要 A/B 或临时改回去时用环境变量 SNAPWHEEL_OCR_LIMIT。
        const int DefaultLimitSideLen = 2560;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryW(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string procName);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        delegate int CreateW(string detModel, string recModel, string dictionary, int cpuThreads, out IntPtr handle, StringBuilder error, int errorCapacity);

        // ppocr_config_w 的 C# 影子（字段名、顺序、类型都照 sdk\native\include\ppocr_api.h:48-70 抄，
        // 错一个字段就是踩内存）。为什么要用它：只有 create_ex 这条入口能改 limit_side_len，
        // 简版 ppocr_create_w 没有这个参数。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PpocrConfig
        {
            public int struct_size;
            [MarshalAs(UnmanagedType.LPWStr)] public string det_model_path;
            [MarshalAs(UnmanagedType.LPWStr)] public string rec_model_path;
            [MarshalAs(UnmanagedType.LPWStr)] public string rec_dict_path;
            [MarshalAs(UnmanagedType.LPWStr)] public string cls_model_path;
            public int limit_side_len;
            public double det_db_thresh;
            public double det_db_box_thresh;
            public double det_db_unclip_ratio;
            public int use_dilation;
            public int use_angle_cls;
            public double cls_thresh;
            public int cls_batch_num;
            public int rec_batch_num;
            public int rec_img_h;
            public int rec_img_w;
            public int rec_predictor_num;
            public int cpu_threads;
        }

        // 默认值由 DLL 自己填（ppocr_config_init），我们只覆盖上面那条 limit 和线程数 ——
        // 其余阈值保持厂商默认，改动面越小越好排查。
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void ConfigInit(ref PpocrConfig config);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        delegate int CreateExW(ref PpocrConfig config, out IntPtr handle, StringBuilder error, int errorCapacity);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int OcrBgr(IntPtr handle, IntPtr pixels, int width, int height, int channels, int stride, out IntPtr utf8Json, out int jsonSize, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder error, int errorCapacity);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void FreePtr(IntPtr memory);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void DestroyHandle(IntPtr handle);

        static bool _probed;
        static IntPtr _handle = IntPtr.Zero;
        static string _why = "";
        static OcrBgr _ocr;
        static FreePtr _free;

        public static bool Available { get { Probe(); return _handle != IntPtr.Zero; } }
        public static string Why { get { Probe(); return _why; } }
        public static int Limit { get { return LimitSideLen(); } }

        // SNAPWHEEL_OCR_LIMIT 是给测试/排查用的覆盖口（A/B 同一张图），不是给用户配置的开关。
        static int LimitSideLen()
        {
            try
            {
                string s = Environment.GetEnvironmentVariable("SNAPWHEEL_OCR_LIMIT");
                int v;
                if (!string.IsNullOrEmpty(s) && int.TryParse(s, out v) && v >= 128) return v;
            }
            catch { }
            return DefaultLimitSideLen;
        }

        // ---------------- 组件在哪 ----------------
        //
        // 约定（按优先级）：
        //   ① 环境变量 SNAPWHEEL_OCR_DIR 指到哪就用哪（测试和排查用，不写在文档里当用法）
        //   ② exe 旁边的 ocr\ 目录
        //   ③ 直接就在 exe 旁边
        // 目录里要有 DllName + 模型（模型可以在目录里，也可以在它的 inference\ 子目录里）。
        static string PayloadDir()
        {
            List<string> cands = new List<string>();
            string env = null;
            try { env = Environment.GetEnvironmentVariable("SNAPWHEEL_OCR_DIR"); } catch { }
            if (!string.IsNullOrEmpty(env)) cands.Add(env);

            string exeDir = null;
            try { exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); } catch { }
            if (!string.IsNullOrEmpty(exeDir))
            {
                cands.Add(Path.Combine(exeDir, "ocr"));
                cands.Add(exeDir);
            }

            for (int i = 0; i < cands.Count; i++)
                if (File.Exists(Path.Combine(cands[i], DllName)) && FindModels(cands[i]) != null) return cands[i];
            return null;
        }

        // 模型文件名不写死：只认 *det*.onnx / *rec*.onnx / *dict*.txt 这三条规律。
        // 为什么：包换版本时（v5 → v6 → …）文件名会变，写死了就得改代码。
        // 代价是"目录里有多个候选"时取排序第一个 —— 这种目录本来就是我们自己发的，够用。
        static string[] FindModels(string dir)
        {
            string det = FindOne(dir, "*det*.onnx");
            string rec = FindOne(dir, "*rec*.onnx");
            string dict = FindOne(dir, "*dict*.txt");
            if (det == null || rec == null || dict == null) return null;
            return new string[] { det, rec, dict };
        }

        static string FindOne(string dir, string pattern)
        {
            foreach (string d in new string[] { dir, Path.Combine(dir, "inference") })
            {
                try
                {
                    if (!Directory.Exists(d)) continue;
                    string[] hits = Directory.GetFiles(d, pattern);
                    if (hits.Length == 0) continue;
                    Array.Sort(hits, StringComparer.OrdinalIgnoreCase);
                    return hits[0];
                }
                catch { }
            }
            return null;
        }

        // ---------------- 开局 ----------------
        static void Probe()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                string dir = PayloadDir();
                if (dir == null)
                {
                    _why = Lang.T(
                        "没有本地取字组件（Win7 需要 SnapWheel.exe 旁边放一个 ocr 目录，里面是 " + DllName + " 和模型）",
                        "No local OCR component (on Windows 7 put an \"ocr\" folder next to SnapWheel.exe containing " + DllName + " and the models)");
                    return;
                }

                string dllPath = Path.Combine(dir, DllName);
                IntPtr mod = LoadLibraryW(dllPath);
                if (mod == IntPtr.Zero)
                {
                    int code = Marshal.GetLastWin32Error();
                    _why = code == ErrorBadExeFormat
                        ? Lang.T("本地取字组件的位数不对（32 位系统要用 x86 版的 " + DllName + "）",
                                 "The local OCR component has the wrong bitness (a 32-bit system needs the x86 " + DllName + ")")
                        : Lang.T("本地取字组件加载失败（错误 " + code + "）：" + dllPath,
                                 "Could not load the local OCR component (error " + code + "): " + dllPath);
                    try { Err.Log("Ocr", new Exception(_why)); } catch { }
                    return;
                }

                CreateW create = Bind<CreateW>(mod, "ppocr_create_w");
                CreateExW createEx = Bind<CreateExW>(mod, "ppocr_create_ex_w");
                ConfigInit configInit = Bind<ConfigInit>(mod, "ppocr_config_init");
                _ocr = Bind<OcrBgr>(mod, "ppocr_ocr_bgr");
                _free = Bind<FreePtr>(mod, "ppocr_free");
                DestroyHandle destroy = Bind<DestroyHandle>(mod, "ppocr_destroy");
                if ((create == null && (createEx == null || configInit == null)) || _ocr == null || _free == null)
                {
                    _why = Lang.T("本地取字组件不完整（缺导出函数）", "The local OCR component is incomplete (missing exports)");
                    try { Err.Log("Ocr", new Exception(_why)); } catch { }
                    return;
                }
                GC.KeepAlive(destroy);   // 留着引用，免得将来误删导出检查

                string[] models = FindModels(dir);
                StringBuilder err = new StringBuilder(ErrorCapacity);
                IntPtr handle = IntPtr.Zero;
                int rc;
                if (createEx != null && configInit != null)
                {
                    PpocrConfig cfg = new PpocrConfig();
                    configInit(ref cfg);
                    cfg.det_model_path = models[0];
                    cfg.rec_model_path = models[1];
                    cfg.rec_dict_path = models[2];
                    cfg.limit_side_len = LimitSideLen();
                    cfg.cpu_threads = CpuThreads;
                    rc = createEx(ref cfg, out handle, err, err.Capacity);
                }
                else
                {
                    // 老版本 DLL 没有 create_ex：退回简版入口（会吃到 960 的默认上限）
                    rc = create(models[0], models[1], models[2], CpuThreads, out handle, err, err.Capacity);
                }
                if (rc != 0 || handle == IntPtr.Zero)
                {
                    _why = Lang.T("本地取字引擎初始化失败（" + rc + "）：" + err.ToString(),
                                  "Could not initialize the local OCR engine (" + rc + "): " + err.ToString());
                    try { Err.Log("Ocr", new Exception(_why)); } catch { }
                    return;
                }
                _handle = handle;
            }
            catch (Exception ex)
            {
                _why = Lang.T("本地取字组件不可用：", "The local OCR component is unavailable: ") + ex.Message;
                try { Err.Log("Ocr", ex); } catch { }
            }
        }

        static T Bind<T>(IntPtr module, string name) where T : class
        {
            IntPtr p = GetProcAddress(module, name);
            if (p == IntPtr.Zero) return null;
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        // ---------------- 认字 ----------------
        // 传进来的是**紧凑的 BGRA 像素**（就是 Ocr.PixelsOf 拷出来的那份），
        // 所以 channels=4、stride=w*4 直接喂 —— 一个字节都不用转。
        public static string RecognizePixels(byte[] bgra, int w, int h, out string error)
        {
            error = null;
            Probe();
            if (_handle == IntPtr.Zero) { error = _why; return null; }
            if (bgra == null || w <= 0 || h <= 0 || bgra.Length < w * 4 * h)
            {
                error = Lang.T("像素数据不完整", "The pixel data is incomplete");
                return null;
            }

            GCHandle pin = default(GCHandle);
            try
            {
                pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
                IntPtr json = IntPtr.Zero;
                int size = 0;
                StringBuilder err = new StringBuilder(ErrorCapacity);
                int rc = _ocr(_handle, pin.AddrOfPinnedObject(), w, h, 4, w * 4, out json, out size, err, err.Capacity);
                if (rc != 0 || json == IntPtr.Zero || size <= 0)
                {
                    error = Lang.T("识别失败（" + rc + "）：" + err.ToString(), "Recognition failed (" + rc + "): " + err.ToString());
                    try { Err.Log("Ocr", new Exception(error)); } catch { }
                    return null;
                }
                try
                {
                    byte[] utf8 = new byte[size];
                    Marshal.Copy(json, utf8, 0, size);
                    return JoinTexts(Encoding.UTF8.GetString(utf8));
                }
                finally { try { _free(json); } catch { } }   // 头文件：utf8_json 由 DLL 分配，必须 ppocr_free
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { Err.Log("Ocr", ex); } catch { }
                return null;
            }
            finally { if (pin.IsAllocated) pin.Free(); }
        }

        // JSON → 一段文字。
        //
        // 为什么自己扫而不用 JavaScriptSerializer：那个在 System.Web.Extensions 里，
        // 是 ASP.NET 的程序集，"一个 exe、零依赖"这条不破但要多背一个程序集引用和一条
        // 运行时解析路径（Win7 上还可能只装了 Client Profile 里没有它）。
        // 而我们要的信息只有一样：按顺序取出每个文本块的 "text"。
        // 输出格式是**我们自己钉死版本的那个 DLL** 产的（{"elapsed_ms":…,"results":[{"text":…},…]}），
        // 不是什么通用 JSON —— 扫不出东西时下面会明确报错，不会悄悄返回空字符串。
        static string JoinTexts(string json)
        {
            List<string> lines = new List<string>();
            if (!string.IsNullOrEmpty(json))
            {
                int i = 0;
                while (true)
                {
                    int k = json.IndexOf("\"text\"", i, StringComparison.Ordinal);
                    if (k < 0) break;
                    k += 6;
                    while (k < json.Length && (json[k] == ' ' || json[k] == '\t' || json[k] == ':')) k++;
                    if (k >= json.Length || json[k] != '"') { i = k; continue; }
                    k++;
                    StringBuilder sb = new StringBuilder();
                    while (k < json.Length && json[k] != '"')
                    {
                        char c = json[k];
                        if (c == '\\' && k + 1 < json.Length)
                        {
                            k++;
                            char e = json[k];
                            if (e == 'n') sb.Append('\n');
                            else if (e == 'r') sb.Append('\r');
                            else if (e == 't') sb.Append('\t');
                            else if (e == 'b') sb.Append('\b');
                            else if (e == 'f') sb.Append('\f');
                            else if (e == 'u')
                            {
                                int cp;
                                if (k + 4 < json.Length && int.TryParse(json.Substring(k + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cp))
                                {
                                    sb.Append((char)cp);
                                    k += 4;
                                }
                            }
                            else sb.Append(e);          // \" \\ \/ 等：原样就是那个字符
                        }
                        else sb.Append(c);
                        k++;
                    }
                    string t = sb.ToString().Trim();
                    if (t.Length > 0) lines.Add(t);
                    i = k + 1;
                }
            }
            return string.Join("\n", lines.ToArray());
        }
    }
}
