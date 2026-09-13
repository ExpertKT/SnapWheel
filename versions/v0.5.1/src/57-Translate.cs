using System;
using System.IO;
using System.Net;
using System.Text;

namespace SnapWheel
{
    // 翻译：把 OCR 出来的文字一键翻成中文/英文。
    //
    // 为什么用 MyMemory：它是少数"不要 API key"的接口，实测这台机器能通；
    // Google 那个免费端点国内是 429/连不上。只发一个普通的 HTTPS GET，
    // 不碰系统网络设置、不改代理；失败就把原因原样告诉用户，绝不假装成功。
    static class Translate
    {
        const int MaxChunk = 420;      // 免费接口对单次请求长度有限制，长文切段
        const int TimeoutMs = 9000;

        static bool IsCjk(char c)
        {
            return (c >= 0x3400 && c <= 0x4DBF) || (c >= 0x4E00 && c <= 0x9FFF)
                || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFF00 && c <= 0xFFEF);
        }

        // 中文占三成以上就当它是中文 -> 翻成英文；否则翻成中文
        public static bool LooksChinese(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int cjk = 0, total = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) continue;
                total++;
                if (IsCjk(c)) cjk++;
            }
            return total > 0 && cjk * 10 >= total * 3;
        }

        public static string TargetLabel(string text) { return LooksChinese(text) ? "英文" : "中文"; }

        // 成功返回译文；失败返回 null 并给出人话原因
        public static string Run(string text, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) { error = "没有要翻译的文字"; return null; }
            string src = LooksChinese(text) ? "zh-CN" : "en";
            string dst = LooksChinese(text) ? "en" : "zh-CN";

            StringBuilder outp = new StringBuilder();
            string[] chunks = Split(text, MaxChunk);
            for (int i = 0; i < chunks.Length; i++)
            {
                string one = One(chunks[i], src, dst, out error);
                if (one == null) return null;
                if (outp.Length > 0) outp.Append(LooksChinese(text) ? "\n" : "\n");
                outp.Append(one.Trim());
            }
            return outp.ToString();
        }

        static string[] Split(string s, int max)
        {
            if (s.Length <= max) return new string[] { s };
            System.Collections.Generic.List<string> parts = new System.Collections.Generic.List<string>();
            int start = 0;
            while (start < s.Length)
            {
                int len = Math.Min(max, s.Length - start);
                // 尽量在换行/句号/空格处断开，别把句子切两半
                if (start + len < s.Length)
                {
                    int cut = -1;
                    for (int k = start + len; k > start + max / 2; k--)
                    {
                        char c = s[k - 1];
                        if (c == '\n' || c == '。' || c == '！' || c == '？' || c == '.' || c == '!' || c == '?' || c == ' ') { cut = k; break; }
                    }
                    if (cut > start) len = cut - start;
                }
                parts.Add(s.Substring(start, len));
                start += len;
            }
            return parts.ToArray();
        }

        static string One(string text, string src, string dst, out string error)
        {
            error = null;
            try
            {
                // .NET Framework 默认只肯用 TLS 1.0/SSL3，现代接口一律要求 TLS 1.2 ——
                // 不设这句就会报"未能创建 SSL/TLS 安全通道"（实测就是这个错）
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                string url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text) +
                             "&langpair=" + Uri.EscapeDataString(src) + "%7C" + Uri.EscapeDataString(dst) + "&de=snapwheel@example.com";
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = TimeoutMs;
                req.ReadWriteTimeout = TimeoutMs;
                req.UserAgent = "SnapWheel/" + AppInfo.Version;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    string t = ExtractField(json, "translatedText");
                    if (t == null) { error = "接口返回的内容看不懂（可能被限流了）"; return null; }
                    if (t.IndexOf("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase) >= 0)
                    { error = "免费翻译额度用完了（MyMemory 限流），过一会儿再试"; return null; }
                    return t;
                }
            }
            catch (WebException wex)
            {
                error = "翻译接口连不上：" + (wex.Status == WebExceptionStatus.Timeout ? "超时" : wex.Message);
                return null;
            }
            catch (Exception ex) { error = "翻译失败：" + ex.Message; return null; }
        }

        // 从 {"responseData":{"translatedText":"..."}} 里把那个字段抠出来（不引 JSON 库，
        // 只需要一个字段：先找 key，再按 JSON 字符串规则解转义）
        internal static string ExtractField(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int k = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (k < 0) return null;
            int colon = json.IndexOf(':', k);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return null;
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char n = json[i + 1];
                    i += 2;
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 3 < json.Length)
                            {
                                int code;
                                if (int.TryParse(json.Substring(i, 4), System.Globalization.NumberStyles.HexNumber, null, out code))
                                { sb.Append((char)code); i += 4; }
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
    }
}
