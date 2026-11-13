using System.Runtime.InteropServices;

namespace VowenLite;

// Small click-through pill at the top centre of the screen the mouse is on. Drawn with GDI+ (flat API)
// into a per-pixel-alpha layered window, so edges are smooth and it can fade, without loading WinForms
// into the daemon. Listening: a level-driven waveform. Transcribing: three pulsing dots.
// Created on the daemon thread; Set() can be called from any thread (it posts to that thread).
static partial class Overlay
{
    public enum State { Hidden, Listening, Transcribing, Thinking, Assistant, Notice }   // Assistant = hold-to-talk to Luna

    // Mic level (RMS, 0..1) for the waveform; set by Dictation while recording.
    public static Func<float> LevelSource;

    const uint WM_APP_STATE = 0x8001, WM_TIMER = 0x0113;
    const float PillWBase = 172, PillH = 44, Margin = 16;   // at 96 dpi
    static float pillW = PillWBase;
    static string noticeText = "";
    static long noticeSeq;
    const int FadeInMs = 200, FadeOutMs = 150, FadeStateMs = 160;

    // Palette (ARGB), same as the window: black ground, grey surface, ice accent.
    const uint Fill = 0xFF0C0D10, Border = 0xFF2A2D35, Ice = 0xFFA9D8FF, Ink = 0xFFF3F4F6, Text2 = 0xFFC9CCD3;

    static IntPtr hwnd, token;
    static WndProc wndProc; // kept alive: Windows only holds a raw pointer
    static State drawState = State.Listening;
    static bool Live => drawState == State.Listening || drawState == State.Assistant;
    static bool visible;
    static long showStart, hideStart, stateStart;
    static float scale = 1, level;
    static int winW, winH, winX, winY;
    static IntPtr memDc, dib, bits, bitmap, g, fontFamily, font, format;
    static IntPtr oldBmp;

