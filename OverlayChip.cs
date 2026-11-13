using System.Runtime.InteropServices;

namespace VowenLite;

// Confirmation pill and the bottom-right card stack (Luna working, timers, stopwatch, notes; see Cards.cs).
// Same GDI+ / layered-window approach as the main pill (Overlay.cs).
static partial class Overlay
{
    // ---- check-mark confirmation ("Timer set · 10 minutes"), shown in the normal pill ----------------

    static System.Threading.Timer noticeTimer;   // field: a Timer nobody references can be collected before it fires

    static bool noticeError;

    public static void Notice(string text, int ms = 0, bool error = false)
    {
        if (hwnd == IntPtr.Zero) return;
        noticeText = text.TrimStart('✓', ' ');
        noticeError = error;
        long seq = Interlocked.Increment(ref noticeSeq);
        Set(State.Notice);
        int dur = ms > 0 ? ms : Math.Clamp(1500 + noticeText.Length * 45, 1800, 4200);
        noticeTimer?.Dispose();
        noticeTimer = new System.Threading.Timer(_ =>
        {
            // Only hide our own confirmation, never a dictation pill that appeared meanwhile.
            if (Interlocked.Read(ref noticeSeq) == seq && drawState == State.Notice) Set(State.Hidden);
        }, null, dur, Timeout.Infinite);
    }

    static void Check(float x, float w, float cy, float s, float content)
    {
        GdipCreatePen1(Argb(noticeError ? 0xFFFFB4A8 : Ice, content), 2.8f * s, 2, out var pen);
        GdipSetPenStartCap(pen, 2); GdipSetPenEndCap(pen, 2);   // round
        float bx = x + 3 * s, by = cy;
        if (noticeError)
        {
            // a cross instead of a check mark
            GdipDrawLine(g, pen, bx + 4 * s, by - 8 * s, bx + 18 * s, by + 8 * s);
            GdipDrawLine(g, pen, bx + 18 * s, by - 8 * s, bx + 4 * s, by + 8 * s);
        }
        else
        {
            GdipDrawLine(g, pen, bx, by, bx + 7 * s, by + 7 * s);
            GdipDrawLine(g, pen, bx + 7 * s, by + 7 * s, bx + 20 * s, by - 8 * s);
        }
        GdipDeletePen(pen);
    }

    // ---- card stack: rows from Cards.Snapshot(), stacked upward from the bottom-right corner ----------

    const uint WM_APP_CHIP = 0x8002;
    const float ChipW = 260, ChipH = 40, RowGap = 8;
    public static Func<List<Row>> RowSource = Cards.Snapshot;

    static IntPtr chipHwnd, chipDc, chipDib, chipBits, chipBmp, chipG, chipFam, chipTimeFont, chipLabelFont, chipFmt, chipOld;
    static WndProc chipProc;
    static int chipWinW, chipWinH, chipX, chipY, chipRows, chipInterval;
    static float chipScale = 1;
    static bool chipVisible;
    static RECT chipWork;

