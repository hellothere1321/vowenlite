using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VowenLite;

// `VowenLite.exe --shot <png>`: saves the window the user is looking at (or the screen under the cursor if there is
// none) so Luna can attach it to a request like "what's this error on my screen". Own short-lived process, so the
// daemon never loads System.Drawing.
static class Shot
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);

    // screen = the whole monitor instead of the active window; meta = also write "<left> <top> <width> <height> <imgW> <imgH>" to <path>.txt
    // (PcBridge maps click coordinates in the picture back to the screen with it).
    // circle = freeze the window and let the user draw red rings on it first (Circler); the rings are part of the saved picture.
    public static int Capture(string path, bool screen = false, int Max = 1800, bool meta = false, bool circle = false)
    {
        Native.SetProcessDpiAwarenessContext((IntPtr)(-4));
        Rectangle area = Rectangle.Empty;
        var h = GetForegroundWindow();
        if (screen && h != IntPtr.Zero && GetWindowRect(h, out var wr)) area = Screen.FromRectangle(Rectangle.FromLTRB(wr.left, wr.top, wr.right, wr.bottom)).Bounds;
        else if (h != IntPtr.Zero && !IsIconic(h) && GetWindowRect(h, out var r) && r.right - r.left > 200 && r.bottom - r.top > 150)
            area = Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);
        if (area.IsEmpty) area = Screen.FromPoint(Cursor.Position).Bounds;
        area.Intersect(SystemInformation.VirtualScreen);
        if (area.IsEmpty) return 1;

        using var bmp = new Bitmap(area.Width, area.Height);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
        if (circle && !Circler.Run(bmp, area)) return 3;   // Esc: the caller falls back to a plain shot

        // keep it readable but small: the model's cost grows with the picture size
        int iw = area.Width, ih = area.Height;
        if (Math.Max(area.Width, area.Height) > Max)
        {
            double k = (double)Max / Math.Max(area.Width, area.Height);
            using var small = new Bitmap((int)(area.Width * k), (int)(area.Height * k));
            iw = small.Width; ih = small.Height;
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(bmp, 0, 0, small.Width, small.Height);
            }
            small.Save(path, ImageFormat.Png);
        }
        else bmp.Save(path, ImageFormat.Png);
        if (meta) File.WriteAllText(path + ".txt", $"{area.Left} {area.Top} {area.Width} {area.Height} {iw} {ih}");
        return 0;
    }
}

// The frozen picture of the window, full size over the window itself, so it looks unchanged. Drag = draw a red stroke (as many as you
// like); Enter or right-click = done (strokes are burned into the bitmap); Esc = cancel. The hint bar is drawn live only, never saved.
sealed class Circler : Form
{
    readonly Bitmap bmp;
    readonly List<List<Point>> strokes = new();
    List<Point> cur;
    public bool Done;

    Circler(Bitmap b, Rectangle area)
    {
        bmp = b;
        AutoScaleMode = AutoScaleMode.None; FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false; TopMost = true; DoubleBuffered = true; Cursor = Cursors.Cross; KeyPreview = true;
        Bounds = area;
        Shown += (_, _) => { Bounds = area; Activate(); };
    }

    public static bool Run(Bitmap bmp, Rectangle area)
    {
        using var f = new Circler(bmp, area);
        using var t = new System.Windows.Forms.Timer { Interval = 90000 };   // walked away: give up
        t.Tick += (_, _) => f.Close(); t.Start();
        Application.Run(f);
        if (!f.Done) return false;
        using var g = Graphics.FromImage(bmp);
        f.DrawStrokes(g);
        return true;
    }

    float Width_ => Math.Max(4f, Math.Max(bmp.Width, bmp.Height) / 400f);
    void DrawStrokes(Graphics g)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(235, 255, 40, 40), Width_) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        foreach (var s in strokes)
        {
            if (s.Count > 1) g.DrawLines(pen, s.ToArray());
            else if (s.Count == 1) g.FillEllipse(pen.Brush, s[0].X - Width_ / 2, s[0].Y - Width_ / 2, Width_, Width_);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.DrawImageUnscaled(bmp, 0, 0);
        DrawStrokes(e.Graphics);
        const string hint = "Circle what you want Luna to look at   ·   Enter or right-click = done   ·   Esc = skip";
        using var font = new Font("Segoe UI", 11f, FontStyle.Bold);
        var sz = e.Graphics.MeasureString(hint, font);
        float x = (Width - sz.Width) / 2 - 14, y = 12;
        using (var br = new SolidBrush(Color.FromArgb(215, 20, 20, 24))) e.Graphics.FillRectangle(br, x, y, sz.Width + 28, sz.Height + 14);
        e.Graphics.DrawString(hint, font, Brushes.White, x + 14, y + 7);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle) { Done = true; Close(); return; }
        if (e.Button != MouseButtons.Left) return;
        cur = new List<Point> { e.Location }; strokes.Add(cur); Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (cur == null || (e.Button & MouseButtons.Left) == 0) return;
        cur.Add(e.Location); Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e) { cur = null; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Done = false; Close(); }
        else if (e.KeyCode == Keys.Enter) { Done = strokes.Count > 0; Close(); }
        else if (e.KeyCode == Keys.Z && e.Control && strokes.Count > 0) { strokes.RemoveAt(strokes.Count - 1); Invalidate(); }
    }
}
