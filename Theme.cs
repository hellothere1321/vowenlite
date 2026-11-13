using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace VowenLite;

// Lithrum look: black ground, quiet grey surfaces, one ice-blue accent, pill controls.
static class Theme
{
    static Color Hex(string h) => Color.FromArgb(Convert.ToInt32(h.Substring(0, 2), 16), Convert.ToInt32(h.Substring(2, 2), 16), Convert.ToInt32(h.Substring(4, 2), 16));

    public static readonly Color Bg = Hex("000000"), S1 = Hex("0c0d10"), S2 = Hex("15171b"), S3 = Hex("24272e"),
        Ink = Hex("f3f4f6"), Text2 = Hex("c9ccd3"), Soft = Hex("9a9fa9"), Faint = Hex("7a7f89"),
        Ice = Hex("a9d8ff"), Err = Hex("ffb4a8"), Rec = Hex("ff6b5e");

    static readonly HashSet<string> installed = new(FontFamily.Families.Select(f => f.Name));
    static string Pick(params string[] names) => names.FirstOrDefault(installed.Contains) ?? "Segoe UI";

    public static readonly string Body = Pick("Segoe UI");   // classic Segoe UI: hinted, crisp in GDI (the Variable family renders soft)
    public static readonly string BodyBold = Pick("Segoe UI Semibold");
    public static readonly string Display = Pick("Segoe UI Light", "Segoe UI");
    public static readonly string DisplayBold = Pick("Segoe UI Semibold");
    public static readonly string Mono = Pick("Cascadia Mono", "Consolas");

    public static Font Font(string family, float pt) => new(family, pt, FontStyle.Regular, GraphicsUnit.Point);

    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // The Lithrum stone mark (assets/mark.svg), scaled into `box`.
    public static void DrawMark(Graphics g, RectangleF box, Color c)
    {
        float s = Math.Min(box.Width / 324f, box.Height / 414f);
        float ox = box.X + (box.Width - 324 * s) / 2, oy = box.Y + (box.Height - 414 * s) / 2;
        PointF P(float x, float y) => new(ox + (x - 378) * s, oy + (y - 333) * s);
        using var path = new GraphicsPath(FillMode.Alternate);
        path.AddPolygon(new[] { P(558, 405), P(630, 333), P(630, 477), P(522, 585), P(612, 675), P(540, 747), P(378, 585), P(450, 513) });
        path.AddPolygon(new[] { P(558, 585), P(630, 513), P(702, 585), P(702, 729), P(630, 657) });
        using var b = new SolidBrush(c);
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillPath(b, path);
        g.SmoothingMode = old;
    }

    public static Icon AppIcon()
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = Round(new RectangleF(0, 0, 64, 64), 14)) using (var br = new SolidBrush(S1)) g.FillPath(br, bg);
            DrawMark(g, new RectangleF(18, 14, 28, 36), Ice);
        }
        var h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); } finally { DestroyIcon(h); }
    }

    // Dark title bar + dark scrollbars (Windows 10 1809+/11); silently does nothing where unsupported.
    public static void DarkTitleBar(IntPtr hwnd)
    {
        int on = 1, black = 0, ink = Ink.R | Ink.G << 8 | Ink.B << 16;
        DwmSetWindowAttribute(hwnd, 20, ref on, 4);
        DwmSetWindowAttribute(hwnd, 35, ref black, 4);   // caption colour
        DwmSetWindowAttribute(hwnd, 36, ref ink, 4);     // caption text
        DwmSetWindowAttribute(hwnd, 34, ref black, 4);   // border
    }

    public static void DarkScroll(Control c)
    {
        void Apply() { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } }
        if (c.IsHandleCreated) Apply(); else c.HandleCreated += (_, _) => Apply();
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string subApp, string subIdList);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
}