    static void CreateChip()
    {
        chipProc = ChipProc;
        var wc = new WNDCLASS { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(chipProc), hInstance = GetModuleHandle(null), lpszClassName = "VowenLiteChip" };
        RegisterClass(ref wc);
        const uint WS_POPUP = 0x80000000;
        // Not click-through: the cards can be clicked. Fully transparent pixels still let clicks fall through to the app below,
        // and NOACTIVATE means clicking a card never steals focus from what you're typing in.
        const uint WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
        chipHwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE,
            wc.lpszClassName, "VowenLite cards", WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    // ---- mouse: hover highlight, x button, click / right-click ----

    static List<Row> chipRowsNow = new();
    static int chipHot = -1;
    static bool chipHotX, chipTracking;

    static (int Row, bool OnX) ChipHit(int px, int py)
    {
        float s = chipScale, m = Margin * s, w = ChipW * s, h = ChipH * s, gap = RowGap * s;
        if (px < m || px > m + w) return (-1, false);
        for (int i = 0; i < chipRowsNow.Count; i++)
        {
            float top = m + i * (h + gap);
            if (py >= top && py <= top + h) return (i, px >= m + w - 38 * s);
        }
        return (-1, false);
    }

    static IntPtr ChipMouse(IntPtr h, uint msg, IntPtr lParam)
    {
        int x = (short)((long)lParam & 0xFFFF), y = (short)(((long)lParam >> 16) & 0xFFFF);
        var (row, onX) = ChipHit(x, y);
        switch (msg)
        {
            case 0x0200: // WM_MOUSEMOVE
                if (!chipTracking) { var t = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 2, hwndTrack = h }; TrackMouseEvent(ref t); chipTracking = true; }
                if (row != chipHot || onX != chipHotX) { chipHot = row; chipHotX = onX; if (chipRowsNow.Count > 0) ChipRender(chipRowsNow); }
                break;
            case 0x02A3: // WM_MOUSELEAVE
                chipTracking = false;
                if (chipHot != -1) { chipHot = -1; chipHotX = false; if (chipRowsNow.Count > 0) ChipRender(chipRowsNow); }
                break;
            case 0x0201: // WM_LBUTTONDOWN
            case 0x0204: // WM_RBUTTONDOWN
                if (row >= 0 && row < chipRowsNow.Count && chipRowsNow[row].Id != null)
                {
                    string id = chipRowsNow[row].Id;
                    Feedback.Tick();
                    if (msg == 0x0204 || onX) Cards.Dismiss(id); else Cards.Activate(id);
                }
                break;
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
    [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT t);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr inst, IntPtr name);
    [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr cursor);

    // Any thread: something in the stack changed (a timer was added, Luna started or finished, a note was pinned...).
    public static void ChipRefresh()
    {
        if (chipHwnd != IntPtr.Zero) PostMessage(chipHwnd, WM_APP_CHIP, IntPtr.Zero, IntPtr.Zero);
    }

    static IntPtr ChipProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_CHIP || msg == WM_TIMER) { ChipTick(); return IntPtr.Zero; }
        if (msg == 0x0200 || msg == 0x02A3 || msg == 0x0201 || msg == 0x0204) return ChipMouse(h, msg, lParam);
        if (msg == 0x0021) return (IntPtr)3;                                   // WM_MOUSEACTIVATE -> MA_NOACTIVATE
        if (msg == 0x0020) { SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)32649)); return (IntPtr)1; }   // WM_SETCURSOR: hand
        return DefWindowProc(h, msg, wParam, lParam);
    }

    static void ChipTick()
    {
        var rows = RowSource?.Invoke() ?? new();
        if (rows.Count == 0)
        {
            if (chipVisible) { DetailHide(); ShowWindow(chipHwnd, 0); KillTimer(chipHwnd, (IntPtr)2); chipVisible = false; chipInterval = 0; chipRows = 0; }
            return;
        }
        if (!chipVisible || rows.Count != chipRows)
        {
            chipRows = rows.Count;
            chipHot = -1; chipHotX = false;
            ChipPlace(rows.Count);
            chipVisible = true;
        }
        // Smooth spinner while Luna is working, otherwise once a second (negligible CPU).
        int want = detScrolling || rows.Any(r => r.Icon == CardIcon.Spinner || r.Icon == CardIcon.Bell) ? 50 : 1000;
        if (want != chipInterval) { SetTimer(chipHwnd, (IntPtr)2, (uint)want, IntPtr.Zero); chipInterval = want; }
        ChipRender(rows);
    }

    static void ChipPlace(int rows)
    {
        // Cards live on the primary (default) monitor, not the one the cursor is on; only the dictation pill follows the cursor.
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var mon = MonitorFromPoint(new POINT { x = 0, y = 0 }, 1 /* DEFAULTTOPRIMARY */);
        GetMonitorInfo(mon, ref mi);
        chipWork = mi.rcWork;
        uint dpi = 96;
        try { if (GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 && dx >= 96) dpi = dx; } catch { }
        chipScale = dpi / 96f;
        chipWinW = (int)Math.Ceiling((ChipW + Margin * 2) * chipScale);
        chipWinH = (int)Math.Ceiling((rows * ChipH + (rows - 1) * RowGap + Margin * 2) * chipScale);
        chipX = mi.rcWork.right - chipWinW - (int)(4 * chipScale);
        chipY = mi.rcWork.bottom - chipWinH - (int)(2 * chipScale);   // bottom-right, just above the taskbar (the top-right has window buttons)
        ChipFree();

        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = chipWinW, biHeight = -chipWinH, biPlanes = 1, biBitCount = 32 };
        chipDc = CreateCompatibleDC(IntPtr.Zero);
        chipDib = CreateDIBSection(chipDc, ref bi, 0, out chipBits, IntPtr.Zero, 0);
        chipOld = SelectObject(chipDc, chipDib);
        GdipCreateBitmapFromScan0(chipWinW, chipWinH, chipWinW * 4, 0x000E200B, chipBits, out chipBmp);
        GdipGetImageGraphicsContext(chipBmp, out chipG);
        GdipSetSmoothingMode(chipG, 4);
        GdipSetTextRenderingHint(chipG, 5);
        foreach (var name in new[] { "Segoe UI Semibold", "Segoe UI" })
            if (GdipCreateFontFamilyFromName(name, IntPtr.Zero, out chipFam) == 0) break;
        GdipCreateFont(chipFam, (float)Math.Round(15 * chipScale), 0, 2, out chipTimeFont);
        GdipCreateFont(chipFam, (float)Math.Round(13 * chipScale), 0, 2, out chipLabelFont);
        GdipCreateStringFormat(0x1000 /* NoWrap */, 0, out chipFmt);
        GdipSetStringFormatLineAlign(chipFmt, 1);
        GdipSetStringFormatTrimming(chipFmt, 3 /* ellipsis */);
        SetWindowPos(chipHwnd, (IntPtr)(-1), chipX, chipY, chipWinW, chipWinH, 0x0010 | 0x0040);
    }


    // ---- hover panel: what Luna is thinking / the finished answer, in a click-through window to the left of the stack ----

    static IntPtr detHwnd;
    static string detText;
    static int detRow = -1, detOff = -1;
    static long detStart;
    static bool detFollow, detScrolling;   // a long answer is sliding up: tick fast enough to look smooth

    static void DetailSync(List<Row> rows)
    {
        if (chipHot >= 0 && chipHot < rows.Count && !chipHotX && !string.IsNullOrEmpty(rows[chipHot].Detail)) DetailShow(rows[chipHot].Detail, chipHot, rows[chipHot].Icon == CardIcon.Spinner);
        else DetailHide();
    }

    static void DetailHide()
    {
        if (detHwnd != IntPtr.Zero && detText != null) ShowWindow(detHwnd, 0);
        detText = null; detRow = -1; detOff = -1; detScrolling = false;
    }

    // follow = a working card: keep the newest line in view. Otherwise (a finished answer) start at the top and slowly scroll down.
    static void DetailShow(string text, int row, bool follow)
    {
        float s = chipScale, pad = 14 * s, textW = 330 * s, maxTextH = 260 * s;
        if (row != detRow || follow != detFollow) { detStart = Environment.TickCount64; detFollow = follow; }

        GdipCreateStringFormat(0, 0, out var fmt);
        GdipSetStringFormatTrimming(fmt, 3);
        var lay = new RectF { x = 0, y = 0, w = textW, h = 100000 };
        GdipMeasureString(chipG, text, -1, chipLabelFont, ref lay, fmt, out var bounds, out _, out _);
        float fullH = bounds.h + 4 * s, textH = Math.Min(fullH, maxTextH), overflow = Math.Max(0, fullH - textH);
        float scrollY = follow ? overflow : Math.Clamp((Environment.TickCount64 - detStart - 1800) / 1000f * 26 * s, 0, overflow);
        int off = (int)scrollY;
        detScrolling = !follow && scrollY < overflow;
        if (text == detText && row == detRow && off == detOff) { GdipDeleteStringFormat(fmt); return; }   // unchanged: don't repaint (the card re-renders 20x a second)
        detText = text; detRow = row; detOff = off;
        int w = (int)Math.Ceiling(textW + 2 * pad), h = (int)Math.Ceiling(textH + 2 * pad);

        float right = chipX + Margin * s - 10 * s;
        float rowBottom = chipY + Margin * s + row * (ChipH + RowGap) * s + ChipH * s;
        int x = (int)(right - w), y = (int)Math.Max(chipWork.top + 4 * s, rowBottom - h);
        if (x < chipWork.left + 4) x = chipWork.left + 4;

        if (detHwnd == IntPtr.Zero)
        {
            const uint WS_POPUP = 0x80000000, WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
            detHwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE,
                "VowenLiteChip", "VowenLite detail", WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        }

        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        IntPtr dib = CreateDIBSection(dc, ref bi, 0, out var bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(dc, dib);
        GdipCreateBitmapFromScan0(w, h, w * 4, 0x000E200B, bits, out var bmp);
        GdipGetImageGraphicsContext(bmp, out var gr);
        GdipSetSmoothingMode(gr, 4);
        GdipSetTextRenderingHint(gr, 5);

        void Round(float rx, float ry, float rw, float rh, float rr, uint argb)
        {
            GdipCreatePath(0, out var path);
            RoundRect(path, rx, ry, rw, rh, rr);
            GdipCreateSolidFill(argb, out var br);
            GdipFillPath(gr, br, path);
            GdipDeleteBrush(br);
            GdipDeletePath(path);
        }
        GdipGraphicsClear(gr, 0);
        Round(0, 0, w, h, 16 * s, Border);
        Round(s, s, w - 2 * s, h - 2 * s, 15 * s, Fill);
        GdipCreateSolidFill(0xFFC9CCD3, out var tb);
        GdipSetClipRect(gr, pad, pad, textW, textH, 0 /* replace */);
        var rect = new RectF { x = pad, y = pad - off, w = textW, h = fullH };
        GdipDrawString(gr, text, -1, chipLabelFont, ref rect, fmt, tb);
        GdipResetClip(gr);
        if (overflow > 0)
        {
            // a thin scroll indicator on the right edge
            float trackH = textH, thumbH = Math.Max(18 * s, trackH * textH / fullH), thumbY = pad + (trackH - thumbH) * (overflow > 0 ? off / overflow : 0);
            Round(w - 7 * s, thumbY, 3 * s, thumbH, 1.5f * s, 0xFF4A4F5A);
        }
        GdipDeleteBrush(tb);
        GdipFlush(gr, 1);

        var dst = new POINT { x = x, y = y };
        var size = new SIZE { cx = w, cy = h };
        var src = new POINT();
        var bf = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        UpdateLayeredWindow(detHwnd, IntPtr.Zero, ref dst, ref size, dc, ref src, 0, ref bf, 2);
        SetWindowPos(detHwnd, (IntPtr)(-1), x, y, w, h, 0x0010 | 0x0040);

        GdipDeleteGraphics(gr);
        GdipDisposeImage(bmp);
        SelectObject(dc, old);
        DeleteDC(dc);
        DeleteObject(dib);
        GdipDeleteStringFormat(fmt);
    }

    [DllImport("gdiplus.dll")] static extern int GdipSetClipRect(IntPtr g, float x, float y, float w, float h, int mode);
    [DllImport("gdiplus.dll")] static extern int GdipResetClip(IntPtr g);
    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)] static extern int GdipMeasureString(IntPtr g, string text, int len, IntPtr font, ref RectF layout, IntPtr format, out RectF bounds, out int codepointsFitted, out int linesFilled);

    static void ChipFree()
    {
        if (chipTimeFont != IntPtr.Zero) { GdipDeleteFont(chipTimeFont); chipTimeFont = IntPtr.Zero; }
        if (chipLabelFont != IntPtr.Zero) { GdipDeleteFont(chipLabelFont); chipLabelFont = IntPtr.Zero; }
        if (chipFam != IntPtr.Zero) { GdipDeleteFontFamily(chipFam); chipFam = IntPtr.Zero; }
        if (chipFmt != IntPtr.Zero) { GdipDeleteStringFormat(chipFmt); chipFmt = IntPtr.Zero; }
        if (chipG != IntPtr.Zero) { GdipDeleteGraphics(chipG); chipG = IntPtr.Zero; }
        if (chipBmp != IntPtr.Zero) { GdipDisposeImage(chipBmp); chipBmp = IntPtr.Zero; }
        if (chipDc != IntPtr.Zero) { SelectObject(chipDc, chipOld); DeleteDC(chipDc); chipDc = IntPtr.Zero; }
        if (chipDib != IntPtr.Zero) { DeleteObject(chipDib); chipDib = IntPtr.Zero; }
    }

    static void ChipFillRound(float x, float y, float w, float h, float r, uint argb)
    {
        GdipCreatePath(0, out var path);
        RoundRect(path, x, y, w, h, r);
        GdipCreateSolidFill(argb, out var br);
        GdipFillPath(chipG, br, path);
        GdipDeleteBrush(br);
        GdipDeletePath(path);
    }

    static void ChipText(string text, IntPtr f, float x, float y, float w, float h, uint argb)
    {
        var rect = new RectF { x = (float)Math.Round(x), y = (float)Math.Round(y), w = w, h = h };
        GdipCreateSolidFill(argb, out var br);
        GdipDrawString(chipG, text, -1, f, ref rect, chipFmt, br);
        GdipDeleteBrush(br);
    }

    static void ChipRender(List<Row> rows)
    {
        if (chipG == IntPtr.Zero) return;
        float s = chipScale, m = Margin * s, w = ChipW * s, h = ChipH * s, r = h / 2;
        long now = Environment.TickCount64;
        chipRowsNow = rows;
        GdipGraphicsClear(chipG, 0);

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            float top = m + i * (h + RowGap * s);
            bool hot = i == chipHot && row.Id != null, bell = row.Icon == CardIcon.Bell;
            for (int k = 5; k >= 1; k--)
                ChipFillRound(m - k * s * 1.4f, top - k * s * 1.4f + 3 * s, w + k * s * 2.8f, h + k * s * 2.8f, r + k * s * 1.4f, (uint)(5 + (6 - k) * 2) << 24);
            // A ringing card has an ice border; a hovered card lights up.
            ChipFillRound(m, top, w, h, r, hot ? 0xFF7FA6C9 : bell ? Ice : Border);
            ChipFillRound(m + s, top + s, w - 2 * s, h - 2 * s, r - s, hot ? 0xFF14171C : Fill);

            float cx = m + 24 * s, cy = top + h / 2, rr = 9.5f * s;
            ChipIcon(row, cx, cy, rr, s, now);

            float tx = m + 44 * s, reserve = row.Id != null ? 26 * s : 0;   // room for the x button
            if (row.Secondary.Length == 0)
                ChipText(row.Primary, chipLabelFont, tx, top, w - (tx - m) - 18 * s - reserve, h, Ink);   // notes: the text is the whole card
            else
            {
                ChipText(row.Primary, chipTimeFont, tx, top, 64 * s, h, Ink);
                ChipText(row.Secondary, chipLabelFont, tx + 62 * s, top, w - (tx - m) - 62 * s - 16 * s - reserve, h, 0xFF9A9FA9);
            }

            // x button: always on a ringing card, otherwise while hovered.
            if (row.Id != null && (hot || bell))
            {
                float bx = m + w - 24 * s;
                bool overX = hot && chipHotX;
                if (overX) ChipFillRound(bx - 11 * s, cy - 11 * s, 22 * s, 22 * s, 11 * s, 0xFF2A2D35);
                GdipCreatePen1(overX ? 0xFFFFB4A8 : 0xFF9A9FA9, 2f * s, 2, out var xp);
                GdipSetPenStartCap(xp, 2); GdipSetPenEndCap(xp, 2);
                GdipDrawLine(chipG, xp, bx - 4 * s, cy - 4 * s, bx + 4 * s, cy + 4 * s);
                GdipDrawLine(chipG, xp, bx + 4 * s, cy - 4 * s, bx - 4 * s, cy + 4 * s);
                GdipDeletePen(xp);
            }
        }

        DetailSync(rows);
        GdipFlush(chipG, 1);
        var dst = new POINT { x = chipX, y = chipY };
        var size = new SIZE { cx = chipWinW, cy = chipWinH };
        var src = new POINT();
        var bf = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        UpdateLayeredWindow(chipHwnd, IntPtr.Zero, ref dst, ref size, chipDc, ref src, 0, ref bf, 2);
    }

    static void ChipIcon(Row row, float cx, float cy, float rr, float s, long now)
    {
        switch (row.Icon)
        {
            case CardIcon.Ring:
            case CardIcon.Spinner:
            {
                GdipCreatePen1(0xFF2A2D35, 2.8f * s, 2, out var track);
                GdipDrawEllipse(chipG, track, cx - rr, cy - rr, rr * 2, rr * 2);
                GdipDeletePen(track);
                float start = -90, sweep;
                if (row.Icon == CardIcon.Spinner) { start = (now % 1100) / 1100f * 360f; sweep = 110; }
                else sweep = (float)(row.Frac * 359.9);
                if (sweep > 0.7f)
                {
                    GdipCreatePen1(Ice, 2.8f * s, 2, out var arc);
                    GdipSetPenStartCap(arc, 2); GdipSetPenEndCap(arc, 2);
                    GdipDrawArc(chipG, arc, cx - rr, cy - rr, rr * 2, rr * 2, start, sweep);
                    GdipDeletePen(arc);
                }
                break;
            }
            case CardIcon.Check:
            {
                GdipCreatePen1(Ice, 2.8f * s, 2, out var pen);
                GdipSetPenStartCap(pen, 2); GdipSetPenEndCap(pen, 2);
                GdipDrawLine(chipG, pen, cx - 7 * s, cy + 0.5f * s, cx - 2 * s, cy + 5.5f * s);
                GdipDrawLine(chipG, pen, cx - 2 * s, cy + 5.5f * s, cx + 8 * s, cy - 6 * s);
                GdipDeletePen(pen);
                break;
            }
            case CardIcon.Dot:
            {
                GdipCreateSolidFill(Ice, out var br);
                float d = 3.6f * s;
                GdipFillEllipse(chipG, br, cx - d, cy - d, d * 2, d * 2);
                GdipDeleteBrush(br);
                break;
            }
            case CardIcon.Play:
            {
                GdipCreateSolidFill(Ice, out var br);
                var tri = new[] { new PtF { x = cx - 4.5f * s, y = cy - 7 * s }, new PtF { x = cx - 4.5f * s, y = cy + 7 * s }, new PtF { x = cx + 7 * s, y = cy } };
                GdipFillPolygon(chipG, br, tri, 3, 0);
                GdipDeleteBrush(br);
                break;
            }
            case CardIcon.Bell:
            {
                // A bell that pulses while the alarm rings.
                float k = s * (1f + 0.14f * (float)Math.Sin(now / 130.0));
                GdipCreateSolidFill(Ice, out var br);
                var bell = new[]
                {
                    new PtF { x = cx, y = cy - 8.5f * k }, new PtF { x = cx + 3.8f * k, y = cy - 6.8f * k }, new PtF { x = cx + 5.8f * k, y = cy - 2f * k },
                    new PtF { x = cx + 6.2f * k, y = cy + 3.2f * k }, new PtF { x = cx + 8.4f * k, y = cy + 6.2f * k }, new PtF { x = cx - 8.4f * k, y = cy + 6.2f * k },
                    new PtF { x = cx - 6.2f * k, y = cy + 3.2f * k }, new PtF { x = cx - 5.8f * k, y = cy - 2f * k }, new PtF { x = cx - 3.8f * k, y = cy - 6.8f * k },
                };
                GdipFillPolygon(chipG, br, bell, bell.Length, 0);
                GdipFillEllipse(chipG, br, cx - 2.4f * k, cy + 6.8f * k, 4.8f * k, 4.2f * k);   // clapper
                GdipDeleteBrush(br);
                break;
            }
            case CardIcon.Pause:
                ChipFillRound(cx - 6 * s, cy - 6.5f * s, 4.2f * s, 13 * s, 1.5f * s, Ice);
                ChipFillRound(cx + 1.8f * s, cy - 6.5f * s, 4.2f * s, 13 * s, 1.5f * s, Ice);
                break;
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct PtF { public float x, y; }
    [DllImport("gdiplus.dll")] static extern int GdipFillPolygon(IntPtr g, IntPtr brush, PtF[] points, int count, int fillMode);
    [DllImport("gdiplus.dll")] static extern int GdipCreatePen1(uint argb, float width, int unit, out IntPtr pen);
    [DllImport("gdiplus.dll")] static extern int GdipDeletePen(IntPtr pen);
    [DllImport("gdiplus.dll")] static extern int GdipSetPenStartCap(IntPtr pen, int cap);
    [DllImport("gdiplus.dll")] static extern int GdipSetPenEndCap(IntPtr pen, int cap);
    [DllImport("gdiplus.dll")] static extern int GdipDrawLine(IntPtr g, IntPtr pen, float x1, float y1, float x2, float y2);
    [DllImport("gdiplus.dll")] static extern int GdipDrawArc(IntPtr g, IntPtr pen, float x, float y, float w, float h, float start, float sweep);
    [DllImport("gdiplus.dll")] static extern int GdipDrawEllipse(IntPtr g, IntPtr pen, float x, float y, float w, float h);
    [DllImport("gdiplus.dll")] static extern int GdipSetStringFormatTrimming(IntPtr format, int trimming);
}

// Short sounds so timers feel real: a two-note chime when one is set, a repeating alarm when it ends.
static class Feedback
{
    [DllImport("kernel32.dll")] static extern bool Beep(uint freq, uint ms);

    public static void Set() => Task.Run(() => { try { Beep(1175, 60); Beep(1568, 90); } catch { } });

    // One round of the alarm (the ringing card repeats it until dismissed).
    public static void Pattern() => Task.Run(() => { try { Beep(1047, 110); Beep(1319, 110); Beep(1568, 190); } catch { } });

    // Tiny click so dismissing a card feels physical.
    public static void Tick() => Task.Run(() => { try { Beep(880, 18); } catch { } });

    public static void Alarm() => Task.Run(() =>
    {
        try
        {
            for (int i = 0; i < 4; i++) { Beep(1047, 120); Beep(1319, 120); Beep(1568, 200); Thread.Sleep(260); }
        }
        catch { }
    });
}
