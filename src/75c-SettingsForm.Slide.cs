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
    // 设置窗口的翻页（滑动切换）逻辑（从 75-SettingsForm.cs 拆出来，纯搬移，行为不变）。
    // 这一组是"翻页时把两页各截成位图来滑动"的实现 —— 它和界面构建、和值保存是三件不同的事，
    // 拆开后改动画不会碰到控件构建，改控件也不会碰坏动画。
    partial class SettingsForm
    {
        Bitmap ShotPage(Control pg)
        {
            Bitmap b = new Bitmap(pg.Width, pg.Height, PixelFormat.Format32bppPArgb);
            pg.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
            return b;
        }

        void FreeSlide()
        {
            if (_slideA != null) { try { _slideA.Dispose(); } catch { } _slideA = null; }
            if (_slideB != null) { try { _slideB.Dispose(); } catch { } _slideB = null; }
        }

        void BodyPaint(object o, PaintEventArgs pe)
        {
            if (_slideA != null) pe.Graphics.DrawImageUnscaled(_slideA, _slideAx, 0);
            if (_slideB != null) pe.Graphics.DrawImageUnscaled(_slideB, _slideBx, 0);
        }

        // 用户输入路径（点扇区 / 滚轮）走这里：动画期间直接忽略 —— 这就是"防连点"，
        // 连点不会叠加动画、不会重叠、不会跳变。（把防连点放在输入层，而不是塞进 ShowPage：
        // 塞进 ShowPage 会让"程序性切页"被悄悄吞掉 —— 没消息泵时动画永远走不完，
        // 后面几次切页就全丢了，工具/测试里踩到过。）
        bool TryGoto(int i)
        {
            if (Animating) return false;
            ShowPage(i);
            return true;
        }

        // 程序性切页（首次显示 / 工具 / 测试）：一定切过去；上一段动画没收尾就先精确收尾，绝不卡住
        void ShowPage(int i, bool animate)
        {
            if (i < 0) i = 0;
            if (i > _pages.Length - 1) i = _pages.Length - 1;
            if (Animating) FinishNow();
            if (i == _cur) return;          // 已经在这一页：不重播
            BuildPage(i);
            int from = _cur;
            int dialFrom = _dial != null ? _dial.Current : -1;   // 分页器高亮的"起点"要按它自己的高亮算
            _cur = i;
            if (_dial != null) _dial.Current = i;
            if (from < 0 || !animate || !IsHandleCreated || _body == null
                || _body.ClientSize.Width <= 0 || _body.ClientSize.Height <= 0)
            {
                SnapTo(i);                  // 首次显示 / 窗口还没出来：直接摆好（老行为）
                return;
            }
            StartSlide(from, i, dialFrom);
        }

        // 把正在走的动画立刻收尾到目标页（精确落位，等同动画最后一帧）
        void FinishNow()
        {
            if (_ptimer != null) _ptimer.Stop();
            if (_pwatch != null) { try { _pwatch.Stop(); } catch { } _pwatch = null; }
            int to = _animTo;
            _animT = 1f;
            _animFrom = -1;
            if (to >= 0) SnapTo(to);
            else FreeSlide();
        }

        void BuildPage(int i)
        {
            if (_built[i]) return;
            _built[i] = true;
            _pages[i].SuspendLayout();
            _builders[i]();
            _pages[i].ResumeLayout(true);
            _pages[i].PerformLayout();
            EnsureFit(i);        // 建完就量：这一页的内容要是不够放，窗口当场长大（翻到哪页都不会裁）
        }

        // 精确落位：Dock=Fill 由布局引擎给出整格矩形，动画结束绝不留下 1px 偏移
        void SnapTo(int i)
        {
            // 先把真控件摆回来，再扔贴图 —— 任何一帧都不许出现"没内容"的空档
            for (int k = 0; k < _pages.Length; k++)
            {
                _pages[k].Dock = DockStyle.Fill;
                _pages[k].Visible = (k == i);
            }
            FreeSlide();
            if (_dial != null)
            {
                _dial.AnimFrom = i; _dial.AnimTo = i; _dial.AnimT = 1f;
                _dial.Current = i; _dial.Invalidate();
            }
            if (_body != null) { _body.PerformLayout(); _body.Invalidate(); }
        }

        void StartSlide(int from, int to, int dialFrom)
        {
            BuildPage(from);
            _animFrom = from; _animTo = to; _animT = 0f;
            _dialFrom = dialFrom < 0 ? from : dialFrom;
            int W = _body.ClientSize.Width, H = _body.ClientSize.Height;
            // 先把两页摆好（Dock=Fill）并排一次版，才能拍到正确的图
            for (int k = 0; k < _pages.Length; k++)
            {
                _pages[k].Dock = DockStyle.Fill;
                _pages[k].Visible = (k == from || k == to);
            }
            _body.PerformLayout();
            _pages[from].PerformLayout();
            _pages[to].PerformLayout();
            // 拍两张图：旧页滑出、新页滑入（各 4~9ms，一次切页只拍一次）
            FreeSlide();
            _slideA = ShotPage(_pages[from]);
            _slideB = ShotPage(_pages[to]);
            // 真控件全藏起来 —— 滑动期间 body 只贴这两张图，不挪窗口、不重排、不重画文字
            for (int k = 0; k < _pages.Length; k++) _pages[k].Visible = false;
            int dir = (to > from) ? 1 : -1;              // 往后翻：新页从右边进来
            _slideAx = 0;
            _slideBx = dir * W;
            _animDir = dir;
            _pwatch = System.Diagnostics.Stopwatch.StartNew();
            if (_ptimer == null)
            {
                _ptimer = new System.Windows.Forms.Timer();
                _ptimer.Interval = 10;                   // 和轮盘动画同一个节拍（~66fps）
                _ptimer.Tick += delegate(object o, EventArgs e2) { AnimTick(); };
            }
            _ptimer.Start();
            ApplySlide(0f);                              // 第 0 帧：新页整页在窗口外 —— 一帧都不许重叠
        }

        void ApplySlide(float t)
        {
            int W = _body == null ? 0 : _body.ClientSize.Width;
            if (W <= 0 || _body.ClientSize.Height <= 0) return;
            float e = Gfx.EaseOut(t);                // 先快后慢、收尾稳（跟轮盘同一套缓动）
            _slideBx = (int)Math.Round(_animDir * W * (1f - e));    // ±W -> 0
            _slideAx = (int)Math.Round(-_animDir * W * e);          // 0 -> ∓W
            if (_dial != null)
            {
                _dial.AnimFrom = _dialFrom; _dial.AnimTo = _animTo; _dial.AnimT = e;
                _dial.Invalidate();                  // 扇区高亮/凸起跟着一起走过去
            }
            if (_body != null) _body.Invalidate();   // 双缓冲面板：一帧只画一次，贴图不出闪
        }

        void AnimTick()
        {
            if (_animFrom < 0 || _animTo < 0) { if (_ptimer != null) _ptimer.Stop(); return; }
            // 进度看真实时间：这一帧画得慢（负载重/重绘多）时不会把整段动画拖长，总时长始终是 160ms 左右
            _animT = _pwatch == null ? 1f : (float)(_pwatch.Elapsed.TotalMilliseconds / PageAnimMs);
            if (_animT >= 1f)
            {
                _animT = 1f;                        // 收尾精确到 1，不留 1.03 这种余量
                ApplySlide(1f);                     // 最后一帧：位置精确等于目标（0 偏移）
                int to = _animTo;
                _animFrom = -1;
                if (_ptimer != null) _ptimer.Stop();
                if (_pwatch != null) { _pwatch.Stop(); _pwatch = null; }
                SnapTo(to);                         // 再交回布局引擎（Dock=Fill），保证和静态布局逐像素一致
                _animTo = to;
                return;
            }
            ApplySlide(_animT);
        }

    }
}
