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
    public static int Capture(string path, bool screen = false, int Max = 1800, bool meta = false)
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
