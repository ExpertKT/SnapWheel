// ocr-native-test.cs -- 本地取字引擎（Win7 那条兜底路）的冒烟测试。
//
// 为什么要有它：Win7 上"取字"只能走 57-OcrNative.cs 这个外部引擎，而开发机是 Win10/11，
// **平时跑的一直是系统自带 OCR** —— 也就是说兜底那条路"没人走过就等于没有"。
// 这个用例强制它走本地引擎（SNAPWHEEL_OCR=native），拿一张自己画的、字**已知**的图去认，
// 认不出来就红 —— 这样"Win7 上的取字"才成为一条可验证的结论，而不是一句"应该能行"。
//
// 前置：本机要有那套装组件（lw.OpenCVDNN.PPOCR.dll + 模型，约 21MB）。没有就**跳过**：
// 开发机上本来就不该有它，只有打包机/真机才带。指定位置用 SNAPWHEEL_OCR_DIR=<目录>。
//
// 编译（build.ps1 -Test 会自动带上）：
//   csc /nologo /target:exe /main:SnapWheel.OcrNativeTest /out:build\_t_ocr-native.exe src\*.cs tests\ocr-native-test.cs
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;

namespace SnapWheel
{
    static class OcrNativeTest
    {
        static int pass, fail;
        static void Check(string n, bool ok, string d)
        {
            if (ok) { pass++; Console.WriteLine("  [OK]   " + n); }
            else { fail++; Console.WriteLine("  [FAIL] " + n + "   " + d); }
        }

        // 一张字写死的白底黑字图：两行，顺序也要一起验
        static Bitmap Sheet()
        {
            Bitmap b = new Bitmap(620, 190);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.White);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                // 用雅黑而不是宋体：小字号下雅黑的笔画更清楚，PP-OCR 也更容易认
                using (Font f = new Font("Microsoft YaHei", 34f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (Brush br = new SolidBrush(Color.Black))
                {
                    g.DrawString("本地取字", f, br, 24, 34);
                    g.DrawString("OCR 1234", f, br, 24, 108);
                }
            }
            return b;
        }

        static int Main()
        {
            Console.WriteLine("取字（本地引擎 / Win7 兜底）");

            string dir = Environment.GetEnvironmentVariable("SNAPWHEEL_OCR_DIR");
            Environment.SetEnvironmentVariable("SNAPWHEEL_OCR", "native");   // 有组件时强制走本地引擎

            // 这里必须看 OcrNative.Available，不能看 Ocr.Available：
            // 后者在本地引擎没起来时会**退回系统 OCR**，于是位数不对的组件也能"通过"，
            // 那这个用例就白跑了（断言的是系统 OCR，不是 Win7 那条路）。
            if (!OcrNative.Available)
            {
                Console.WriteLine("  跳过：本机没有能用的本地取字组件" + (string.IsNullOrEmpty(dir) ? "" : "（SNAPWHEEL_OCR_DIR=" + dir + "）"));
                Console.WriteLine("        " + OcrNative.Why);
                Console.WriteLine("通过 0 / 失败 0");
                return 0;
            }

            string err;
            string text = null;

            // 引擎默认把检测输入的长边压到 960 px —— 大截图（双屏 5120×1600 之类）里的
            // 小字会被缩没，这是"经常识别不到"的主因，所以默认值必须留在放宽后的档位上。
            // 这两条同时证明 create_ex 那条入口真的走通了（结构体字段错位会先在这里炸）。
            Check("检测长边上限已放宽（不是引擎默认的 960）", OcrNative.Limit >= 2560, "实际：" + OcrNative.Limit);
            Environment.SetEnvironmentVariable("SNAPWHEEL_OCR_LIMIT", "2048");
            Check("SNAPWHEEL_OCR_LIMIT 能覆盖上限（A/B 与排查用）", OcrNative.Limit == 2048, "实际：" + OcrNative.Limit);
            Environment.SetEnvironmentVariable("SNAPWHEEL_OCR_LIMIT", null);

            try
            {
                using (Bitmap b = Sheet()) text = Ocr.Recognize(b, out err);
            }
            catch (Exception ex) { err = ex.Message; }

            Console.WriteLine("  认出：" + (text == null ? "(null)" : "\"" + text.Replace("\n", "\\n").Replace("\r", "") + "\""));

            Check("能认出来（不是 null / 不是空）", !string.IsNullOrEmpty(text), text == null ? "error=" + err : "空字符串");
            if (!string.IsNullOrEmpty(text))
            {
                Check("认出了数字 1234", text.Contains("1234"), "实际：" + text);
                Check("认出了中文「取字」", text.Contains("取字"), "实际：" + text);
                int a = text.IndexOf("取字", StringComparison.Ordinal);
                int b2 = text.IndexOf("1234", StringComparison.Ordinal);
                Check("按从上到下的顺序给结果", a >= 0 && b2 >= 0 && a < b2, "取字@" + a + " 1234@" + b2);
            }

            // ---- 取字框里那档「引擎选择」也得真的通到这条路上 ----
            // 上面设的环境变量比设置里的选择强，所以先清掉才测得到 Ocr.Engine 本身。
            // 换引擎之后必须 Reconfigure()（清掉上一次的探测结论），这两条一起验。
            Environment.SetEnvironmentVariable("SNAPWHEEL_OCR", null);
            Ocr.Engine = "native";
            Ocr.Reconfigure();
            Check("设置里选「本地组件」时本地引擎真的被用上", Ocr.Available, "Why=" + Ocr.Why);
            try
            {
                using (Bitmap b = Sheet()) text = Ocr.Recognize(b, out err);
            }
            catch (Exception ex) { err = ex.Message; }
            Check("设置里选「本地组件」时照样认得出这张图",
                  !string.IsNullOrEmpty(text) && text.Contains("1234"),
                  text == null ? "error=" + err : "实际：" + text);

            Console.WriteLine();
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            Environment.ExitCode = fail == 0 ? 0 : 1;
            return fail == 0 ? 0 : 1;
        }
    }
}
