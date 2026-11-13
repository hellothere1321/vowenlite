using System.Drawing;
using System.Windows.Forms;

namespace VowenLite;

// Typing mode for Luna: the Copilot key or a double-tap of Right Ctrl -> this small box (own process, `--ask`).
// Enter sends the text to the daemon (file + named event), Esc or losing focus closes it. If a conversation was active in the last
// 10 minutes the box says it is continuing it and the request carries its thread id; Tab switches to a new conversation.
sealed class AskBox : Form
{
    public const string Event = "Local\\VowenLite.AskSubmit";
    public static string File => Path.Combine(Paths.DataDir, "ask-request.txt");
    const string Single = "Local\\VowenLite.AskOpen";

    readonly TextBox box = new();
    readonly Label hint = new() { AutoSize = false, ForeColor = Theme.Text2, BackColor = Theme.S2 };
    readonly ConvThread thread;   // newest conversation, if it is recent
    bool sent, fresh;

    public static int Run()
    {
        using var mutex = new Mutex(true, Single, out bool first);
        if (!first) return 0;   // already open
        ApplicationConfiguration.Initialize();
        Application.Run(new AskBox());
        return 0;
    }

    AskBox()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Theme.S2;
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int w = Math.Min(wa.Width - 40, (int)(640 * DeviceDpi / 96.0)), h = (int)(52 * DeviceDpi / 96.0);
        Bounds = new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Bottom - h - (int)(80 * DeviceDpi / 96.0), w, h);
        Padding = new Padding((int)(14 * DeviceDpi / 96.0), 0, (int)(14 * DeviceDpi / 96.0), 0);
        thread = Threads.Newest(TimeSpan.FromMinutes(10));
        if (thread != null) { h += (int)(18 * DeviceDpi / 96.0); Bounds = new Rectangle(Left, Top - (int)(18 * DeviceDpi / 96.0), Width, h); }

        box.BorderStyle = BorderStyle.None;
        box.BackColor = Theme.S2;
        box.ForeColor = Theme.Ink;
        box.Font = Theme.Font(Theme.Body, 13);
        box.PlaceholderText = "Ask Luna…  (Enter to send, Esc to cancel)";
        box.Dock = DockStyle.None;
        hint.Font = Theme.Font(Theme.Body, 9);
        hint.Visible = thread != null;
        Controls.Add(box);
        Controls.Add(hint);
        ShowHint();
        Resize += (_, _) => DoLayout();
        DoLayout();
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Close(); }
            else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Send(); }
        };
        Deactivate += (_, _) => { if (!sent) Close(); };
        Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new Pen(Theme.Ice, 1.5f);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        };
        Shown += (_, _) => { Theme.DarkTitleBar(Handle); Grab(); box.Focus(); };
    }

    [System.Runtime.InteropServices.DllImport("user32")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [System.Runtime.InteropServices.DllImport("user32")] static extern bool SetForegroundWindow(IntPtr h);

    // A process started from the daemon's key hook is not allowed to take focus; a synthetic Alt tap lifts that lock.
    void Grab()
    {
        keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);
        TopMost = true; BringToFront(); SetForegroundWindow(Handle); Activate();
    }

    void DoLayout()
    {
        int th = box.PreferredHeight;
        int top = 0;
        if (thread != null)
        {
            int hh = (int)(18 * DeviceDpi / 96.0);
            hint.SetBounds(Padding.Left, (int)(5 * DeviceDpi / 96.0), Width - Padding.Horizontal, hh);
            top = hh;
        }
        box.SetBounds(Padding.Left, top + (Height - top - th) / 2, Width - Padding.Horizontal, th);
    }

    void ShowHint()
    {
        if (thread == null) return;
        var words = (thread.Turns.LastOrDefault()?.Q ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string first = string.Join(" ", words.Take(6)) + (words.Length > 6 ? "…" : "");
        hint.Text = fresh ? "new conversation  ·  Tab to continue “" + first + "”" : "continuing: " + first + "  ·  Tab for a new conversation";
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Tab && thread != null) { fresh = !fresh; ShowHint(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void Send()
    {
        string text = box.Text.Trim();
        if (text.Length == 0) { Close(); return; }
        sent = true;
        try
        {
            System.IO.File.WriteAllText(File, Threads.Compose(thread == null ? null : fresh ? "new" : thread.Id, text));
            using var ev = EventWaitHandle.OpenExisting(Event);
            ev.Set();
        }
        catch { }
        Close();
    }

    protected override bool ShowWithoutActivation => false;
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } }   // tool window: no taskbar/alt-tab
}
