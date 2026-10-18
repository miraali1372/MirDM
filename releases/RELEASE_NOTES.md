# ⚡ MirDM v1.0.0 — Official Portable Release

### 🚀 Ultra-Speed Windows Portable Download Manager (.NET 9 & C# 13)

We are thrilled to announce the official v1.0.0 release of **MirDM (Mir Download Manager)** — the next-generation native Windows download manager and network acceleration engine.

---

### 🌟 Release Highlights
- **⚡ Native HTTP/3 QUIC Transport (`MsQuic`)**: Eliminates TCP Head-of-Line (HoL) blocking with independent UDP-multiplexed streams.
- **🏎️ Dynamic DNS Edge-IP Racing**: Micro-handshake races identify the lowest-latency CDN edge nodes in real time.
- **🔀 Dynamic Splitter & Tail-Latency Work Stealing**: Automatically bifurcates and redistributes lagging chunks at the 99% mark so 100% of bandwidth is utilized until the very last byte.
- **💾 Direct IOCP Zero-Copy Disk I/O**: Direct async random-access writes to exact byte offsets on NTFS/exFAT with zero temp-file stitching overhead.
- **📋 Add Batch & Asterisk (`*`) Wildcard Generator**: Paste lists of URLs or auto-generate numbered sequences (e.g. `episode_(*).mp4` from 1 to 50 with leading zeros).
- **🧹 Safe "Clear List"**: Cleans queues and sessions without deleting your downloaded files on disk.
- **🎨 Fluent Dark Acrylic Desktop UI**: Visual Segment Heatmap, real-time Canvas speed graph, and live telemetry.
- **🧳 100% Portable**: No installation, no admin rights, no dependencies. Just extract and run!

---

### 📦 Package Assets
- **`MirDM_v1.0.0_Portable_Win-x64.zip`**: Contains `MirDM.exe`, `msquic.dll`, and `README.txt`.
- **Target OS**: Windows 10 (Version 1809+) or Windows 11 (64-bit x64).

---

### 🇮🇷 راهنمای نسخه فارسی
- این نسخه کاملاً پرتابل بوده و بدون نیاز به نصب روی تمامی سیستم‌های ویندوز ۱۰ و ۱۱ نگارش ۶۴ بیتی اجرا می‌شود.
- دارای موتور اختصاصی HTTP/3، دانلود دسته‌ای با الگوهای ستاره مانند IDM، و پاک‌کردن ایمن لیست بدون حذف فایل‌ها.
- فایل `MirDM_v1.0.0_Portable_Win-x64.zip` را دانلود و استخراج کرده و `MirDM.exe` را اجرا کنید.
