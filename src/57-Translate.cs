using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace SnapWheel
{
    // ==================== 翻译（0.6.0：重做引擎链 + 去机翻腔） ====================
    // 为什么换掉原来那套：原来只有 MyMemory 一个源（它本质是"翻译记忆库"，质量参差 + 老限流），
    // 用户原话是「翻译后机翻过于严重」。2026-09-14 在本机实测同一句英文：
    //   原文    Failed to load the resource bundle. Please make sure the application is not
    //           running in compatibility mode.
    //   MyMemory 加载资源捆绑包失败。请确保应用程序未在兼容模式下运行。      <- 生硬
    //   有道      加载资源包失败。请确保应用程序没有在兼容模式下运行。        <- 明显自然
    // 同时实测：腾讯 transmart 返回空、Google gtx 直接 429（国内不通）、
    // 豆包/DeepSeek 的官方 API 没有免 key 的（都得注册 key）——
    // 所以策略是三层：
    //   ① 用户自己在设置里填的 OpenAI 兼容接口（URL + key + 模型名）——DeepSeek / 豆包 / 通义 / 本地 Ollama 都兼容。
    //      填了就用它：质量最好，也真正消掉机翻腔。没填就跳过，不打扰。
    //   ② 有道 aidemo：**免费、无 key、国内直连**，质量明显好于 MyMemory —— 默认走这层。
    //   ③ MyMemory：保底（境外、可能不稳，但聊胜于无）。
    // 三层都失败才报错，并且把**最有说服力的那个原因**告诉用户（配置问题优先于网络问题）。
    //
    // 另外：换引擎只能改善，不能根除"腔调"。所以最后再过一道 Tone()：
    // 中英标点归位、中文之间多余空格删掉、行首尾空格与多余空行清理。
    // 这一步刻意做得**很保守** —— 绝不碰版本号/小数/网址里的点（v0.5.3、1.5、http://a.b 都原样）。
    static class Translate
    {
        const int MaxChunk = 420;          // 单次请求的长度上限，长文切段
        const int TimeoutMs = 15000;       // 免费接口一条的时限（长文要发好几次）
        const int LlmTimeoutMs = 30000;    // 大模型慢一些，单独给宽一点

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
            bool toChinese = (dst != "en");

            StringBuilder outp = new StringBuilder();
            string[] chunks = Split(text, MaxChunk);
            // 配置只读一次就够：One() 里每段都去 Load() 会把 settings.ini 反复读好几遍（长文切段多）
            Settings st = null;
            try { st = Settings.Load(); } catch { st = null; }
            int empty = 0;
            for (int i = 0; i < chunks.Length; i++)
            {
                string one = One(chunks[i], src, dst, st, out error);
                if (one == null) return null;
                one = Tone(one, toChinese).Trim();
                if (one.Length == 0) { empty++; continue; }
                if (outp.Length > 0) outp.Append('\n');       // 分段译完拼回去，一段一行
                outp.Append(one);
            }
            if (outp.Length == 0)
            {
                error = empty > 0 ? "接口没返回译文（多半是被限流了），过一会儿再试" : "没有要翻译的文字";
                return null;
            }
            return outp.ToString();
        }

        static string[] Split(string s, int max)
        {
            if (s.Length <= max) return new string[] { s };
            List<string> parts = new List<string>();
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

        // ============================ 引擎链 ============================
        // 一段文字：按 ① LLM ② 有道 ③ MyMemory 的顺序试，谁先成功用谁。
        // 注意：① 是用户自己填的，**填了但失败不该让功能整个不可用** —— 记下原因继续往下退，
        // 只有当后面也全失败时，才把最先失败的那个原因（配置问题）报出来。
        static string One(string text, string src, string dst, Settings st, out string error)
        {
            error = null;

            if (st != null && !string.IsNullOrEmpty(st.LlmUrl) && st.LlmUrl.Trim().Length > 0)
            {
                string e1;
                string r1 = OneLlm(text, src, dst, st, out e1);
                if (r1 != null) return r1;
                error = e1;
            }

            string e2;
            string r2 = OneYoudao(text, src, dst, out e2);
            if (r2 != null) return r2;
            if (string.IsNullOrEmpty(error)) error = e2;

            string e3;
            string r3 = OneMyMemory(text, src, dst, out e3);
            if (r3 != null) return r3;
            if (string.IsNullOrEmpty(error)) error = e3;

            return null;
        }

        static void Tls()
        {
            // .NET Framework 默认只肯用 TLS 1.0/SSL3，现代接口一律要 TLS 1.2 ——
            // 不设这句就报"未能创建 SSL/TLS 安全通道"（实测就是这个错）
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

        // ---------------- ① 大模型（OpenAI 兼容：DeepSeek / 豆包 / 通义 / Ollama 都能用） ----------------
        static string OneLlm(string text, string src, string dst, Settings st, out string error)
        {
            error = null;
            try
            {
                Tls();
                string url = st.LlmUrl.Trim();
                if (url.Length == 0) { error = "没填翻译接口地址"; return null; }
                // 允许只填到 /v1，剩下那截自动补；填全了就用填的
                if (url.IndexOf("/chat/completions", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    if (url.EndsWith("/")) url = url.Substring(0, url.Length - 1);
                    url += "/chat/completions";
                }
                string want = (dst == "en") ? "英文" : "简体中文";
                string sys = "你是翻译引擎。只输出译文本身：不要解释、不要引号、不要 Markdown 标记。"
                           + "严格保持原文的换行与段落结构，不要合并或增删句子。";
                string usr = "把下面的内容翻译成" + want + "：\n" + text;

                StringBuilder body = new StringBuilder();
                body.Append("{\"model\":\"").Append(JsonEsc(st.LlmModel)).Append("\"");
                body.Append(",\"temperature\":0.2,\"stream\":false,\"messages\":[");
                body.Append("{\"role\":\"system\",\"content\":\"").Append(JsonEsc(sys)).Append("\"},");
                body.Append("{\"role\":\"user\",\"content\":\"").Append(JsonEsc(usr)).Append("\"}]}");

                byte[] data = Encoding.UTF8.GetBytes(body.ToString());
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = LlmTimeoutMs;
                req.ReadWriteTimeout = LlmTimeoutMs;
                req.UserAgent = "SnapWheel/" + AppInfo.Version;
                if (!string.IsNullOrEmpty(st.LlmKey))
                    req.Headers["Authorization"] = "Bearer " + st.LlmKey.Trim();
                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    string t = ExtractField(json, "content");
                    if (t == null)
                    {
                        error = "翻译接口没按 OpenAI 格式返回（该填 /v1/chat/completions 那种地址）";
                        return null;
                    }
                    return t;
                }
            }
            catch (WebException wex)
            {
                HttpWebResponse hr = wex.Response as HttpWebResponse;
                string code = hr != null ? ("HTTP " + (int)hr.StatusCode) : (wex.Status == WebExceptionStatus.Timeout ? "超时" : wex.Message);
                error = "自填翻译接口连不上（" + code + "）——检查地址 / key / 模型名";
                return null;
            }
            catch (Exception ex) { error = "自填翻译接口失败：" + ex.Message; return null; }
        }

        // ---------------- ② 有道（免费、无 key、国内直连） ----------------
        static string OneYoudao(string text, string src, string dst, out string error)
        {
            error = null;
            try
            {
                Tls();
                string body = "q=" + Uri.EscapeDataString(text) + "&from=" + YdLang(src) + "&to=" + YdLang(dst);
                byte[] data = Encoding.UTF8.GetBytes(body);
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://aidemo.youdao.com/trans");
                req.Method = "POST";
                req.ContentType = "application/x-www-form-urlencoded";
                req.Timeout = TimeoutMs;
                req.ReadWriteTimeout = TimeoutMs;
                req.UserAgent = "SnapWheel/" + AppInfo.Version;
                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    return ReadYoudao(json, dst, out error);
                }
            }
            catch (WebException wex)
            {
                error = "免费翻译接口连不上：" + (wex.Status == WebExceptionStatus.Timeout ? "超时" : wex.Message);
                return null;
            }
            catch (Exception ex) { error = "翻译失败：" + ex.Message; return null; }
        }

        // MyMemory 用 zh-CN，有道用 zh-CHS —— 别混用（混了有道会把中文当未知语言）
        static string YdLang(string code)
        {
            if (code == "zh-CN" || code == "zh" || code == "zh-Hans") return "zh-CHS";
            if (code == "zh-TW" || code == "zh-Hant") return "zh-CHT";
            return code;
        }

        // 有道的返回长这样（字段顺序不保证）：
        //   {"tSpeakUrl":"...","requestId":"...","query":"原文","translation":["译文"],"mTerminalDict":{...},...}
        // 解析要点：
        //   · 取**最后一个** "translation" —— 用户原文里万一带这个词，它出现在更早的 query 字段里，取最后一个才不会读错；
        //   · translation 是**数组**，可能按句切分成多项，全都要（中文之间不加空格、英文之间加空格）。
        internal static string ReadYoudao(string json, string dst, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(json)) { error = "翻译接口没有返回内容"; return null; }
            int k = json.LastIndexOf("\"translation\"", StringComparison.Ordinal);
            if (k < 0) { error = "翻译接口返回的内容看不懂（可能被限流了）"; return null; }
            int lb = json.IndexOf('[', k);
            if (lb < 0) { error = "翻译接口返回的内容看不懂"; return null; }
            int rb = json.IndexOf(']', lb);
            if (rb < 0) rb = json.Length;

            List<string> parts = new List<string>();
            int i = lb + 1;
            while (i < rb)
            {
                if (json[i] != '"') { i++; continue; }
                int next;
                string one = ReadJsonString(json, i, out next);
                if (next <= i) break;
                i = next;
                if (one != null) parts.Add(one);
            }
            if (parts.Count == 0) { error = "免费翻译额度用完了（限流）——过一会儿再试"; return null; }

            string sep = (dst == "en") ? " " : "";
            StringBuilder sb = new StringBuilder();
            for (int n = 0; n < parts.Count; n++)
            {
                if (n > 0) sb.Append(sep);
                sb.Append(parts[n]);
            }
            return sb.ToString();
        }

        // ---------------- ③ MyMemory（保底，原来的实现） ----------------
        static string OneMyMemory(string text, string src, string dst, out string error)
        {
            error = null;
            try
            {
                Tls();
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
                    string t = ReadResult(json, out error);
                    if (t == null) return null;
                    return t;
                }
            }
            catch (WebException wex)
            {
                error = "备用翻译接口连不上：" + (wex.Status == WebExceptionStatus.Timeout ? "超时" : wex.Message);
                return null;
            }
            catch (Exception ex) { error = "翻译失败：" + ex.Message; return null; }
        }

        // 从 MyMemory 返回里读结果：成功=译文（可能为空串）；失败=null，并把人话原因写进 error。
        // 几种失败要分开，不然用户看到的永远是同一句"内容看不懂"：
        //   限流（MYMEMORY WARNING / responseDetails 带 LIMIT）-> 说清楚是被限流了
        //   别的错误状态 -> 把接口给的原因原样带出来
        internal static string ReadResult(string json, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(json)) { error = "备用接口没有返回内容"; return null; }

            string t = ExtractField(json, "translatedText");
            string details = ExtractField(json, "responseDetails");
            string status = ExtractRaw(json, "responseStatus");

            bool limited = (t != null && t.IndexOf("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase) >= 0)
                        || (details != null && (details.IndexOf("LIMIT", StringComparison.OrdinalIgnoreCase) >= 0
                                             || details.IndexOf("WARNING", StringComparison.OrdinalIgnoreCase) >= 0));
            if (limited)
            {
                error = "免费翻译额度用完了（MyMemory 限流）——过一会儿再试";
                return null;
            }
            if (!string.IsNullOrEmpty(status) && status != "200")
            {
                error = "备用翻译接口报错：" + (string.IsNullOrEmpty(details) ? status : details);
                return null;
            }
            if (t == null) { error = "备用接口返回的内容看不懂（可能被限流了）"; return null; }
            return t.Trim();
        }

        // ============================ 去机翻腔（保守后处理） ============================
        // 只做四件事，都是"引擎不该做但经常不做"的收尾：
        //   ① 去掉引擎自己带回来的包裹引号（有时它会很客气地把译文用引号括起来）
        //   ② 翻成中文时把"被中文字夹着"的半角标点改成全角 —— **绝不动版本号/小数/网址里的点**
        //   ③ 中文与中文之间的半角空格删掉（机翻常见："加载 资源包 失败"）
        //   ④ 行首尾空格、连续 3 个以上空行清理
        internal static string Tone(string t, bool toChinese)
        {
            if (string.IsNullOrEmpty(t)) return t;
            t = t.Trim();

            // ① 整段被一对引号包着才脱 —— 只脱最外层，里面本来就有引号的不动
            if (t.Length >= 2)
            {
                char a = t[0], b = t[t.Length - 1];
                if ((a == '"' && b == '"') || (a == '“' && b == '”') || (a == '\'' && b == '\''))
                    t = t.Substring(1, t.Length - 2).Trim();
            }

            if (toChinese) t = PunctToCjk(t);
            t = TightenCjk(t);
            t = Tidy(t);
            return t;
        }

        // 半角 -> 全角：只有"前一个字是中文、后一个字不是数字/字母"时才换。
        // 这样 v0.5.3 / 3.14 / api.example.com / e.g. 里的点全都原样保留。
        static string PunctToCjk(string s)
        {
            StringBuilder o = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                char n = (i + 1 < s.Length) ? s[i + 1] : '\0';
                bool isP = (c == ',' || c == '.' || c == '?' || c == '!' || c == ':' || c == ';');
                if (isP && o.Length > 0 && IsCjk(o[o.Length - 1]))
                {
                    bool nextBad = (n >= '0' && n <= '9') || (n >= 'a' && n <= 'z') || (n >= 'A' && n <= 'Z');
                    if (!nextBad)
                    {
                        o.Append(c == ',' ? '，' : c == '.' ? '。' : c == '?' ? '？' : c == '!' ? '！' : c == ':' ? '：' : '；');
                        continue;
                    }
                }
                o.Append(c);
            }
            return o.ToString();
        }

        // 中文之间的半角空格删掉；顺便把全角空格当半角看
        static string TightenCjk(string s)
        {
            StringBuilder o = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '\u3000')
                {
                    char prev = o.Length > 0 ? o[o.Length - 1] : '\0';
                    char next = (i + 1 < s.Length) ? s[i + 1] : '\0';
                    if (IsCjk(prev) && IsCjk(next)) continue;      // 中文 中 文 -> 删掉这个空格
                }
                o.Append(c);
            }
            return o.ToString();
        }

        // 行尾空格 + 连续空行压缩
        static string Tidy(string s)
        {
            string[] lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            StringBuilder o = new StringBuilder(s.Length);
            int blank = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string ln = lines[i].TrimEnd();
                if (ln.Trim().Length == 0)
                {
                    blank++;
                    if (blank > 1) continue;                      // 最多留一个空行
                }
                else blank = 0;
                if (o.Length > 0) o.Append('\n');
                o.Append(ln);
            }
            return o.ToString().Trim();
        }

        // ============================ JSON 小工具（不引 JSON 库） ============================
        // 读一个"可能是字符串也可能是数字"的字段（MyMemory 的 responseStatus 是不带引号的 200）
        internal static string ExtractRaw(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int k = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (k < 0) return null;
            int colon = json.IndexOf(':', k);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            int start = i;
            while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '.' || json[i] == '-')) i++;
            return i > start ? json.Substring(start, i - start) : null;
        }

        // 从 {"responseData":{"translatedText":"..."}} 里把那个字段抠出来
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
            int next;
            return ReadJsonString(json, i, out next);
        }

        // 从 s[i]（必须是引号）读一个 JSON 字符串，next = 引号之后的下一格；解转义（\n \uXXXX ...）
        static string ReadJsonString(string s, int i, out int next)
        {
            next = i;
            if (i >= s.Length || s[i] != '"') return null;
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    i += 2;
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 3 < s.Length)
                            {
                                int code;
                                if (int.TryParse(s.Substring(i, 4), System.Globalization.NumberStyles.HexNumber, null, out code))
                                { sb.Append((char)code); i += 4; }
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                    continue;
                }
                if (c == '"') { i++; break; }
                sb.Append(c);
                i++;
            }
            next = i;
            return sb.ToString();
        }

        // 拼 JSON 请求体时用：引号/反斜杠/换行必须转义，否则大模型接口直接 400
        static string JsonEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder o = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': o.Append("\\\""); break;
                    case '\\': o.Append("\\\\"); break;
                    case '\n': o.Append("\\n"); break;
                    case '\r': o.Append("\\r"); break;
                    case '\t': o.Append("\\t"); break;
                    default:
                        if (c < ' ') o.Append("\\u").Append(((int)c).ToString("x4"));
                        else o.Append(c);
                        break;
                }
            }
            return o.ToString();
        }
    }
}