    public static void Create()
    {
        var si = new GdiplusStartupInput { GdiplusVersion = 1 };
        GdiplusStartup(out token, ref si, IntPtr.Zero);
        wndProc = Proc;
        var wc = new WNDCLASS { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc), hInstance = GetModuleHandle(null), lpszClassName = "VowenLiteOverlay" };
        RegisterClass(ref wc);
        const uint WS_POPUP = 0x80000000;
        const uint WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x8000000;
        hwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE,
            wc.lpszClassName, "VowenLite", WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        CreateChip();
    }

    public static void Set(State s)
    {
        if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_APP_STATE, (IntPtr)(int)s, IntPtr.Zero);
    }

    static IntPtr Proc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_STATE)
        {
            var s = (State)(int)wParam;
            long now = Environment.TickCount64;
            if (s == State.Hidden)
            {
                if (visible && hideStart == 0) hideStart = now;
                return IntPtr.Zero;
            }
            // A confirmation is as wide as its text; everything else uses the standard pill.
            float want = s == State.Notice ? Math.Clamp(104 + noticeText.Length * 8.4f, PillWBase, 460) : PillWBase;
            bool resize = visible && hideStart == 0 && Math.Abs(want - pillW) > 0.5f;
            pillW = want;
            if (!visible || hideStart != 0 || resize)
            {
                bool wasVisible = visible && hideStart == 0;
                Place(h);
                if (!wasVisible) showStart = now;       // a plain resize keeps the pill where it is, no re-fade
                hideStart = 0;
                stateStart = now - FadeStateMs;
                level = 0;
                visible = true;
                SetTimer(h, (IntPtr)1, 16, IntPtr.Zero);
            }
            else if (drawState != s) stateStart = now;
            drawState = s;
            Render();
            return IntPtr.Zero;
        }
        if (msg == WM_TIMER) { if (visible) Render(); return IntPtr.Zero; }
        return DefWindowProc(h, msg, wParam, lParam);
    }

    // Sizes the window and its backing bitmap for the monitor under the cursor.
    static void Place(IntPtr h)
    {
        GetCursorPos(out var pt);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var mon = MonitorFromPoint(pt, 2 /* nearest */);
        GetMonitorInfo(mon, ref mi);
        // Scale for the monitor the pill will appear on. (GetDpiForWindow reports the monitor the window was
        // LAST on, which made the pill tiny or huge when the cursor was on a different-DPI screen.)
        uint dpi = 96;
        try { if (GetDpiForMonitor(mon, 0 /* effective */, out uint dx, out _) == 0 && dx >= 96) dpi = dx; } catch { }
        scale = dpi / 96f;
        winW = (int)Math.Ceiling((pillW + Margin * 2) * scale);
        winH = (int)Math.Ceiling((PillH + Margin * 2) * scale);
        winX = mi.rcWork.left + (mi.rcWork.right - mi.rcWork.left - winW) / 2;
        winY = mi.rcWork.top + (int)(4 * scale);
        FreeSurface();

        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = winW, biHeight = -winH, biPlanes = 1, biBitCount = 32 };
        memDc = CreateCompatibleDC(IntPtr.Zero);
        dib = CreateDIBSection(memDc, ref bi, 0, out bits, IntPtr.Zero, 0);
        oldBmp = SelectObject(memDc, dib);
        GdipCreateBitmapFromScan0(winW, winH, winW * 4, 0x000E200B /* 32bppPARGB */, bits, out bitmap);
        GdipGetImageGraphicsContext(bitmap, out g);
        GdipSetSmoothingMode(g, 4);        // anti-alias
        GdipSetTextRenderingHint(g, 5);    // ClearType (grid-fit): crisp, because the pill interior is opaque
        foreach (var name in new[] { "Segoe UI Semibold", "Segoe UI" })
            if (GdipCreateFontFamilyFromName(name, IntPtr.Zero, out fontFamily) == 0) break;
        GdipCreateFont(fontFamily, (float)Math.Round(15 * scale), 0, 2 /* pixel */, out font);
        GdipCreateStringFormat(0x1000 /* NoWrap */, 0, out format);
        GdipSetStringFormatLineAlign(format, 1 /* center */);
        SetWindowPos(h, (IntPtr)(-1) /* TOPMOST */, winX, winY, winW, winH, 0x0010 /* NOACTIVATE */ | 0x0040 /* SHOWWINDOW */);
    }

    static void FreeSurface()
    {
        if (font != IntPtr.Zero) { GdipDeleteFont(font); font = IntPtr.Zero; }
        if (fontFamily != IntPtr.Zero) { GdipDeleteFontFamily(fontFamily); fontFamily = IntPtr.Zero; }
        if (format != IntPtr.Zero) { GdipDeleteStringFormat(format); format = IntPtr.Zero; }
        if (g != IntPtr.Zero) { GdipDeleteGraphics(g); g = IntPtr.Zero; }
        if (bitmap != IntPtr.Zero) { GdipDisposeImage(bitmap); bitmap = IntPtr.Zero; }
        if (memDc != IntPtr.Zero) { SelectObject(memDc, oldBmp); DeleteDC(memDc); memDc = IntPtr.Zero; }
        if (dib != IntPtr.Zero) { DeleteObject(dib); dib = IntPtr.Zero; }
    }

    static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    static float EaseOut(float t) { t = 1 - Clamp01(t); return 1 - t * t * t; }
    static uint Argb(uint c, float alpha) => ((uint)(Clamp01(alpha) * (c >> 24)) << 24) | (c & 0x00FFFFFF);

    static void RoundRect(IntPtr path, float x, float y, float w, float h, float r)
    {
        float d = Math.Min(r * 2, Math.Min(w, h));
        GdipAddPathArc(path, x, y, d, d, 180, 90);
        GdipAddPathArc(path, x + w - d, y, d, d, 270, 90);
        GdipAddPathArc(path, x + w - d, y + h - d, d, d, 0, 90);
        GdipAddPathArc(path, x, y + h - d, d, d, 90, 90);
        GdipClosePathFigure(path);
    }

    static void FillRound(float x, float y, float w, float h, float r, uint argb)
    {
        GdipCreatePath(0, out var path);
        RoundRect(path, x, y, w, h, r);
        GdipCreateSolidFill(argb, out var br);
        GdipFillPath(g, br, path);
        GdipDeleteBrush(br);
        GdipDeletePath(path);
    }

    static void Render()
    {
        if (g == IntPtr.Zero) return;
        long now = Environment.TickCount64;
        float s = scale, ease, dy;
        if (hideStart != 0)
        {
            float t = Clamp01((now - hideStart) / (float)FadeOutMs);
            if (t >= 1)
            {
                ShowWindow(hwnd, 0);
                KillTimer(hwnd, (IntPtr)1);
                visible = false;
                hideStart = 0;
                return;
            }
            ease = 1 - t; dy = -4 * s * t;
        }
        else
        {
            ease = EaseOut((now - showStart) / (float)FadeInMs);
            dy = (ease - 1) * 10 * s;
        }
        float content = Clamp01((now - stateStart) / (float)FadeStateMs);

        GdipGraphicsClear(g, 0);
        float m = Margin * s, w = pillW * s, h = PillH * s, r = h / 2;

        // Soft shadow: a few translucent rings under the pill.
        for (int i = 5; i >= 1; i--)
            FillRound(m - i * s * 1.4f, m - i * s * 1.4f + 3 * s, w + i * s * 2.8f, h + i * s * 2.8f, r + i * s * 1.4f, (uint)(5 + (6 - i) * 2) << 24);

        // Pill: hairline border, then an opaque fill 1px inside it (opaque so text can be ClearType).
        FillRound(m, m, w, h, r, Border);
        FillRound(m + s, m + s, w - 2 * s, h - 2 * s, r - s, Fill);
        GdipFlush(g, 1);

        // Bars (left) and label; text sits on whole pixels.
        float cy = m + h / 2;
        float iconX = m + 22 * s, iconW = 28 * s;
        if (drawState == State.Notice) Check(iconX, iconW, cy, s, content);
        else Bars(iconX, iconW, cy, s, now, content);

        string label = drawState == State.Listening ? "Listening" : drawState == State.Assistant ? "Luna" : drawState == State.Thinking ? "Thinking"
                     : drawState == State.Notice ? noticeText : "Transcribing";
        var rect = new RectF { x = (float)Math.Round(iconX + iconW + 14 * s), y = (float)Math.Round(m), w = w - (iconW + 14 * s + 22 * s) - 6 * s, h = (float)Math.Round(h) };
        GdipCreateSolidFill(Argb(Live || drawState == State.Notice ? Ink : Text2, content), out var tb);
        GdipDrawString(g, label, -1, font, ref rect, format, tb);
        GdipDeleteBrush(tb);

        GdipFlush(g, 1);
        var dst = new POINT { x = winX, y = winY + (int)dy };
        var size = new SIZE { cx = winW, cy = winH };
        var src = new POINT();
        var bf = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = (byte)(255 * Clamp01(ease)), AlphaFormat = 1 /* AC_SRC_ALPHA */ };
        UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dst, ref size, memDc, ref src, 0, ref bf, 2 /* ULW_ALPHA */);
    }

    // Five rounded bars. Listening: they rise with the mic level. Transcribing: a slow wave runs through them.
    static void Bars(float x, float w, float cy, float s, long now, float content)
    {
        double t = now / 1000.0;
        if (Live)
        {
            float raw = 0;
            try { raw = LevelSource?.Invoke() ?? 0; } catch { }
            float target = Clamp01((float)Math.Sqrt(raw) * 2.6f);
            level += (target - level) * (target > level ? 0.6f : 0.14f);
        }
        float[] weight = { .55f, .82f, 1f, .82f, .55f };
        float barW = 3.4f * s, gap = (w - 5 * barW) / 4, maxH = 24 * s, minH = 4.5f * s;
        for (int i = 0; i < 5; i++)
        {
            float v;
            if (Live)
            {
                float idle = 0.12f + 0.08f * (float)Math.Sin(t * 5.5 + i * 1.3);
                float wob = 0.72f + 0.28f * (float)Math.Sin(t * 11 + i * 2.1);
                v = Clamp01(idle + level * weight[i] * wob);
            }
            else v = 0.16f + 0.34f * (0.5f + 0.5f * (float)Math.Sin(t * 4.2 - i * 0.9));
            float bh = minH + v * (maxH - minH), bx = x + i * (barW + gap);
            FillRound(bx, cy - bh / 2, barW, bh, barW / 2, Argb(Ice, content));
        }
    }

    delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS
    {
        public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName;
    }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct RectF { public float x, y, w, h; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct GdiplusStartupInput { public uint GdiplusVersion; public IntPtr DebugEventCallback; public int SuppressBackgroundThread, SuppressExternalCodecs; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClass(ref WNDCLASS wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dstDc, ref POINT dst, ref SIZE size, IntPtr srcDc, ref POINT src, uint key, ref BLENDFUNCTION bf, uint flags);
    [DllImport("user32.dll")] static extern UIntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] static extern bool KillTimer(IntPtr h, IntPtr id);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int dpiType, out uint x, out uint y);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdiplus.dll")] static extern int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, IntPtr output);
    [DllImport("gdiplus.dll")] static extern int GdipCreateBitmapFromScan0(int w, int h, int stride, int format, IntPtr scan0, out IntPtr bmp);
    [DllImport("gdiplus.dll")] static extern int GdipGetImageGraphicsContext(IntPtr image, out IntPtr g);
    [DllImport("gdiplus.dll")] static extern int GdipDisposeImage(IntPtr image);
    [DllImport("gdiplus.dll")] static extern int GdipDeleteGraphics(IntPtr g);
    [DllImport("gdiplus.dll")] static extern int GdipFlush(IntPtr g, int intent);
    [DllImport("gdiplus.dll")] static extern int GdipSetSmoothingMode(IntPtr g, int mode);
    [DllImport("gdiplus.dll")] static extern int GdipSetTextRenderingHint(IntPtr g, int mode);
    [DllImport("gdiplus.dll")] static extern int GdipGraphicsClear(IntPtr g, uint argb);
    [DllImport("gdiplus.dll")] static extern int GdipCreateSolidFill(uint argb, out IntPtr brush);
    [DllImport("gdiplus.dll")] static extern int GdipDeleteBrush(IntPtr brush);
    [DllImport("gdiplus.dll")] static extern int GdipCreatePath(int fillMode, out IntPtr path);
    [DllImport("gdiplus.dll")] static extern int GdipDeletePath(IntPtr path);
    [DllImport("gdiplus.dll")] static extern int GdipAddPathArc(IntPtr path, float x, float y, float w, float h, float start, float sweep);
    [DllImport("gdiplus.dll")] static extern int GdipClosePathFigure(IntPtr path);
    [DllImport("gdiplus.dll")] static extern int GdipFillPath(IntPtr g, IntPtr brush, IntPtr path);
    [DllImport("gdiplus.dll")] static extern int GdipFillEllipse(IntPtr g, IntPtr brush, float x, float y, float w, float h);
    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)] static extern int GdipCreateFontFamilyFromName(string name, IntPtr collection, out IntPtr family);
    [DllImport("gdiplus.dll")] static extern int GdipDeleteFontFamily(IntPtr family);
    [DllImport("gdiplus.dll")] static extern int GdipCreateFont(IntPtr family, float size, int style, int unit, out IntPtr font);
    [DllImport("gdiplus.dll")] static extern int GdipDeleteFont(IntPtr font);
    [DllImport("gdiplus.dll")] static extern int GdipCreateStringFormat(int flags, int lang, out IntPtr format);
    [DllImport("gdiplus.dll")] static extern int GdipSetStringFormatLineAlign(IntPtr format, int align);
    [DllImport("gdiplus.dll")] static extern int GdipDeleteStringFormat(IntPtr format);
    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)] static extern int GdipDrawString(IntPtr g, string text, int len, IntPtr font, ref RectF rect, IntPtr format, IntPtr brush);
}
