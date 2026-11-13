using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace VowenLite;

enum PillKind { Primary, Quiet, Ghost, Danger }

// Rounded pill button, drawn by hand so it matches the site's buttons.
sealed class Pill : Control
{
    public PillKind Kind = PillKind.Quiet;
    public Color? Dot;          // small dot before the label (record light)
    public bool DotLit = true;
    public bool Chevron;        // trailing ▾ for dropdown-style pills
    public int MinWidth, MaxWidth = 10000;
    bool hover, down;

    public Pill(string text, PillKind kind = PillKind.Quiet)
    {
        Kind = kind;
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
        Font = Theme.Font(Theme.BodyBold, 9.5f);
        AutoSize = true;
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, 8, 0);
    }

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    int Extra => (Dot != null ? S(16) : 0) + (Chevron ? S(20) : 0);

    public override Size GetPreferredSize(Size proposed)
    {
        int tw = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        int w = Math.Clamp(tw + Extra + S(36), Math.Max(MinWidth, 0), MaxWidth);
        return new Size(w, S(36));
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (IsHandleCreated) { PerformLayout(); Invalidate(); } }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); hover = down = false; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Focus(); Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode is Keys.Space or Keys.Enter) OnClick(EventArgs.Empty); base.OnKeyUp(e); }

    public void Redraw() { PerformLayout(); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        Color fill, fore;
        bool on = Enabled;
        switch (Kind)
        {
            case PillKind.Primary:
                fill = !on ? Theme.S3 : hover ? Theme.Mix(Theme.Ink, Theme.Bg, .16f) : Theme.Ink;
                fore = on ? Theme.Bg : Theme.Faint; break;
            case PillKind.Ghost:
                fill = on && hover ? Theme.S2 : Color.Transparent;
                fore = !on ? Theme.S3 : hover ? Theme.Ink : Theme.Soft; break;
            case PillKind.Danger:
                fill = on && hover ? Theme.S2 : Color.Transparent;
                fore = !on ? Theme.S3 : hover ? Theme.Err : Theme.Soft; break;
            default:
                fill = !on ? Theme.S1 : hover ? Theme.S3 : Theme.S2;
                fore = on ? Theme.Ink : Theme.Faint; break;
        }
        if (down && on) fill = Theme.Mix(fill == Color.Transparent ? Theme.S2 : fill, Theme.Bg, .25f);

        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        if (fill != Color.Transparent)
        {
            using var path = Theme.Round(r, r.Height / 2);
            using var br = new SolidBrush(fill);
            g.FillPath(br, path);
        }
        if (ShowFocusCues && Focused)
        {
            using var path = Theme.Round(new RectangleF(1, 1, Width - 3, Height - 3), (Height - 3) / 2f);
            using var pen = new Pen(Theme.Ice, 2);
            g.DrawPath(pen, path);
        }

        var inner = new Rectangle(S(18), 0, Width - S(36), Height);
        if (Dot != null)
        {
            int d = S(8), tw = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            int x = Chevron ? inner.X : Math.Max(inner.X, (Width - (d + S(8) + tw)) / 2);
            var c = DotLit || !on ? Dot.Value : Theme.Mix(Dot.Value, fill, .65f);
            using var br = new SolidBrush(on ? c : Theme.Faint);
            g.FillEllipse(br, x, (Height - d) / 2f, d, d);
            inner.Width -= x + d + S(8) - inner.X; inner.X = x + d + S(8);
            if (!Chevron) inner.Width = Width - inner.X - S(10);
        }
        if (Chevron) inner.Width -= S(20);
        TextRenderer.DrawText(g, Text, Font, inner, fore,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
            (Chevron || Dot != null ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter));
        if (Chevron)
        {
            float cx = Width - S(22), cy = Height / 2f, w = S(4);
            using var pen = new Pen(fore, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[] { new PointF(cx - w, cy - w / 2), new PointF(cx, cy + w / 2), new PointF(cx + w, cy - w / 2) });
        }
    }
}

