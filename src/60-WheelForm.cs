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
    // 轮盘主窗口：字段 / 构造 / 生命周期 / 布局 / 几何 / 对外接口。
    // 绘制、动画、毛玻璃背景、鼠标键盘与拖放分别在 61/62/63/64 几个 partial 里。
    partial class WheelForm : Form
    {
        Store _store { get { return _mgr.ActiveStore; } }     // always the ACTIVE wheel's store
        WheelManager _mgr;
        Settings _settings;
        Timer _anim;
        float _R = 300f;          // ring radius, measured from the screen corner
        float _thumb = 96f;       // nominal thumbnail long side
        float _phiMin, _phiMax;   // angle span that keeps cards fully on screen
        int _slots = 5;
        StoreItem _dragOutItem = null;   // item being pulled out (animates away)
        float _offset = 0f, _targetOffset = 0f;
        float _show = 0f, _targetShow = 0f;
        DateTime _showT0 = DateTime.Now;
        float _showFrom = 0f;
        bool _showAnimating = false;
        int _hover = -1;
        int _enlarged = -1;
        int _holdIndex = -1;
        DateTime _holdStart = DateTime.MinValue;
        bool _closeHover = false;
        bool _gearHover = false;
        bool _shootHover = false;
        public event EventHandler SettingsRequested;
        Dictionary<int, float> _scales = new Dictionary<int, float>();
        int _peekIndex = -1;           // kept during the fade-out so the peek can animate away
        float _dragOutProg = 0f;       // 0..1 pull-out shrink progress
        StoreItem _deletingItem = null;
        float _deleteProg = 0f;
        DateTime _lastRightClick = DateTime.MinValue;
        int _lastRightIndex = -1;
        const float HoverScale = 1.36f;
        float PeekScale { get { return Math.Max(1.2f, Math.Min(5f, _settings.PeekPercent / 100f)); } }

        // ---------- 风格参数（新拟态 / 扁平 / 毛玻璃）----------
        bool StyleNeu() { return _settings.UiStyle != "flat" && _settings.UiStyle != "solid"; }
        bool StyleSolid() { return _settings.UiStyle == "solid"; }
        bool StyleFlatOnly() { return _settings.UiStyle == "flat"; }

        // 动画速度：>1 = 更快。所有时长都乘这个系数，保证各段动画不会各走各的
        float AnimK() { return 100f / Math.Max(50f, Math.Min(200f, (float)_settings.AnimSpeed)); }

        // 面板底色（毛玻璃的"玻璃"部分；solid 风格强制不透明，方便在花哨壁纸上也能看清）
        Color GlassBase()
        {
            if (StyleSolid()) return Color.FromArgb(30, 32, 38);
            if (StyleFlatOnly()) return Color.FromArgb(26, 28, 34);
            return Color.FromArgb(20, 23, 30);
        }


        int ShadowA(int baseA) { return (int)(baseA * _settings.ShadowPercent / 100f); }

        float CardRadOf(RectangleF r)
        {
            float rad = Math.Min(r.Width, r.Height) * (_settings.CardRadius / 100f);
            return rad < 3f ? 3f : rad;
        }


        Color AccentColor()
        {
            int i = _settings.AccentIndex;
            if (i >= 0 && i < Palette.Colors.Length) return Palette.Get(i);
            return _mgr.Accent;
        }


        float _keyT = 0f;              // 万能键按下进度 0..1

        float _keyHov = 0f;            // 万能键悬停进度 0..1（指针压上去要有反应）

        bool _keyHover = false;

        bool _nameHover = false;       // 指针停在 Wheel 名药丸上（提示"点一下改名"）

        // 圆钮按下反馈：按下先变暗缩一下，过 ~110ms 再真正执行，这样"按下去"是看得见的
        float _closeDown = 0f, _gearDown = 0f, _shootDown = 0f;

        bool _closePend = false, _gearPend = false, _shootPend = false;   // 已按下、等延迟

        bool _closeHold = false, _gearHold = false, _shootHold = false;   // 鼠标仍按着

        DateTime _closeDownAt = DateTime.MinValue;                        // 关闭键按下的时刻（判长按）

        bool _closeLong = false;                                          // 关闭键已长按到位（变红，松手退出）

        float _closeHoldP = 0f;                                           // 长按进度 0..1（画红色进度环）

        string _pendingBtn = "";

        DateTime _pendingAt = DateTime.MinValue;

        bool _intro = false;           // 正在播环的动画（拉出 / 收起都算）

        float _introT = 0f;            // 0..1：环"露出来"的程度

        float _introDur = 1.8f;        // 秒（完成一整趟 0->1 的时间基准）

        DateTime _introAt = DateTime.MinValue;

        float _ringFrom = 1f;          // 本次动画的起点值

        float _ringTo = 1f;            // 本次动画的目标值（可反向，随时改目标）


        // ---------- 收起态（像贴边小球那样，只在屏幕边上留一个可点的小把手）----------
        bool _collapsed = false;       // 已完全收起：只画把手，不画环

        bool _collapsing = false;      // 当前这次动画是"收起"方向

        bool _nubOutHover = false;     // 指针停在"拉出"把手上

        bool _nubInHover = false;      // 指针停在"收起"把手上

        float _nubHov = 0f;            // 把手悬停进度 0..1

        float _nubAppearT = 1f;        // 把手"出现"进度 0..1（启动时不要突然冒出来）

        DateTime _nubAppearAt = DateTime.MinValue;

        float _nubHintT = 0f;                            // 把手"点我展开/收起"提示的淡入进度

        bool _adminTipShown = false;                     // 管理员"拖不动"的说明每次运行只弹一次

        DateTime _firstRunHintUntil = DateTime.MinValue; // 首次运行自动亮提示的截止时刻

        DateTime _collapsedAt = DateTime.MinValue;       // 收起完成的时刻（之后一小段内不允许再展开）

        string _lastClipFp = "";                          // 上一张从剪贴板收进来的图（去重用）
        // 注：以前这里还有一个 _selfClipboardAt（"自己写完剪贴板 1.5 秒内不导入"的时间窗），
        // 已经换成 SelfClipboard 的按图指纹登记：时间窗会连用户在这段时间里真正复制的一张图一起吞掉，
        // 而且窗口一过就失效（截图浮层是模态的，消息什么时候被泵到并不确定）。


        public bool IsCollapsed { get { return _collapsed; } }
        public bool IsExpanded { get { return !_collapsed && !_intro; } }

        // 贴边把手：一条沿"竖直的屏幕边"（拉出），一条沿"水平的屏幕边"（收起）
        const float NubLong = 78f;     // 把手长边
        const float NubThick = 13f;    // 把手厚度（贴着屏幕边）

        // 把手 = 卷轴的两端。环的圆弧从"竖直那条边上距角落 R 处"扫到"水平那条边上距角落 R 处"，
        // 这两个端点就是卷轴的两头，把手就钉在端点上，正好接住弧的末端。
        // 卡片都往内缩了一个安全角（phiMin），所以把手不会压到最边上的那张卡。
        float _nubOutDist = -1f, _nubInDist = -1f;   // <0 = 用自动值；调参时可覆盖
        float NubDistAuto() { return _R; }
        float NubDistOut() { return _nubOutDist >= 0f ? _nubOutDist : NubDistAuto(); }
        float NubDistIn() { return _nubInDist >= 0f ? _nubInDist : NubDistAuto(); }

        // 光标是否还在关闭键附近（留 26px 余量：手抖不算离开，明显挪开才作废）
        bool _testIgnoreLeave = false;      // 测试用：跳过光标判断（合成事件时真实光标不在按钮上）
        bool CursorOverCloseButton()
        {
            if (_testIgnoreLeave) return true;
            try
            {
                Point cp = ToLogicalPt(PointToClient(Cursor.Position));
                Rectangle r = CloseButtonRect();
                r.Inflate(26, 26);
                return r.Contains(cp);
            }
            catch { return true; }   // 取不到就当作还在按钮上，别误取消
        }


        // 单把手模式：右下边那个把手不画、也不能点（任务栏自动隐藏时鼠标扫底边不会撞到它）
        public bool NubSingleMode() { return _settings.NubSingle; }

        bool CanExpandByNub()
        {
            return true;      // 不做冷却：收起后也可以立刻再展开（两个方向都随时可点）
        }


        const float CardPad = 0f;       // card == image rect, so the picture fills the rounded frame

        DateTime _lastActive = DateTime.Now;

        Point _mouseDownPt;

        bool _maybeDrag;

        int _dragIndex = -1;

        bool _rendered = false;

        DragProxyForm _proxy = new DragProxyForm();


        const int WS_EX_LAYERED = 0x80000;

        const int WS_EX_TOOLWINDOW = 0x80;

        const int WS_EX_NOACTIVATE = 0x08000000;


        public WheelForm(WheelManager mgr, Settings settings)
        {
            _mgr = mgr;
            _settings = settings;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = settings.AlwaysOnTop;
            ApplyLayout();
            // 视口初始位置 = "最新那张顶在弧上端"（0.5.3 的锚点，见 OffsetForNewest）。
            // 必须在 ApplyLayout 之后设（_slots / _phiMin / _phiMax 都是它算出来的），
            // 也必须在这里设：Store 在构造时就把保存目录里的图恢复了，开机第一眼要给最新那批；
            // 不设的话第一张新图会从"下端那一格"往上爬到上端（反方向的动画）。
            _offset = _targetOffset = OffsetForNewest();
            GiveFeedback += new GiveFeedbackEventHandler(OnGiveFeedback);
            AllowDrop = true;
            DragEnter += new DragEventHandler(OnDragOverWheel);
            DragOver += new DragEventHandler(OnDragOverWheel);
            DragLeave += new EventHandler(delegate(object o, EventArgs ev) { ClearDropCache(); if (_dropActive) { _dropActive = false; _dropExternal = false; Render(); } });
            DragDrop += new DragEventHandler(OnDragDropWheel);
            _anim = new Timer();
            _anim.Interval = 15;
            _anim.Tick += new EventHandler(AnimTick);
            _anim.Start();

            // 首次运行（展开状态）：让"点我收起"把手提示自动亮一次
            if (!_settings.NubHintDone)
            {
                _firstRunHintUntil = DateTime.Now.AddSeconds(14);
                _settings.NubHintDone = true;
            }
        }


        // ---------- 分辨率 / DPI 适配 ----------
        // UiK = 界面缩放系数。所有绘制都在"逻辑坐标"里做，DrawWheel 开头统一 ScaleTransform(UiK)，
        // 于是字体、图标、线宽、间距全都跟着放大，不用到处改常数。
        // 命中测试也全在逻辑坐标里算：鼠标进来先 ToLogical() 转一次。
        float UiK = 1f;


        float AutoUiK()
        {
            try { return Native.DpiScaleOf(IsHandleCreated ? Handle : IntPtr.Zero); }
            catch { return 1f; }
        }


        PointF ToLogical(Point p) { return new PointF(p.X / UiK, p.Y / UiK); }
        Point ToLogicalPt(Point p) { return new Point((int)Math.Round(p.X / UiK), (int)Math.Round(p.Y / UiK)); }
        SizeF LogicalSize() { return new SizeF(Width / UiK, Height / UiK); }

        // 把鼠标事件里的物理坐标换成逻辑坐标（其余字段原样带过来）
        MouseEventArgs LogicalArgs(MouseEventArgs e)
        {
            if (Math.Abs(UiK - 1f) < 0.001f) return e;
            return new MouseEventArgs(e.Button, e.Clicks, (int)Math.Round(e.X / UiK), (int)Math.Round(e.Y / UiK), e.Delta);
        }


        public void ApplyLayout()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            float baseSpan = Math.Max(120, Math.Min(700, _settings.Radius)) +
                             Math.Max(40, Math.Min(260, _settings.ThumbSize)) * 1.75f + 190f;

            // 自动 = 跟显示器缩放比例走（2K@125% -> 1.25，4K@150% -> 1.5），看起来大小才一致；
            // 但自动模式不会把轮盘撑得比屏幕还大
            float k = (_settings.UiScale > 0) ? (_settings.UiScale / 100f) : AutoUiK();
            if (_settings.UiScale <= 0)
            {
                float fit = Math.Min(wa.Width, wa.Height) / baseSpan;
                if (k > fit) k = fit;
            }
            UiK = Math.Max(0.6f, Math.Min(2.5f, k));

            _thumb = Math.Max(40, Math.Min(260, _settings.ThumbSize));     // 逻辑值
            _R = Math.Max(120, Math.Min(700, _settings.Radius));           // 逻辑值
            _slots = Math.Max(2, Math.Min(12, _settings.Slots));
            _phiMin = (float)Math.Asin(Math.Min(0.92, (_thumb * 0.80f) / _R));
            _phiMax = (float)(Math.PI / 2) - _phiMin;
            // 逻辑尺寸 -> 物理像素：半径 + 摇杆键(92) + 名字标签
            int size = (int)Math.Round((_R + _thumb * 1.75f + 190f) * UiK);
            if (size < 160) size = 160;
            int cap = Math.Min(wa.Width, wa.Height);
            if (size > cap) size = cap;            // 别让窗口比屏幕还大（角落外那截本来就是空的）
            if (Size.Width != size) Size = new Size(size, size);
            PlaceBottomLeft();
            _rendered = false;
            FreeBackdrop();                    // 尺寸/位置变了，玻璃底得重抓
            if (Visible) { RequestBackdropAsync(); Render(); }
        }


        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 让窗口对截屏隐身：这样玻璃底可以随时重抓（不会把轮盘自己拍进去），
            // 顺带好处是用户截图时轮盘不会出现在图里
            try { Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE); } catch { }
            try { Native.AddClipboardFormatListener(Handle); } catch { }   // 剪贴板里有新图 -> 自动收进轮盘
            // 句柄建好之后 DPI 才查得准；自动模式下补一次布局
            if (_settings.UiScale <= 0)
            {
                float want = Math.Max(0.6f, Math.Min(2.5f, AutoUiK()));
                if (Math.Abs(want - UiK) > 0.01f) ApplyLayout();
            }
        }


        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }


        public void ApplyTopMost()
        {
            TopMost = _settings.AlwaysOnTop;
            if (Visible) { _rendered = false; Render(); }   // no Hide/Show flash
        }


        public void PlaceBottomLeft()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int size = Width;
            int left = (Sx() > 0) ? wa.Left : wa.Right - size;
            int top = (Sy() > 0) ? wa.Top : wa.Bottom - size;
            Location = new Point(left, top);
        }


        // ---- geometry: a full ring whose centre sits on the chosen screen corner ----
        float Sx() { return _settings.Corner.EndsWith("L") ? 1f : -1f; }   // outward horizontal (into screen)
        float Sy() { return _settings.Corner.StartsWith("T") ? 1f : -1f; } // outward vertical (into screen)

        PointF Center()
        {
            // 逻辑坐标（绘制带 UiK 缩放，命中测试也统一用逻辑坐标）
            SizeF ls = LogicalSize();
            float cx = (Sx() > 0) ? 0f : ls.Width;
            float cy = (Sy() > 0) ? 0f : ls.Height;
            return new PointF(cx, cy);
        }


        float ArcStart()
        {
            if (Sy() < 0) return (Sx() > 0) ? 270f : 180f;
            return (Sx() > 0) ? 0f : 90f;
        }


        float StepRad() { return (_phiMax - _phiMin) / Math.Max(1, _slots - 1); }

        float EffR() { return _R; }

        float ItemPhi(int i) { return _phiMin + (i - _offset) * StepRad(); }

        // ============================ 视口锚点（0.5.3） ============================
        // `_offset` 的含义没变：**落在弧下端那一格（_phiMin）上的图片下标**（可以是负数，见下）。
        // 于是"让最新那张（下标 Count-1）顶在弧的**上端**（_phiMax）"就是：
        //     _offset + (Slots-1) = Count-1   →   _offset = Count - Slots
        // Count < Slots 时它是负数 —— 那正是"从弧上端开始往下堆、下端先空着"的样子。
        //
        // 为什么改这个：以前"跟到最新"用的是 Count-1（最新那张落在**下端**那一格），
        // 于是每截一张，之前的图全被推到弧下方看不见，而弧的上半截永远是空的（用户原话
        // "之前的缩略图在下面全都显示不全，而 wheel 上半部分又空空的没有利用上"）。
        // 改成"最新顶在上端"之后，老图依次往下排；堆满（Count > Slots）之后再来新图时
        // _offset 会整体 +1，也就是**新图从上端挤进来、老图一起被往下挤一格**，
        // 最下面那张滑出可见弧 —— 这就是用户要的堆叠逻辑。
        float OffsetForNewest() { return _store.Items.Count - _slots; }
        float MinOffset() { return OffsetForNewest(); }
        float MaxOffset() { return Math.Max(MinOffset(), _store.Items.Count - 1); }

        PointF ItemCenter(int i) { return ItemCenterAtPhi(ItemPhi(i)); }

        PointF ItemCenterAtPhi(float phi)
        {
            float r = EffR();
            PointF c = Center();
            return new PointF((float)(c.X + r * Math.Cos(phi) * Sx()), (float)(c.Y + r * Math.Sin(phi) * Sy()));
        }


        Dictionary<StoreItem, DateTime> _enterT0 = new Dictionary<StoreItem, DateTime>();


        // 只有“真的拉得很长”的图才进特殊方框：宽高比超过 ExtremeRatio:1（或反过来）才算。
        // 想改判定松紧，只动这一个数就行：越小越容易进方框，越大越严格。
        public const float ExtremeRatio = 4.5f;


        // very extreme aspect ratios get a fixed square box with a distinctive border colour
        static bool IsExtreme(StoreItem it)
        {
            if (it == null || it.Image == null) return false;
            float a = ImgW(it) / Math.Max(1f, ImgH(it));
            return a > ExtremeRatio || a < (1f / ExtremeRatio);
        }


        // card size == the image's exact aspect; extreme ones become a square box
        // 已释放的 Image 不是 null，直接读宽高会抛 ArgumentException —— 统一走这里兜住
        static float ImgW(StoreItem it) { try { return (it != null && it.Image != null) ? it.Image.Width : 1f; } catch { return 1f; } }
        static float ImgH(StoreItem it) { try { return (it != null && it.Image != null) ? it.Image.Height : 1f; } catch { return 1f; } }

        SizeF CardSize(StoreItem it)
        {
            float iw = ImgW(it);
            float ih = ImgH(it);
            if (IsExtreme(it)) return new SizeF((float)Math.Round(_thumb), (float)Math.Round(_thumb));
            float s = _thumb / Math.Max(iw, ih);
            return new SizeF(Math.Max(6f, (float)Math.Round(iw * s)), Math.Max(6f, (float)Math.Round(ih * s)));
        }


        // card = image rect grown by `pad` (hover lift). pad may be negative (pull-out shrink).
        RectangleF CardRect(int i, float pad)
        {
            if (i < 0 || i >= _store.Items.Count) return RectangleF.Empty;
            PointF c = ItemCenter(i);
            SizeF sz = CardSize(_store.Items[i]);
            float x = (float)Math.Round(c.X - sz.Width / 2f - pad);
            float y = (float)Math.Round(c.Y - sz.Height / 2f - pad);
            return new RectangleF(x, y, sz.Width + 2f * pad, sz.Height + 2f * pad);
        }


        // image rect inside a card, always the crisp nominal size
        RectangleF ImageRect(int i)
        {
            if (i < 0 || i >= _store.Items.Count) return RectangleF.Empty;
            PointF c = ItemCenter(i);
            SizeF sz = CardSize(_store.Items[i]);
            return new RectangleF((float)Math.Round(c.X - sz.Width / 2f), (float)Math.Round(c.Y - sz.Height / 2f), sz.Width, sz.Height);
        }


        // fit the whole image inside a box, preserving aspect
        static SizeF FitInside(Size img, float bw, float bh)
        {
            if (img.Width <= 0 || img.Height <= 0) return new SizeF(bw, bh);
            float s = Math.Min(bw / img.Width, bh / img.Height);
            return new SizeF(img.Width * s, img.Height * s);
        }


        // cache scaled thumbnails by rounded pixel size -> no per-frame resampling shimmer
        Dictionary<StoreItem, Dictionary<long, Bitmap>> _thumbCache = new Dictionary<StoreItem, Dictionary<long, Bitmap>>();


        // draw a bitmap honouring an alpha value (DrawImageUnscaled ignores alpha entirely)
        ImageAttributes _ia = new ImageAttributes();


        Rectangle CloseButtonRect() { return BtnRect(0); }
        Rectangle GearButtonRect() { return BtnRect(1); }
        Rectangle ShootButtonRect() { return BtnRect(2); }

        // ---- 万能键（在弧线中点）：长按弹出四扇区圆盘，拖到扇区松手执行 ----
        Rectangle KeyRect()
        {
#if NO_KEY
            return Rectangle.Empty;           // v0.2.0 变体：不含万能键
#else
            float mid = (_phiMin + _phiMax) / 2f;
            // 弧的“内侧”中点：避开缩略图，也不压住角上的按钮
            float kr2 = EffR() - _thumb * 1.25f;
            if (kr2 < 60f) kr2 = 60f;
            PointF p = ItemCenterAtPhiRadius(mid, kr2);
            int s = 92;                       // 摇杆式大圆盘
            return new Rectangle((int)Math.Round(p.X - s / 2f), (int)Math.Round(p.Y - s / 2f), s, s);
#endif
        }


        PointF ItemCenterAtPhiRadius(float phi, float radius)
        {
            PointF c = Center();
            return new PointF((float)(c.X + radius * Math.Cos(phi) * Sx()), (float)(c.Y + radius * Math.Sin(phi) * Sy()));
        }


        bool _keyDown = false;

        DateTime _keyDownAt = DateTime.MinValue;

        // 万能键长按多久弹圆盘：从 260ms 收到 140ms（更跟手），仍能区分"点一下"和"长按"
        const int KeyMenuDelayMs = 140;

        float _menuT = 0f;             // 0..1 radial menu expansion

        bool _menuOpen = false;

        int _sector = -1;              // 0=上 1=右 2=下 3=左（动作可由用户自定义）

        bool _delConfirm = false;      // 删除确认态：摇杆左右两半 = 取消 / 确认

        DateTime _delConfirmAt = DateTime.MinValue;

        int _delHalf = -1;             // -1=不在键上 0=左半(取消) 1=右半(确认)

        Point _swipeStart;

        Color _accentCur = Color.FromArgb(0, 122, 204);

        float _switchFlash = 0f;

        Point _backdropOffset = new Point(0, 0);

        readonly object _glassLock = new object();
        int _frameNo = 0;                      // 第几帧（换底交叉淡入靠它做到"一帧只混一次"）
        readonly Dictionary<StoreItem, long> _cardSizeMemo = new Dictionary<StoreItem, long>();   // 上一帧每张卡片的尺寸（判断"能不能用贴片缓存"）


        ImageAttributes _iaBack = new ImageAttributes();


        // 名字太长就把中间省略掉，免得药丸撑太宽压到别的东西
        public static string FitName(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= max) return s;
            if (max <= 1) return s.Substring(0, 1);
            int head = (max - 1) / 2, tail = max - 1 - head;
            return s.Substring(0, head) + "…" + s.Substring(s.Length - tail, tail);
        }


        // Wheel 名药丸的矩形（画的时候记下来，命中测试用同一个，改了名字也不会错位）
        RectangleF _namePillRect = RectangleF.Empty;

        RectangleF NamePillRect()
        {
            if (!_settings.ShowNameLabel) return RectangleF.Empty;
            if (_namePillRect.Width > 1f) return _namePillRect;
            // 还没画过（刚启动/刚开关过标签）就先按万能键位置估一个，保证点得到
            Rectangle kr = KeyRect();
            if (kr.Width < 8) return RectangleF.Empty;
            return new RectangleF(kr.X + kr.Width / 2f - 60f, kr.Bottom + 4f, 120f, 30f);
        }


        void SwitchWheel(int dir)
        {
            if (dir > 0) _mgr.Next(); else _mgr.Prev();
            _mgr.Save();
            AfterWheelSwitch();
        }


        public void RemoveItem(StoreItem it, bool deleteFile)
        {
            if (it == null) return;
            // 先留一份"后悔药"（只留引用，不拷图）：托盘「撤销上一次删除」靠它
            if (deleteFile)
            {
                Wheel w = null;
                try { w = _mgr.ActiveWheel; } catch { }
                Undo.Push(w, new StoreItem[] { it }, false);
            }
            try { _store.Items.Remove(it); } catch { }
            try { _thumbCache.Remove(it); } catch { }
            try { _enterT0.Remove(it); } catch { }
            _scales.Clear();
            if (deleteFile)
            {
                try { if (it.FilePath != null && it.FilePath.Length > 0 && File.Exists(it.FilePath)) File.Delete(it.FilePath); }
                catch { }
                // 第一次删图时说清楚"还能找回来" —— 否则没人知道托里有这个后悔药
                if (_settings != null && !_settings.UndoHintDone)
                {
                    _settings.UndoHintDone = true;
                    try { _settings.Save(); } catch { }
                    ShowToast("已删掉这张 —— 托盘右键「撤销上一次删除」可以找回来");
                }
            }
            _hover = -1; _enlarged = -1; _peekIndex = -1;
            if (_targetOffset > MaxOffset()) _targetOffset = MaxOffset();
            if (_targetOffset < MinOffset()) _targetOffset = MinOffset();
            if (_offset > _targetOffset) _offset = _targetOffset;
            _rendered = false;
            Render();
        }


        // 一键清空当前轮盘里的图片，轮盘本身保留
        public void ClearCurrentWheel()
        {
            int n = _store.Items.Count;
            try
            {
                StoreItem[] all = _store.Items.ToArray();
                // 清空也是一次删除：整批留一份，能整体撤回
                if (all.Length > 0)
                {
                    Wheel w = null;
                    try { w = _mgr.ActiveWheel; } catch { }
                    Undo.Push(w, new List<StoreItem>(all), true);
                }
                for (int i = 0; i < all.Length; i++)
                {
                    try { if (all[i].FilePath != null && File.Exists(all[i].FilePath)) File.Delete(all[i].FilePath); } catch { }
                }
                _store.Items.Clear();
                _thumbCache.Clear();
                _enterT0.Clear();
                _scales.Clear();
                _offset = 0f; _targetOffset = 0f; _hover = -1; _enlarged = -1; _peekIndex = -1;
                _deletingItem = null; _deleteProg = 0f;
                _rendered = false;
                Render();
                ShowToast(n > 0 ? ("已清空这一盘：" + n + " 张（托盘 → 撤销上一次删除 可以找回来）") : "这一盘本来就是空的");
            }
            catch (Exception ex) { Err.Log("ClearCurrentWheel", ex); }
        }


        // 设置窗口点了确定之后，界面上要做的收尾。
        // 抽成方法是为了能写行为测试 —— 以前这里曾混进一句 HideWheel()（收起态关掉时），
        // 结果每次点确定，轮盘都当场消失，而当时 150 项绘制测试一个都发现不了。
        public void AfterSettingsApplied()
        {
            ApplyTopMost();
            ApplyLayout();
            // 收起态被关掉时，别让轮盘卡在"只剩个把手"的状态里
            if (!_settings.CollapseMode && _collapsed) ExpandWheel();
            RefreshWheel();            // 强制重绘：万能键上的动作名等设置改完要立刻生效
        }


        public void RefreshWheel()
        {
            // 回到"最新那张顶在弧上端"这个默认视口（0.5.3 起这就是默认位；以前是 0 = 最老那张在下端）
            _offset = _targetOffset = OffsetForNewest();
            _hover = -1; _enlarged = -1; _peekIndex = -1;
            _scales.Clear(); _enterT0.Clear();
            _switchFlash = 1f;
            Render();
        }


        void AfterWheelSwitch()
        {
            _offset = _targetOffset = OffsetForNewest();
            _hover = -1; _enlarged = -1; _peekIndex = -1;
            _scales.Clear(); _enterT0.Clear();
            // 新 wheel 的图依次滑入，形成切换过渡
            for (int i = 0; i < _store.Items.Count; i++)
                _enterT0[_store.Items[i]] = DateTime.Now.AddSeconds(i * 0.045);
            _switchFlash = 1f;
            Render();
        }


        void CreateWheel()
        {
            _mgr.New();
            _mgr.Active = _mgr.Wheels.Count - 1;
            _mgr.Save();
            AfterWheelSwitch();
        }


        void DeleteWheel()
        {
            _mgr.Remove(_mgr.Active);
            _mgr.Save();
            AfterWheelSwitch();
        }


        // 点名字药丸改名
        void RenameWheel()
        {
            Wheel w = _mgr.ActiveWheel;
            string old = w.Name;
            try
            {
                using (RenameForm rf = new RenameForm(old))
                {
                    TopMost = false;                     // 轮盘别盖在弹框上面
                    rf.TopMost = true;
                    DialogResult r = rf.ShowDialog();
                    TopMost = _settings.AlwaysOnTop;
                    if (r != DialogResult.OK) { Render(); return; }
                    string nv = rf.Value;
                    if (!string.IsNullOrEmpty(nv) && nv != old)
                    {
                        w.Name = nv;
                        _mgr.Save();
                        ShowToast("已改名为「" + nv + "」");
                    }
                }
            }
            catch { }
            Render();
        }

        public event EventHandler CaptureRequested;

        public event EventHandler ExitRequested;      // 长按关闭键 -> 完全退出

        // 管理员模式下"拖了半天啥也没发生"时抛出去：让 AppCtx 弹说明（要不要换普通权限）
        public event EventHandler AdminHelpRequested;

        // 中键点缩略图：把这张图贴（钉）到屏幕上；at = 想钉的位置（屏幕坐标，图片以它为中心）
        public event Action<Bitmap, Point> PinRequested;


        IntPtr _memDc = IntPtr.Zero, _dib = IntPtr.Zero, _oldBmp = IntPtr.Zero, _bits = IntPtr.Zero;

        int _dibW, _dibH;


        // 圆盘上给动作名用的短标签（太长会画不下）
        static string KeyActionShort(string id)
        {
            switch (id)
            {
                case "new": return "新建";
                case "next": return "下一个";
                case "prev": return "上一个";
                case "delete": return "删除";
                case "shot": return "截图";
                case "collapse": return "收起";
                case "folder": return "文件夹";
                case "settings": return "设置";
                case "paste": return "收一张";
                case "clear": return "清空";
                default: return "";
            }
        }


        bool _dropActive = false;

        bool _returnedToWheel = false;

        bool _dropExternal = false;      // 拖进来的是“外面的文件”（不是轮盘自己的图）

        int _dropCount = 0;

        object _dropCacheKey = null;

        List<string> _dropCacheFiles = null;

        DateTime _dropCacheAt = DateTime.MinValue;

        const string DragFmt = "SnapWheelMove";     // 标记：这是轮盘自己在拖的图


        // 左下角的小提示条
        string _toast = "";

        DateTime _toastAt = DateTime.MinValue;

        public void ShowToast(string s)
        {
            _toast = s == null ? "" : s;
            _toastAt = DateTime.Now;
        }


        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 动画定时器必须停掉：不然窗口关掉之后它还每 15ms 醒一次，
                // 白烧 CPU、还会对着已经释放的窗口去重绘（测试里尤其明显：越跑越慢）
                try { if (_anim != null) { _anim.Stop(); _anim.Dispose(); _anim = null; } } catch { }
                try { ReleaseDib(); } catch { }
                try { FreeBackdrop(); } catch { }
                try { DropLayers(); } catch { }
                try { if (IsHandleCreated) Native.RemoveClipboardFormatListener(Handle); } catch { }
            }
            base.Dispose(disposing);
        }


        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ShowMsg) { ShowWheelWithIntro(); return; }   // second launch asks us to show
            if (m.Msg == 0x031D) { OnClipboardChanged(); return; }      // WM_CLIPBOARDUPDATE：剪贴板有新图
            if (m.Msg == 0x02E0 && _settings.UiScale <= 0)              // WM_DPICHANGED：系统缩放变了，重排一次
            { try { ApplyLayout(); } catch { } }
            if (m.Msg == 0x0084)
            {
                Point cpRaw = PointToClient(Cursor.Position);
                // 空地方点穿（不误点）；但左键按着的时候不做穿透 ——
                // 那多半正在拖拽，穿透会让系统找不到拖放目标，图就掉不进来了
                bool dragging = (Control.MouseButtons & MouseButtons.Left) != 0;
                bool inWin = cpRaw.X >= 0 && cpRaw.Y >= 0 && cpRaw.X < Width && cpRaw.Y < Height;
                if (!dragging && inWin && !OverContent(ToLogicalPt(cpRaw)))
                { m.Result = (IntPtr)(-1); return; }
            }
            base.WndProc(ref m);
        }


        public static readonly uint ShowMsg = Native.RegisterWindowMessage("SnapWheel_SHOW");
    }
}
