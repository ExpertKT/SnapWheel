# SnapWheel v0.3.4

**日期**：2026-09-12

## 本次更新
- 新增：把桌面 / 资源管理器 / 任意位置的图片**拖到轮盘上松手**即可收进当前 wheel（拖动经过时轮盘整条弧变绿并提示「松手把 N 张图片加入「XX」」，松手后逐张滑入 + 左下角提示条）
- 拖文件夹也行：自动展开取第一层里的图片；一次最多 50 张；拖进来的图会按当前 wheel 落盘，重启后自动回读
- 图片格式大幅扩充（统一 ImageIO 加载器，三级兜底）：
  - GDI+ 原生：png / jpg / jpeg / jpe / jfif / bmp / dib / gif / tif / tiff / exif / wmf / emf
  - Icon 类：ico / cur（自动挑最大尺寸的帧，保留透明）
  - 系统 WIC（自动反射加载 PresentationCore，缺了也不影响本体）：webp / heic / heif / avif / jxl / jxr / wdp / hdp / dds / dng / cr2 / nef / arw / orf / rw2 / raf… （装了对应系统解码器就能读；本机已实测 WebP 550x368 通过）
- 存盘智能选格式：真透明存 PNG，不透明（照片）存 JPEG(q92)，磁盘不再被 PNG 撑爆；超过 4096px 的大图自动等比缩到 4096
- 新增托盘菜单「导入图片…」（多选 + 格式过滤），不想拖的时候也能加图
- 修复：IsAlphaPixelFormat 对任何 32bpp 图都返回 true 导致照片全部存成 PNG 的坑（改为真实扫描像素 alpha）