// Small on/off switch with a label.
sealed class Toggle : Control
{
    bool on, hover;
    public event EventHandler CheckedChanged;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Checked { get => on; set { if (on == value) return; on = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); } }

    public Toggle(string text)
    {
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        Font = Theme.Font(Theme.Body, 9f);
        Cursor = Cursors.Hand;
        AutoSize = true;
    }

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    public override Size GetPreferredSize(Size proposed)
    {
        int tw = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
        return new Size(S(34) + S(10) + tw, S(24));
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnClick(EventArgs e) { Checked = !on; base.OnClick(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space) OnClick(EventArgs.Empty); base.OnKeyUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int tw = S(34), th = S(18), ty = (Height - th) / 2;
        using (var path = Theme.Round(new RectangleF(0.5f, ty + .5f, tw - 1, th - 1), th / 2f))
        using (var br = new SolidBrush(on ? Theme.Ice : hover ? Theme.S3 : Theme.S2))
            g.FillPath(br, path);
        if (ShowFocusCues && Focused)
            using (var path = Theme.Round(new RectangleF(-1.5f, ty - 1.5f, tw + 3, th + 3), (th + 3) / 2f))
            using (var pen = new Pen(Theme.Ice, 1.5f)) g.DrawPath(pen, path);
        int kd = th - S(6), kx = on ? tw - kd - S(3) : S(3);
        using (var br = new SolidBrush(on ? Theme.Bg : Theme.Soft)) g.FillEllipse(br, kx, ty + S(3), kd, kd);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(tw + S(10), 0, Width - tw - S(10), Height), hover ? Theme.Ink : Theme.Soft,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}

// Owner-drawn list of rounded rows (title + subtitle, optional state dot).
sealed class RowList : ListBox
{
    public sealed class Row
    {
        public string Title, Sub;
        public Color? Dot;
        public object Tag;
        public override string ToString() => Title;
    }

    int hot = -1;
    readonly Font fTitle = Theme.Font(Theme.BodyBold, 10f), fSub = Theme.Font(Theme.Body, 9f);

    public RowList()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.S1;
        IntegralHeight = false;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        ItemHeight = (int)Math.Round(62 * DeviceDpi / 96.0);
        Theme.DarkScroll(this);
    }

    public Row SelectedRow => SelectedItem as Row;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = IndexFromPoint(e.Location);
        if (i != hot) { hot = i; Invalidate(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { hot = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        for (int i = TopIndex; i < Items.Count; i++)
        {
            var r = GetItemRectangle(i);
            if (r.Top > Height) break;
            OnDrawItem(new DrawItemEventArgs(e.Graphics, Font, r, i, SelectedIndex == i ? DrawItemState.Selected : DrawItemState.None));
        }
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count || Items[e.Index] is not Row row) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        int m = (int)Math.Round(8 * DeviceDpi / 96.0), pad = (int)Math.Round(14 * DeviceDpi / 96.0);
        var rect = new RectangleF(e.Bounds.X + m, e.Bounds.Y + 2, e.Bounds.Width - m * 2, e.Bounds.Height - 4);
        bool sel = (e.State & DrawItemState.Selected) != 0;
        Color bg = sel ? Theme.S2 : e.Index == hot ? Theme.Mix(Theme.S1, Theme.S2, .5f) : Theme.S1;
        if (bg != Theme.S1)
        {
            using var path = Theme.Round(rect, 10 * DeviceDpi / 96f);
            using var br = new SolidBrush(bg);
            g.FillPath(br, path);
        }
        int dotW = row.Dot != null ? pad : 0;
        var title = new Rectangle((int)rect.X + pad, (int)rect.Y + (int)(rect.Height * .16f), (int)rect.Width - pad * 2 - dotW, (int)(rect.Height * .4f));
        var sub = new Rectangle(title.X, title.Bottom, title.Width, (int)(rect.Height * .34f));
        const TextFormatFlags f = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        TextRenderer.DrawText(g, row.Title, fTitle, title, Theme.Ink, f);
        TextRenderer.DrawText(g, row.Sub, fSub, sub, Theme.Soft, f);
        if (row.Dot != null)
        {
            float d = 8 * DeviceDpi / 96f;
            using var br = new SolidBrush(row.Dot.Value);
            g.FillEllipse(br, rect.Right - pad - d + 2, rect.Y + (rect.Height - d) / 2, d, d);
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) { fTitle.Dispose(); fSub.Dispose(); } base.Dispose(disposing); }
}

// Dark drop-down menu (used by the mic picker).
sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.S2;
        public override Color ImageMarginGradientBegin => Theme.S2;
        public override Color ImageMarginGradientMiddle => Theme.S2;
        public override Color ImageMarginGradientEnd => Theme.S2;
        public override Color MenuBorder => Theme.S3;
        public override Color MenuItemBorder => Theme.S3;
        public override Color MenuItemSelected => Theme.S3;
        public override Color MenuItemSelectedGradientBegin => Theme.S3;
        public override Color MenuItemSelectedGradientEnd => Theme.S3;
        public override Color SeparatorDark => Theme.S3;
        public override Color SeparatorLight => Theme.S3;
        public override Color CheckBackground => Theme.S3;
        public override Color CheckSelectedBackground => Theme.S3;
        public override Color CheckPressedBackground => Theme.S3;
    }
    public DarkMenuRenderer() : base(new Colors()) { RoundedEdges = false; }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Ink : Theme.Faint;
        base.OnRenderItemText(e);
    }
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = e.ImageRectangle;
        using var pen = new Pen(Theme.Ice, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[] { new PointF(r.X + r.Width * .22f, r.Y + r.Height * .55f), new PointF(r.X + r.Width * .42f, r.Y + r.Height * .75f), new PointF(r.X + r.Width * .78f, r.Y + r.Height * .3f) });
    }
}
