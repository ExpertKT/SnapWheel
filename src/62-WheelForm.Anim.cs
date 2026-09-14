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
    partial class WheelForm
    {

        RectangleF NubOutRect()
        {
            SizeF ls = LogicalSize();
            float cy = (Sy() > 0) ? NubDistOut() : ls.Height - NubDistOut();
            float x = (Sx() > 0) ? 0f : ls.Width - NubThick;
            return new RectangleF(x, cy - NubLong / 2f, NubThick, NubLong);
        }


        RectangleF NubInRect()
        {
            SizeF ls = LogicalSize();
            float cx = (Sx() > 0) ? NubDistIn() : ls.Width - NubDistIn();
            float y = (Sy() > 0) ? 0f : ls.Height - NubThick;
            return new RectangleF(cx - NubLong / 2f, y, NubLong, NubThick);
        }


        // 一整趟 0 -> 1 的时间基准（不含速度设置）
        float RingUnitDurBase()
        {
            // 固定时长。早先是 0.82 + 0.05*图片数 —— 图一多收起就明显更慢，
            // 但展开/收起本来是一段固定几何过渡，跟盘里存了几张图没关系。
            return 1.0f;
        }


        // 一趟 0->1 的时长：展开和收起各用各的速度百分比（越大越快）
        float RingUnitDur() { return RingUnitDur(false); }

        float RingUnitDur(bool collapsing)
        {
            int sp = collapsing ? _settings.CollapseSpeed : _settings.ExpandSpeed;
            if (sp < 40) sp = 40;
            if (sp > 250) sp = 250;
            return RingUnitDurBase() * AnimK() * (100f / sp);
        }


        // 展开（彩虹拉出）；fast=true 用于截图流程，快一点
        public void ExpandWheel() { ExpandWheel(false); }

        public void ExpandWheel(bool fast)
        {
            // _collapsing 时 _collapsed 还是 false（收完才置位），所以这里必须把"正在收起"排除掉：
            // 否则收起动画走到一半时点展开会被当成"已经展开了"直接 return ——
            // 用户看到的就是"收起后半夜没法立马展开"（点了没反应，动画继续缩回去）。
            if (IsExpanded && Visible && !_collapsing) { _lastActive = DateTime.Now; return; }
            if (_settings.IntroAnim)
            {
                StartIntro();
                if (fast) { _introAt = DateTime.Now; _introDur = RingUnitDur(false) * 0.55f; }
            }
            else { _collapsed = false; _collapsing = false; _intro = false; _introT = 1f; ShowWheel(); }
            _lastActive = DateTime.Now;
        }


        // 开机直接进入收起态：窗口显示出来，但只有贴边那个小把手
        public void StartCollapsed()
        {
            _collapsed = true;
            _collapsing = false;
            _intro = false;
            _introT = 0f;
            _nubAppearT = 0f;                      // 把手从屏幕边滑出来，不要"啪"地出现
            _nubAppearAt = DateTime.Now;
            // 首次运行：把手旁边自动亮一次"点我展开"，让新用户知道它是干嘛的
            if (!_settings.NubHintDone)
            {
                _firstRunHintUntil = DateTime.Now.AddSeconds(14);
                _settings.NubHintDone = true;
            }
            // 后台抓玻璃底（同步抓一次要 12~16ms，会正好把"点开轮盘"那一下顶慢；
            // 窗口设了 WDA_EXCLUDEFROMCAPTURE，显示中抓也不会拍到轮盘自己）
            RequestBackdropAsync();
            _show = 1f; _targetShow = 1f; _showAnimating = false;
            _rendered = false;
            Show();
            Render();
            _lastActive = DateTime.Now;
        }


        // 收起（彩虹缩回）—— 收起态没开的话就退回原来的"直接隐藏"
        public void CollapseWheel() { CollapseWheel(false); }

        public void CollapseWheel(bool fast)
        {
            if (!_settings.CollapseMode) { HideWheel(); return; }
            if (_collapsed) return;
            if (!Visible) { _collapsed = true; _collapsing = false; _introT = 0f; _intro = false; _show = 1f; _targetShow = 1f; _showAnimating = false; _collapsedAt = DateTime.Now; RequestBackdropAsync(); Show(); Render(); return; }
            _collapsing = true;
            _intro = true;
            _ringFrom = _introT;              // 从"现在伸到哪"开始往回收，不跳到完全展开
            _ringTo = 0f;
            _introAt = DateTime.Now;
            _introDur = RingUnitDur(true) * (fast ? 0.14f : 1f);   // 收起用「收起速度」
            _keyDown = false; _menuOpen = false; _menuT = 0f; _enlarged = -1; _hover = -1;
            _lastActive = DateTime.Now;
            Render();                       // 立刻出一帧，点了就能看到动
        }


        // 界面上"关掉轮盘"的动作：收起态开着就收起，否则完全隐藏
        public void DismissWheel()
        {
            if (_settings.CollapseMode) CollapseWheel();
            else HideWheel();
        }


        public void ShowWheel()
        {
            if (!Visible) { RequestBackdropAsync(); _show = 0f; _rendered = false; Show(); }
            SetShow(1f);
            _lastActive = DateTime.Now;
            Render();
        }


        // 软件刚启动时用它：带开启动画地显示出来
        public void ShowWheelWithIntro()
        {
            if (_settings.IntroAnim) { StartIntro(); }
            else ShowWheel();
        }


        // 开启动画：整条环像彩虹一样扫出来，图片一张张沿弧线滑落，万能键/按钮/文字从屏幕外滑入并渐显
        public void StartIntro()
        {
            if (!Visible) { RequestBackdropAsync(); _show = 0f; _rendered = false; Show(); }
            _show = 1f; _targetShow = 1f; _showAnimating = false;
            _collapsed = false; _collapsing = false;
            _intro = true;
            _ringFrom = _introT;
            _ringTo = 1f;
            _introAt = DateTime.Now;
            _introDur = RingUnitDur();
            _scales.Clear(); _enterT0.Clear();
            for (int i = 0; i < _store.Items.Count; i++)
                _enterT0[_store.Items[i]] = DateTime.Now.AddSeconds(0.45 + i * 0.13);
            Render();
        }


        // 开启动画里每个元素的进度（1 = 完全就位）；delay 越大越晚出场。
        // 收起时 _introT 是往回走的，同一个公式自然就变成倒放。
        //
        // delay 是 0..1 的"相对出场顺序"，不是秒！原来写的是秒（最大 0.72s + 0.55s 过渡），
        // 总时长从 2.15s 压到 1.07s 后，后面的元素根本走不到位 ——
        // 动画一结束就从半路"啪"地闪到最终位置（按钮、计数胶囊就是这么闪的）。
        float IntroP(float delay)
        {
            if (_collapsed) return 0f;
            if (!_intro) return 1f;
            const float span = 0.46f;                  // 单个元素自己的过渡长度（占总时长比例）
            float start = delay * (1f - span);
            float x = (_introT - start) / span;
            if (x <= 0f) return 0f;
            if (x >= 1f) return 1f;
            // 每个元素自己的过渡曲线。
            // 原来这里是 easeOutCubic（1-(1-x)^3）：一进窗口就窜出去 —— 按 66 帧算，
            // 头两帧每帧要走十几像素，后面几帧几乎不动，看起来就是"啪一下到位、然后爬"；
            // 而图片是缓缓滑进来的，于是万能键/按钮/胶囊这几样就显得"帧率更低"。
            // 换成 smoothstep：**窗口和总时长一个字没动**，只把头尾放缓、把位移摊匀。
            return x * x * (3f - 2f * x);
        }


        // 从屏幕外滑进来的位移（朝角落方向，也就是朝屏幕外）
        PointF IntroShift(float p)
        {
            if (!_intro && !_collapsed) return new PointF(0f, 0f);
            float off = (1f - p) * 110f;
            return new PointF((Sx() > 0 ? -off : off), (Sy() > 0 ? -off : off));
        }


        // 收起过程中图片的淡出因子（1 -> 0，在环完全缩回之前就淡完）
        float CollapseCardP()
        {
            if (_collapsed) return 0f;
            if (!_intro || !_collapsing) return 1f;
            float v = _introT / 0.55f;
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }



        public void HideWheel() { SetShow(0f); }
        public void ToggleWheel()
        {
            if (_collapsed) ShowWheelWithIntro();          // 收起态 -> 拉出来（带彩虹动画）
            else if (_targetShow > 0.5f) DismissWheel();   // 展开中 -> 收起（或隐藏）
            else ShowWheelWithIntro();
        }


        void SetShow(float target)
        {
            if (Math.Abs(_targetShow - target) < 0.001f && _showAnimating == false) { _targetShow = target; return; }
            _showFrom = _show;
            _showT0 = DateTime.Now;
            _targetShow = target;
            _showAnimating = true;
        }


        // 这一帧算错了不该让整个程序挂掉（Timer 里抛异常会直接弹崩溃框）
        void AnimTick(object sender, EventArgs e)
        {
            try { AnimTickCore(); }
            catch (Exception ex) { try { Err.Log("wheel.AnimTick", ex); } catch { } }
        }


        void AnimTickCore()
        {
            bool need = false;
            bool hide = _targetShow < _show;
            if (_showAnimating)
            {
                float dur = (hide ? 0.50f : 0.30f) * AnimK();      // 速度设置会一起缩放
                float t = (float)((DateTime.Now - _showT0).TotalSeconds / dur);
                if (t >= 1f) { t = 1f; _showAnimating = false; }
                float ease = t * t * (3f - 2f * t);       // smoothstep
                _show = _showFrom + (_targetShow - _showFrom) * ease;
                need = true;
            }
            else if (_show != _targetShow) { _show = _targetShow; need = true; }

            float dop = _targetOffset - _offset;
            bool scrolling = Math.Abs(dop) > 0.02f;
            if (scrolling) { _offset += dop * 0.20f; need = true; }
            else if (_offset != _targetOffset) { _offset = _targetOffset; need = true; }

            // while the arc is sliding, freeze hover so cards don't grow/shrink alternately (no tremble)
            if (scrolling)
            {
                if (_hover != -1) { _hover = -1; need = true; }
            }
            else if (_show > 0.6f && Visible)
            {
                Point cp = ToLogicalPt(PointToClient(Cursor.Position));
                int hh = HitTest(cp);
                bool gh = GearButtonRect().Contains(cp);
                bool ch = CloseButtonRect().Contains(cp);
                bool sH = ShootButtonRect().Contains(cp);
                bool kh = KeyRect().Width > 8 && KeyRect().Contains(cp);
                bool nh = NamePillRect().Contains(cp);
                // 悬停变化这一帧**必须画**（省电模式下也不许延到下一 tick）：置 _forceDraw
                if (hh != _hover) { _hover = hh; need = true; _forceDraw = true; }
                if (gh != _gearHover) { _gearHover = gh; need = true; _forceDraw = true; }
                if (ch != _closeHover) { _closeHover = ch; need = true; _forceDraw = true; }
                if (sH != _shootHover) { _shootHover = sH; need = true; _forceDraw = true; }
                if (kh != _keyHover) { _keyHover = kh; need = true; _forceDraw = true; }
                if (nh != _nameHover) { _nameHover = nh; need = true; _forceDraw = true; }
            }


            if (_gearHold) { Point cp4 = ToLogicalPt(PointToClient(Cursor.Position)); if (!GearButtonRect().Contains(cp4)) _gearHold = false; }
            if (_shootHold) { Point cp5 = ToLogicalPt(PointToClient(Cursor.Position)); if (!ShootButtonRect().Contains(cp5)) _shootHold = false; }

            if (_holdIndex >= 0 && _maybeDrag && _enlarged < 0)
                if ((DateTime.Now - _holdStart).TotalMilliseconds > 300) { _enlarged = _holdIndex; need = true; }

            // per-item scale: hover 1.36x, hold-to-peek 2.4x - ONE animation, so it can never desync
            for (int i = 0; i < _store.Items.Count; i++)
            {
                float tgt;
                if (_store.Items[i] == _dragOutItem) tgt = 0f;
                else if (i == _enlarged) tgt = PeekScale;                else if (i == _hover) tgt = HoverScale;
                else tgt = 1f;
                float cur;
                if (!_scales.TryGetValue(i, out cur)) cur = 1f;
                float rate = (tgt > 1.5f || cur > 1.5f) ? 0.16f : 0.19f;   // peek moves a little slower
                rate = Math.Max(0.05f, Math.Min(0.5f, rate / AnimK()));
                if (Math.Abs(cur - tgt) > 0.003f) { _scales[i] = cur + (tgt - cur) * rate; need = true; }
                else if (cur != tgt) { _scales[i] = tgt; need = true; }
            }
            // hold-to-peek fade state kept in sync with the scale animation (single source of truth)
            if (_enlarged >= 0) _peekIndex = _enlarged;
            if (_enlarged < 0 && _peekIndex >= 0)
            {
                float cur; if (!_scales.TryGetValue(_peekIndex, out cur)) cur = 1f;
                if (cur <= 1.02f) _peekIndex = -1;
            }
            if (_enlarged >= 0 || _peekIndex >= 0) need = true;

            // 删除动画：必须独立判断（原来写成 else if，挂在"放大预览"后面 ——
            // 放大预览一开着动画就不推进，于是"有动画但没删掉"）
            if (_deletingItem != null)
            {
                _deleteProg += 0.055f;                     // ~0.28s collapse
                need = true;
                if (_deleteProg >= 1f)
                {
                    StoreItem victim = _deletingItem;
                    _deletingItem = null;
                // 0.6.0：删除后，后面每张图的下标都前移 1 —— 视口也必须跟着挪一格，
                // 否则"上面的缩略图会瞬间下移一格"（用户反馈的瞬移、没有过渡）。
                // 判据：删的是视口最下面那张或更靠上的，才需要 -1（可见内容位置保持不变）；
                // 删的是视口下方的图，可见范围本来就不受影响。用 _targetOffset 而不是 _offset，
                // 这样位移是**动画过渡**过去的，不是跳过去。
                if (_delIdx >= 0 && _delIdx <= (int)Math.Round(_targetOffset))
                    _targetOffset = Math.Max(MinOffset(), _targetOffset - 1f);
                _delIdx = -1;
                    _deleteProg = 0f;
                    RemoveItem(victim, true);
                }
            }

            // keep animating while a freshly captured image is still sliding in / a delete is running
            foreach (KeyValuePair<StoreItem, DateTime> kv in _enterT0)
                if ((DateTime.Now - kv.Value).TotalSeconds < 0.5) { need = true; break; }
            if (_deletingItem != null) need = true;
            if (_phiShift != 0f)
            {
                _phiShift *= 0.80f;
                if (Math.Abs(_phiShift) < 0.002f) _phiShift = 0f;
                need = true;
            }

            // 提示条（"已加入 N 张图片"）淡入淡出
            if (_toast.Length > 0)
            {
                if ((DateTime.Now - _toastAt).TotalSeconds > 2.6f) _toast = "";
                else need = true;
            }

            // 开启动画进度（收起时 _introT 往回走，同一个公式就是倒放）
            if (_intro)
            {
                // 时间按"这次要走多远"算：走得近就快，走完就停 —— 展开/收起共用，随时可反向
                float span = Math.Abs(_ringTo - _ringFrom);
                float dur = Math.Max(0.10f, _introDur * span);
                float t = (float)((DateTime.Now - _introAt).TotalSeconds / dur);
                if (t >= 1f) { t = 1f; _intro = false; }
                // 主进度走"线性"：这样展开和收起在时间上是对称的
                // （之前用 easeOutCubic 作用在主进度上，展开时动作集中在开头、
                //   收起时集中在结尾，于是"展开比收起快太多"）
                _introT = _ringFrom + (_ringTo - _ringFrom) * t;
                if (!_intro)
                {
                    _introT = _ringTo;
                    if (_collapsing) { _collapsed = true; _collapsing = false; _collapsedAt = DateTime.Now; }   // 收完了：进入收起态
                }
                need = true;
            }

            // 把手出现动画（启动时）
            if (_nubAppearT < 1f)
            {
                _nubAppearT += (float)((DateTime.Now - _nubAppearAt).TotalSeconds / 0.55f);
                if (_nubAppearT >= 1f) _nubAppearT = 1f;
                _nubAppearAt = DateTime.Now;
                need = true;
            }

            // 关闭键长按：画一条红色进度环，按住 0.65s 变满 -> 变红，松手退出。
            // 同时兜住"鼠标已经松开但 MouseUp 没收到"的情况（按住时轻微移动不该取消）。
            if (_closeHold)
            {
                // 后悔机制：长按期间把鼠标挪开就作废（稍微留点余量，手抖不算离开）
                if (!CursorOverCloseButton()) CancelCloseHold();
            }
            if (_closeHold)
            {
                float p = (float)Math.Min(1.0, (DateTime.Now - _closeDownAt).TotalMilliseconds / 650.0);
                if (Math.Abs(p - _closeHoldP) > 0.004f) { _closeHoldP = p; need = true; }
                if (p >= 1f && !_closeLong) { _closeLong = true; need = true; }
                need = true;                     // 进度环一直动
            }
            else if (_closeHoldP > 0.004f)
            {
                // 作废/松手之后，红色是"渐变退回去"的，不是瞬间复位
                _closeHoldP += (0f - _closeHoldP) * 0.20f;
                if (_closeHoldP < 0.004f) _closeHoldP = 0f;
                need = true;
            }

            // 圆钮按下反馈 + 延迟执行
            {
                float cd = (_closePend || _closeHold) ? 1f : 0f;
                float gd = (_gearPend || _gearHold) ? 1f : 0f;
                float sd = (_shootPend || _shootHold) ? 1f : 0f;
                if (Math.Abs(_closeDown - cd) > 0.01f) { _closeDown += (cd - _closeDown) * 0.35f; need = true; }
                if (Math.Abs(_gearDown - gd) > 0.01f) { _gearDown += (gd - _gearDown) * 0.35f; need = true; }
                if (Math.Abs(_shootDown - sd) > 0.01f) { _shootDown += (sd - _shootDown) * 0.35f; need = true; }
                if (_pendingBtn.Length > 0 && (DateTime.Now - _pendingAt).TotalMilliseconds > 110)
                {
                    string b = _pendingBtn; _pendingBtn = "";
                    _closePend = _gearPend = _shootPend = false;
                    if (b == "close") DismissWheel();
                    else if (b == "gear") { if (SettingsRequested != null) SettingsRequested(this, EventArgs.Empty); }
                    else if (b == "shoot") { if (CaptureRequested != null) CaptureRequested(this, EventArgs.Empty); }
                    need = true;
                }
                if (_closeDown > 0.01f || _gearDown > 0.01f || _shootDown > 0.01f) need = true;
            }

            // 把手悬停反馈
            {
                bool wantOut = false, wantIn = false;
                if (Visible && _show > 0.6f)
                {
                    Point hp = ToLogicalPt(PointToClient(Cursor.Position));
                    if (_collapsed) wantOut = NubOutRect().Contains(hp);
                    else if (NubSingleMode()) wantOut = NubOutRect().Contains(hp);
                    else if (!_intro) wantIn = NubInRect().Contains(hp);
                }
                if (wantOut != _nubOutHover) { _nubOutHover = wantOut; need = true; }
                if (wantIn != _nubInHover) { _nubInHover = wantIn; need = true; }
                float wantH = (wantOut || wantIn) ? 1f : 0f;
                if (Math.Abs(_nubHov - wantH) > 0.006f) { _nubHov += (wantH - _nubHov) * 0.24f; need = true; }
                else if (_nubHov != wantH) { _nubHov = wantH; need = true; }
                if (_nubHov > 0.01f) need = true;

                // 把手用途提示（"点我展开/收起"）：悬停时亮；首次运行的头 14 秒也自动亮一次
                bool wantHint = wantOut || wantIn || DateTime.Now < _firstRunHintUntil;
                float wantHT = wantHint ? 1f : 0f;
                if (Math.Abs(_nubHintT - wantHT) > 0.006f) { _nubHintT += (wantHT - _nubHintT) * 0.18f; need = true; }
                else if (_nubHintT != wantHT) { _nubHintT = wantHT; need = true; }
                if (_nubHintT > 0.01f) need = true;
            }

            // 后台抓好的玻璃底：在 UI 线程这里换上。
            // 但动画期间不换（尤其是展开/收起那趟）—— 换底会强制整窗重绘，正好卡在动画中间，看着就"顿"。
            // 悬停/交互期间也不换：换底会强制整窗重绘，正好把交互那一下顶慢（"慢半拍"）
            if (!_intro && _hover < 0 && !_closeHover && !_gearHover && !_shootHover && !_keyHover)
                ApplyPendingBackdrop();

            // 背景交叉淡入（换背景时玻璃颜色渐变，不跳）
            if (_backdropOld != null && _backdropFade < 1f)
            {
                _backdropFade += (float)((DateTime.Now - _backdropFadeAt).TotalSeconds / 0.38f);
                if (_backdropFade >= 1f) { _backdropFade = 1f; try { _backdropOld.Dispose(); } catch { } _backdropOld = null; }
                _backdropFadeAt = DateTime.Now;
                need = true;
            }

            // 玻璃底定时重抓：轮盘一直挂着也不会"糊的是半小时前的桌面"
            // （窗口已设置 WDA_EXCLUDEFROMCAPTURE，抓屏不会把轮盘自己拍进去，所以显示中也能抓）
            // 省电模式（电池上）：**只**停"每 3.5 秒的定时重抓"这一档 —— 轮盘刚显示 / 切盘 / 拖放 / 隐藏时
            // 那几处 RequestBackdropAsync() 照旧（否则玻璃底色会缺）。见 12-Power.cs。
            if (_settings.GlassRefresh && Visible && _show > 0.99f && !_intro && !PowerSaveOn())
            {
                if ((DateTime.Now - _backdropAt).TotalSeconds > 3.5)
                {
                    _backdropAt = DateTime.Now;
                    if (_hover < 0 && !_menuOpen && _enlarged < 0 && !_dropActive && _dragOutItem == null && _deletingItem == null)
                    {
                        bool hoverAny = _gearHover || _closeHover || _shootHover || _keyHover || _nameHover;
                        // 把手悬停/按钮按下时绝不重抓：抓屏+模糊要几十毫秒，会让人觉得"点了没反应"
                        bool busy = _nubHov > 0.01f || _nubOutHover || _nubInHover
                                    || _closePend || _gearPend || _shootPend
                                    || _closeDown > 0.01f || _gearDown > 0.01f || _shootDown > 0.01f;
                        if (!hoverAny && !_keyDown && !busy)
                        {
                            // 抓屏+模糊（几十毫秒）放到后台线程做，UI 线程下一帧换上去，绝不卡动画
                            RequestBackdropAsync();
                        }
                    }
                }
            }

            // 万能键按下/松开 + 悬停 的弹性反馈
            {
                float kt = _keyDown ? 1f : 0f;
                float cur = _keyT;
                if (Math.Abs(cur - kt) > 0.004f) { _keyT = cur + (kt - cur) * (kt > cur ? 0.35f : 0.22f); need = true; }
                else if (cur != kt) { _keyT = kt; need = true; }

                float kh = _keyHover ? 1f : 0f;
                float curH = _keyHov;
                if (Math.Abs(curH - kh) > 0.006f) { _keyHov = curH + (kh - curH) * 0.24f; need = true; }
                else if (curH != kh) { _keyHov = kh; need = true; }
                if (_keyHov > 0.01f) need = true;         // 悬停时保持刷新，光晕是渐变的
            }

            // 万能键：长按展开圆盘 / 滑动切换
            if (_keyDown)
            {
                bool radial = (_settings.SwitchMode != "swipe");
                if (radial && !_menuOpen && !_delConfirm && (DateTime.Now - _keyDownAt).TotalMilliseconds > KeyMenuDelayMs)
                {
                    _menuOpen = true; _sector = -1; need = true;
                }
                if (_menuOpen && _menuT < 1f) { _menuT += (1f - _menuT) * 0.28f; need = true; }
            }
            else if (_menuT > 0.001f)
            {
                _menuT += (0f - _menuT) * 0.22f;
                if (_menuT < 0.01f) { _menuT = 0f; _menuOpen = false; _sector = -1; }
                need = true;
            }

            // 主题色过渡 + 切换闪光
            Color want = AccentColor();
            if (_accentCur.ToArgb() != want.ToArgb())
            {
                _accentCur = Color.FromArgb(
                    (int)Math.Round(_accentCur.R + (want.R - _accentCur.R) * 0.22f),
                    (int)Math.Round(_accentCur.G + (want.G - _accentCur.G) * 0.22f),
                    (int)Math.Round(_accentCur.B + (want.B - _accentCur.B) * 0.22f));
                need = true;
            }
            if (_switchFlash > 0f) { _switchFlash += (0f - _switchFlash) * 0.16f; if (_switchFlash < 0.01f) _switchFlash = 0f; need = true; }

            // 删除确认态：高亮鼠标所在的半 + 超时自动取消（避免一直挂着）
            if (_delConfirm)
            {
                int want2 = -1;
                if (Visible)
                {
                    Rectangle kr2 = KeyRect();
                    Point cp2 = ToLogicalPt(PointToClient(Cursor.Position));
                    if (kr2.Contains(cp2)) want2 = (cp2.X < kr2.X + kr2.Width / 2f) ? 0 : 1;
                }
                if (want2 != _delHalf) { _delHalf = want2; need = true; }
                if ((DateTime.Now - _delConfirmAt).TotalSeconds > 10) { _delConfirm = false; _delHalf = -1; need = true; }
            }
            else if (_delHalf != -1) { _delHalf = -1; need = true; }

            if (_settings.AutoHide && !_collapsed && _targetShow > 0.5f && _show > 0.99f && !_intro)
                if ((DateTime.Now - _lastActive).TotalSeconds > _settings.AutoHideSeconds) DismissWheel();

            if (_show <= 0.002f && _targetShow <= 0.002f)
            {
                if (Visible) { Hide(); RequestBackdropAsync(); }   // 隐藏后再抓一次，下次显示时玻璃底是新的（后台抓，别卡 UI）
                return;
            }
            // 省电模式（电池上）：**隔一帧才画一次**，但三条硬约束不许破 ——
            //   ① 定时器间隔绝不动（15ms 那个节奏是动画的时基：_deleteProg += 0.055f、_chipsT 平滑都按帧推进，
            //      改间隔它们就整体变慢）；这里只决定"这一帧要不要真去 Render"。
            //   ② **绝不连续两帧不画**：上一帧跳过了，这一帧无论如何都画（_powerSkipped）。
            //   ③ **输入那一帧必画**：悬停/按下/滚轮改变的那一下要立刻看见（_forceDraw），不许延到下一 tick。
            if (PowerSaveOn() && !_powerSkipped && !_forceDraw)
            {
                _powerSkipped = true;                 // 这一帧省掉
                SkipCountForTest++;
            }
            else
            {
                _powerSkipped = false;
                if (need || !_rendered) Render();     // render ONLY when something changed (smooth + cheap)
            }
            _forceDraw = false;
        }


        // 现在是不是"省电生效"：设置开着 **且** 在电池上（Power.OnBattery 内部缓存 5 秒，不会每帧问系统）
        bool PowerSaveOn()
        {
            try { return _settings != null && _settings.PowerSave && Power.OnBattery(); }
            catch { return false; }
        }


        // only a NEWLY captured image slides in; everything else is already in place
        public void MarkNew(StoreItem it)
        {
            if (it == null) return;
            _enterT0[it] = DateTime.Now;
            // 视口要跟到最新那张 —— 否则"刚截的图"可能落在可见弧之外（收起态下 offset 被归零，
            // 第 7 张的 ItemPhi 已经是 1.747，而可见弧上界 _phiMax+0.5 只有 1.766）：
            // 滑入动画其实在弧外跑，等它擦着边跨进来才"啪"地闪现一下 —— 用户报的
            // "缩略图滑进 wheel 突然闪现、动画和位置合不上"就是这个。
            // 剪贴板导入那条路一直是这么做的（OnClipboardChanged / ImportFiles 里都有这一句），
            // 截图这条路以前漏了。
            //
            // 0.5.3：目标值从 Count-1 改成 Count-Slots（最新那张顶在弧**上端**，见 OffsetForNewest），
            // 而且这一步可以在设置里关掉（关 = 保持用户当前滚动位置，不把他正在看的地方拽走）。
            FollowNewest();
        }


        // 收进 / 截进一张新图之后，视口要不要回到"最新那张"。
        // 设置项 `ResetScrollOnCapture`（默认开）关掉时：什么都不做 —— 视口就停在用户当前的位置，
        // 新图照样按 EnterProgress 从上面滑进来，只是不把画面拽走。
        void FollowNewest()
        {
            if (_settings != null && !_settings.ResetScrollOnCapture) return;
            _targetOffset = OffsetForNewest();
        }


        float EnterProgress(int i)
        {
            if (i < 0 || i >= _store.Items.Count) return 1f;
            DateTime t0;
            if (!_enterT0.TryGetValue(_store.Items[i], out t0)) return 1f;
            float d = (float)(DateTime.Now - t0).TotalSeconds;
            if (d <= 0f) return 0f;
            float t = d / (0.42f * AnimK());
            if (t >= 1f) return 1f;
            return t * t * (3f - 2f * t);      // smoothstep -> eased, silky
        }
    }
}
