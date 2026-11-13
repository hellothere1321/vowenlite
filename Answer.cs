using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace VowenLite;

// Browser options for the small static windows (answer box, Ask panel, widgets). They SHARE the "webview" user-data
// folder, and WebView2 needs identical options from every process using one folder: change them in this one place.
// Software rendering + GPU thread in the browser process measured ~150 MB -> ~75 MB private for an open answer box.
static class WebOpts
{
    public static Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions Tiny() => new("--disable-gpu --in-process-gpu --disable-extensions --disable-background-networking --disable-sync --disable-component-update --disable-default-apps");
}
// The answer box (`VowenLite.exe --answer <file>`; the file is the request, a blank line, then the markdown reply).
// It appears at the bottom right of the MAIN monitor (left of the card stack) once the answer is complete, without taking focus, so it can
// be read while working: formatted text, tables, code and diagrams (Md.cs). It stays until closed (× / Esc when clicked into); a newer
// answer replaces it. Expand makes it tall; the header drags it. Its own process so the daemon never loads WinForms.
sealed class AnswerForm : Form
{
    readonly string request, answer, threadId;
    readonly List<Turn> earlier = new();   // earlier exchanges of this conversation (dimmed above the answer)
    readonly bool autoClose;
    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.S1 };
    EventWaitHandle closeOthers;
    Rectangle work;
    bool expanded;
    int contentHeight;

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x00000080 /* TOOLWINDOW: no Alt+Tab entry */ | 0x08000000 /* NOACTIVATE: never steals focus until the reply row is clicked */; return cp; } }

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public AnswerForm(string file)
    {
        string text = File.ReadAllText(file).Replace("\r\n", "\n");
        try { File.Delete(file); } catch { }
        var tm = System.Text.RegularExpressions.Regex.Match(text, @"^thread:([0-9a-zA-Z]{1,32})\n");   // optional header: the conversation this answer belongs to
        if (tm.Success) { threadId = tm.Groups[1].Value; text = text.Substring(tm.Length); }
        int split = text.IndexOf("\n\n", StringComparison.Ordinal);
        request = split > 0 ? text.Substring(0, split).Trim() : "";
        answer = split > 0 ? text.Substring(split + 2).Trim() : text.Trim();
        // "✓ ..." is a short confirmation: shown briefly.
        autoClose = answer.StartsWith("✓");
        if (autoClose) answer = answer.TrimStart('✓', ' ');
        if (autoClose) threadId = null;
        if (threadId != null)
        {
            var turns = Threads.Get(threadId)?.Turns ?? new List<Turn>();
            int n = turns.Count;
            if (n > 0 && turns[n - 1].Q == request) n--;   // the newest exchange is the answer shown below
            earlier.AddRange(turns.Skip(Math.Max(0, n - 3)).Take(Math.Min(n, 3)));
        }

        // A newer answer replaces the one on screen.
        try
        {
            if (EventWaitHandle.TryOpenExisting("Local\\VowenLite.AnswerClose", out var old)) { old.Set(); Thread.Sleep(120); old.Dispose(); }
            closeOthers = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\VowenLite.AnswerClose");
            closeOthers.Reset();
            var t = new Thread(() => { try { if (closeOthers.WaitOne()) BeginInvoke(Close); } catch { } }) { IsBackground = true };
            t.Start();
        }
        catch { }

        Text = "Luna";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Theme.S1;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        KeyPreview = true;
        work = Screen.PrimaryScreen.WorkingArea;                      // always the main monitor
        Size = new Size(S(480), S(autoClose ? 120 : 190));
        Place();
        Controls.Add(web);
        Load += async (_, _) => await Start();
    }

    // Bottom right, leaving the card column (timers, working cards) free at the very edge.
    void Place()
    {
        int cards = S(288);
        Location = new Point(work.Right - cards - Width, work.Bottom - Height - S(10));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int round = 2; DwmSetWindowAttribute(Handle, 33 /* corner preference */, ref round, 4);
            int border = 0x002E2724; DwmSetWindowAttribute(Handle, 34 /* border colour (BGR) */, ref border, 4);
            int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, 4);
        }
        catch { }
    }

    async Task Start()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.DataDir, "webview"), WebOpts.Tiny());
            await web.EnsureCoreWebView2Async(env);
            var st = web.CoreWebView2.Settings;
            st.AreDevToolsEnabled = false; st.IsStatusBarEnabled = false; st.AreDefaultContextMenusEnabled = false; st.IsZoomControlEnabled = false;
            web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith("data:") || e.Uri.StartsWith("about:")) return;
                e.Cancel = true;   // links open in the real browser
                if (e.Uri.StartsWith("https://") || e.Uri.StartsWith("http://")) try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
            };
            web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            web.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            web.CoreWebView2.WebMessageReceived += (_, e) => OnMessage(e.TryGetWebMessageAsString());
            web.NavigateToString(Page());
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(Paths.DataDir, "daemon.log"), DateTime.Now.ToString("s") + " answer: webview failed: " + ex.Message + Environment.NewLine); } catch { }
            Fallback();
        }
        if (autoClose)
        {
            var t = new System.Windows.Forms.Timer { Interval = 4000 };
            t.Tick += (_, _) => Close();
            t.Start();
        }
    }

    // No WebView2: plain formatted text in the same box.
    void Fallback()
    {
        Controls.Remove(web);
        var rtb = new RichTextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.S1, ForeColor = Theme.Text2, ReadOnly = true, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical };
        Theme.DarkScroll(rtb);
        rtb.Rtf = DocRenderer.Answer((request.Length > 0 ? "**" + request + "**\n\n" : "") + answer);
        Padding = new Padding(S(18), S(14), S(6), S(10));
        Controls.Add(rtb);
        Height = Math.Min((int)(work.Height * 0.6), S(360)); Place();
        rtb.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        rtb.DoubleClick += (_, _) => Close();
    }

    void OnMessage(string msg)
    {
        if (msg == null) return;
        if (msg == "close") Close();
        else if (msg == "copy") { try { Clipboard.SetText(answer); } catch { } }
        else if (msg == "drag") { ReleaseCapture(); SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero); }
        else if (msg == "expand") { expanded = !expanded; Fit(); }
        else if (msg == "focus") { try { SetWindowLong(Handle, -20, GetWindowLong(Handle, -20) & ~0x08000000); Activate(); web.Focus(); } catch { } }   // clicked into the reply row: only now may the box take keyboard focus
        else if (msg.StartsWith("reply:")) SendReply(msg.Substring(6));
        else if (msg.StartsWith("h:") && int.TryParse(msg.Substring(2), out int h)) { contentHeight = h; Fit(); }
    }

    // A reply goes the same way as the typing box: request file + event, with the thread id so the daemon continues this conversation.
    void SendReply(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0 || threadId == null) return;
        try
        {
            File.WriteAllText(AskBox.File, Threads.Compose(threadId, text));
            using var ev = EventWaitHandle.OpenExisting(AskBox.Event);
            ev.Set();
        }
        catch { }
        Close();
    }

    // Height follows the content: up to ~55% of the screen normally, ~88% (and wider) when expanded.
    void Fit()
    {
        if (contentHeight <= 0) return;
        double k = DeviceDpi / 96.0;
        int want = (int)Math.Ceiling(contentHeight * k) + 2;
        int max = (int)(work.Height * (expanded ? 0.88 : 0.55));
        bool atHome = Math.Abs(Bottom - (work.Bottom - S(10))) < S(4);   // not dragged somewhere else
        var size = new Size(S(expanded ? 680 : 480), Math.Clamp(want, S(autoClose ? 90 : 120), max));
        if (size == Size) return;
        int right = Right, bottom = Bottom;
        Size = size;
        if (atHome) Place(); else Location = new Point(Math.Max(work.Left, right - Width), Math.Max(work.Top, bottom - Height));
    }

    // Earlier turns of the conversation, dimmed above the newest answer (click one to expand it).
    string PriorHtml()
    {
        if (earlier.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var t in earlier)
        {
            string a = Md.Plain(t.A);
            sb.Append("<div class=\"prior\"><div class=\"pq\">").Append(System.Net.WebUtility.HtmlEncode(t.Q)).Append("</div><div class=\"pa\">")
              .Append(System.Net.WebUtility.HtmlEncode(a)).Append("</div></div>");
        }
        return sb.ToString();
    }

    string Page()
    {
        string name = (Settings.Load().AssistantNames ?? "Luna").Split(',')[0].Trim();
        if (name.Length == 0) name = "Luna";
        string e(string s) => System.Net.WebUtility.HtmlEncode(s);
        return "<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data:\">" +
            "<style>" + Md.Css + @"
html,body{height:100%;overflow:hidden}
#wrap{display:flex;flex-direction:column;height:100vh}
#head{display:flex;align-items:flex-start;gap:8px;padding:12px 10px 8px 18px;cursor:default;user-select:none;flex:none}
#who{font-size:10.5px;font-weight:600;letter-spacing:.14em;color:var(--ice);text-transform:uppercase}
#req{color:var(--ink);font-size:14px;line-height:1.35;margin-top:2px;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden}
#t{flex:1;min-width:0}
button{background:none;border:0;color:var(--soft);font:12px 'Segoe UI';padding:5px 9px;border-radius:999px;cursor:pointer}button:hover{background:var(--s3);color:var(--ink)}
#x{font-size:15px;line-height:1;padding:5px 9px}
#body{flex:1;overflow-y:auto;padding:2px 18px 16px 18px}
.prior{opacity:.5;margin:0 0 8px;padding:0 0 8px;border-bottom:1px solid var(--s3);cursor:pointer;font-size:12.5px;line-height:1.4}
.prior:hover,.prior.open{opacity:.85}
.pq{color:var(--ink);font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.pa{color:var(--soft);white-space:pre-wrap;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden}
.prior.open .pa{-webkit-line-clamp:unset;display:block}
#reply{flex:none;padding:8px 14px 12px;border-top:1px solid var(--s3)}
#ri{width:100%;box-sizing:border-box;background:var(--s2);color:var(--ink);border:1px solid var(--s3);border-radius:999px;padding:7px 14px;font:13px 'Segoe UI';outline:none}
#ri:focus{border-color:var(--ice)}#ri::placeholder{color:var(--soft)}
#body.fade{opacity:0;transform:translateY(4px)}#body{transition:opacity .18s ease,transform .18s ease}
</style></head><body><div id=""wrap""><div id=""head""><div id=""t""><div id=""who"">" + e(name) + @"</div>" +
            (request.Length > 0 ? "<div id=\"req\">" + e(request) + "</div>" : "") +
            @"</div>" + (autoClose ? "" : @"<button id=""c"" title=""Copy the answer"">Copy</button><button id=""e"" title=""Bigger / smaller"">Expand</button>") + @"<button id=""x"" title=""Close (Esc)"">✕</button></div>
<div id=""body"" class=""fade"">" + PriorHtml() + @"<div id=""cur"">" + Md.ToHtml(answer) + @"</div></div>" +
            (threadId != null ? @"<div id=""reply""><input id=""ri"" type=""text"" placeholder=""Reply…"" autocomplete=""off"" spellcheck=""false""></div>" : "") + @"</div>
<script>
const post = m => window.chrome.webview.postMessage(m);
const head = document.getElementById('head'), body = document.getElementById('body');
const reply = document.getElementById('reply'), ri = document.getElementById('ri'), cur = document.getElementById('cur');
function size(){ post('h:' + Math.ceil(head.getBoundingClientRect().height + body.scrollHeight + (reply ? reply.getBoundingClientRect().height : 0) + 2)); }
function home(){ if (cur && body.scrollHeight - body.clientHeight > 12) body.scrollTop = cur.offsetTop - 8; }
window.addEventListener('load', () => { size(); body.classList.remove('fade'); setTimeout(size, 120); setTimeout(home, 450); });
document.querySelectorAll('.prior').forEach(p => p.onclick = () => { p.classList.toggle('open'); size(); });
if (ri) {
  ri.addEventListener('focus', () => post('focus'));
  ri.addEventListener('keydown', ev => { if (ev.key === 'Enter' && ri.value.trim()) { ev.preventDefault(); post('reply:' + ri.value); } else if (ev.key === 'Escape') { ev.stopPropagation(); post('close'); } });
}
new ResizeObserver(size).observe(document.querySelector('.md'));
document.getElementById('x').onclick = () => post('close');
const c = document.getElementById('c'); if (c) c.onclick = () => { post('copy'); c.textContent = 'Copied'; setTimeout(() => c.textContent = 'Copy', 1400); };
const ex = document.getElementById('e'); if (ex) ex.onclick = () => { post('expand'); ex.textContent = ex.textContent === 'Expand' ? 'Shrink' : 'Expand'; };
head.addEventListener('mousedown', ev => { if (ev.target.tagName !== 'BUTTON' && ev.button === 0) post('drag'); });
document.addEventListener('keydown', ev => { if (ev.key === 'Escape') post('close'); });
</script></body></html>";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Close();
        base.OnKeyDown(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try { closeOthers?.Dispose(); } catch { }
        base.OnFormClosed(e);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
}
