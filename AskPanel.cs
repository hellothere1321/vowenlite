using System.Drawing.Drawing2D;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace VowenLite;

// "Ask AI" under a meeting: a small chat about the meeting (or about all recent meetings).
//   - one-click starter questions, so it is useful before you think of what to type
//   - answers are rendered properly (headings, bullets, tables, [mm:ss] timestamps, diagrams) and kept per meeting in ask.json
//   - the conversation area grows with the content (up to half the pane), collapses, clears; Ask becomes Stop while it thinks
//   - "Add to planner" turns an answer's bullet points (action items) into planner tasks
// The conversation is a WebView2 that is only created when there is something to show.
sealed class AskPanel : Control
{
    sealed class QA { public string Q { get; set; } = ""; public string A { get; set; } = ""; public DateTime At { get; set; } = DateTime.Now; public bool All { get; set; } public bool Failed { get; set; } }

    public Func<string, string, string, bool, CancellationToken, Task<string>> Asker;   // (folder, question, history, allMeetings, cancel) -> answer
    public Action<string, bool> Status = (_, _) => { };

    static readonly (string Label, string Ask)[] Starters =
    {
        ("Key points", "Summarise this meeting in 5 bullets"), ("Action items", "List the action items"), ("Decisions", "What decisions were made?"),
        ("My commitments", "What did I (You) commit to doing?"), ("Open questions", "What questions were left open?"), ("Follow-up", "Draft a short follow-up message to the others"),
    };

    readonly FlowLayoutPanel chips = new() { WrapContents = false, AutoScroll = false, BackColor = Theme.Bg };
    readonly TextBox box = new();
    readonly Pill btnAsk = new("Ask", PillKind.Primary) { Margin = Padding.Empty };
    readonly Pill btnScope = new("This meeting", PillKind.Ghost) { Margin = Padding.Empty, Chevron = true };
    WebView2 web;
    bool webReady, webFailed;
    RichTextBox plain;                       // fallback when WebView2 is missing

    string folder;
    bool canAsk, asking, all, collapsed, showChips;
    List<QA> log = new();
    string pendingQ;
    int contentPx;
    CancellationTokenSource cts;
    readonly List<string> recall = new();
    int recallAt = -1;

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    int ChipsH => S(40);
    int InputH => S(56);

    public AskPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        Dock = DockStyle.Bottom;

        box.BorderStyle = BorderStyle.None; box.BackColor = Theme.S2; box.ForeColor = Theme.Ink;
        box.Font = Theme.Font(Theme.Body, 10.5f);
        box.PlaceholderText = "Ask anything about this meeting…";
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = Ask(box.Text); }
            else if (e.KeyCode == Keys.Up && recall.Count > 0) { recallAt = recallAt < 0 ? recall.Count - 1 : Math.Max(0, recallAt - 1); box.Text = recall[recallAt]; box.SelectionStart = box.TextLength; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Down && recallAt >= 0) { recallAt++; box.Text = recallAt < recall.Count ? recall[recallAt] : ""; if (recallAt >= recall.Count) recallAt = -1; box.SelectionStart = box.TextLength; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape && asking) { e.SuppressKeyPress = true; cts?.Cancel(); }
        };
        box.TextChanged += (_, _) => Sync();
        box.GotFocus += (_, _) => Invalidate(); box.LostFocus += (_, _) => Invalidate();
        btnAsk.Click += (_, _) => { if (asking) cts?.Cancel(); else _ = Ask(box.Text); };
        btnScope.Click += (_, _) => { all = !all; Sync(); box.Focus(); };

        foreach (var s in Starters)
        {
            var p = new Pill(s.Label, PillKind.Quiet) { Margin = new Padding(0, 0, S(6), 0) };
            p.Font = Theme.Font(Theme.Body, 9f);
            p.Click += (_, _) => _ = Ask(s.Ask);
            chips.Controls.Add(p);
        }
        Controls.Add(chips); Controls.Add(box); Controls.Add(btnAsk); Controls.Add(btnScope);
        Height = ChipsH + InputH;
        Sync();
    }

    // ---------------- state

    public void SetMeeting(string meetingFolder, bool hasTranscript)
    {
        bool changed = meetingFolder != folder;
        canAsk = meetingFolder != null && hasTranscript;
        if (changed)
        {
            if (asking) cts?.Cancel();
            folder = meetingFolder;
            log = Load(folder);
            pendingQ = null; contentPx = 0; collapsed = false;
            Render();
        }
        Sync();
    }

    static string FileFor(string f) => Path.Combine(f, "ask.json");
    static List<QA> Load(string f)
    {
        try { if (f != null && File.Exists(FileFor(f))) return JsonSerializer.Deserialize<List<QA>>(File.ReadAllText(FileFor(f))) ?? new(); } catch { }
        return new();
    }
    void Save()
    {
        try
        {
            if (folder == null || !Directory.Exists(folder)) return;
            if (log.Count == 0) { if (File.Exists(FileFor(folder))) File.Delete(FileFor(folder)); return; }
            File.WriteAllText(FileFor(folder), JsonSerializer.Serialize(log.TakeLast(40).ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    void Sync()
    {
        btnAsk.Text = asking ? "Stop" : "Ask";
        btnAsk.Kind = asking ? PillKind.Quiet : PillKind.Primary;
        btnAsk.Enabled = asking || (canAsk && box.Text.Trim().Length > 0);
        btnScope.Text = all ? "All meetings" : "This meeting";
        btnScope.Enabled = canAsk && !asking;
        box.Enabled = canAsk;
        box.PlaceholderText = !canAsk ? "Transcribe this meeting to ask about it" : all ? "Ask across your recent meetings…" : "Ask anything about this meeting…";
        foreach (Control c in chips.Controls) c.Enabled = canAsk && !asking;
        showChips = canAsk && !asking && log.Count == 0;   // (Control.Visible reads false until the window is shown, so the layout keeps its own flag)
        chips.Visible = showChips;
        btnAsk.Redraw(); btnScope.Redraw();
        Relayout();
    }

    // ---------------- layout: [conversation] [starter chips] [input row]

    int ConvoH()
    {
        if ((log.Count == 0 && pendingQ == null) || contentPx <= 0) return 0;
        if (collapsed) return S(34);
        int max = Math.Max(S(120), (int)((Parent?.Height ?? S(600)) * 0.52));
        return Math.Clamp((int)Math.Ceiling(contentPx * DeviceDpi / 96.0) + 2, S(60), max);
    }

    void Relayout()
    {
        int convo = ConvoH(), chipsH = showChips ? ChipsH : 0;
        int want = convo + chipsH + InputH;
        if (Height != want) Height = want;
        int left = S(32), right = Width - S(24);
        if (web != null && !webFailed) { web.Visible = convo > 0; web.SetBounds(left - S(4), 0, Math.Max(10, right - left + S(4)), convo); }
        if (plain != null) { plain.Visible = convo > 0; plain.SetBounds(left, 0, Math.Max(10, right - left), convo); }
        chips.SetBounds(left, convo + S(2), Math.Max(10, right - left), S(36));
        int y = convo + chipsH + S(10);
        btnAsk.PerformLayout();
        int bw = Math.Max(S(72), btnAsk.GetPreferredSize(Size.Empty).Width), sw = btnScope.GetPreferredSize(Size.Empty).Width;
        btnAsk.SetBounds(right - bw, y, bw, S(36));
        btnScope.SetBounds(btnAsk.Left - S(14) - sw, y, sw, S(36));
        box.SetBounds(left + S(18), y + (S(36) - box.Height) / 2, Math.Max(10, btnScope.Left - S(8) - left - S(18)), box.Height);
        Invalidate();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }
    protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); if (Parent != null) Parent.Resize += (_, _) => Relayout(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // the input field: one rounded surface holding the text box and the scope switch
        var r = new RectangleF(S(32), btnAsk.Top, btnScope.Left - S(4) - S(32), S(36));
        using var path = Theme.Round(r, S(18));
        using var br = new SolidBrush(Theme.S2);
        g.FillPath(br, path);
        if (box.Focused) { using var pen = new Pen(Theme.Mix(Theme.S3, Theme.Ice, .45f), 1.2f); g.DrawPath(pen, path); }
    }

    // ---------------- asking

    string History()
    {
        var sb = new StringBuilder();
        foreach (var x in log.Where(x => !x.Failed).TakeLast(4)) sb.Append("Q: ").Append(x.Q).Append("\nA: ").Append(x.A.Length > 1200 ? x.A.Substring(0, 1200) + "…" : x.A).Append("\n\n");
        return sb.ToString();
    }

    async Task Ask(string question)
    {
        string q = (question ?? "").Trim();
        if (asking || !canAsk || q.Length == 0 || Asker == null) return;
        string f = folder; bool wasAll = all;
        if (recall.Count == 0 || recall[^1] != q) recall.Add(q);
        recallAt = -1;
        box.Text = "";
        asking = true; pendingQ = q; collapsed = false;
        cts = new CancellationTokenSource();
        Sync(); Render();
        Status(wasAll ? "Asking across your recent meetings…" : "Asking about this meeting…", false);
        var qa = new QA { Q = q, All = wasAll };
        try { qa.A = (await Asker(f, q, History(), wasAll, cts.Token)).Trim(); Status("Answer ready", false); }
        catch (OperationCanceledException) { qa = null; Status("Stopped.", false); }
        catch (Exception ex) { qa.A = "Couldn't answer: " + ex.Message.Split('\n')[0]; qa.Failed = true; Status(qa.A, true); }
        asking = false; pendingQ = null;
        if (f == folder)
        {
            if (qa != null) { log.Add(qa); Save(); }
            Render(); Sync();
            if (box.CanFocus) box.Focus();
        }
        else if (qa != null && !qa.Failed)
        {
            // the user moved to another meeting while it was thinking: keep the answer with the meeting it belongs to
            var other = Load(f); other.Add(qa);
            try { File.WriteAllText(FileFor(f), JsonSerializer.Serialize(other.TakeLast(40).ToList(), new JsonSerializerOptions { WriteIndented = true })); } catch { }
            Sync();
        }
    }

    // Bullet lines of an answer become planner tasks ("Send the draft to Maya by Friday" keeps its due date).
    void AddToPlanner(int index)
    {
        if (index < 0 || index >= log.Count) return;
        var items = Regex.Matches(log[index].A, @"(?m)^\s*(?:[-*•]|\d+[.)])\s+(.+)$").Select(m => Md.Plain(m.Groups[1].Value)).Where(t => t.Length is > 3 and < 200).Take(12).ToList();
        if (items.Count == 0) { Status("No bullet points in that answer to add.", true); return; }
        int n = 0;
        foreach (var t in items)
        {
            try
            {
                string text = Regex.Replace(t, @"^\s*(?:You|I|Me|Others?)\s*[:\-–]\s*", "", RegexOptions.IgnoreCase);
                text = Regex.Replace(text, @"\[\d{1,2}:\d{2}(?::\d{2})?\]", "").Trim();
                var p = Planner.Parse(text, DateTime.Now, "task");
                if (p == null || p.Title.Length == 0) continue;
                p.Kind = "task";
                if (p.Cat.Length == 0) p.Notes = "From meeting " + Path.GetFileName(folder ?? "");
                Planner.Add(p); n++;
            }
            catch { }
        }
        Status(n == 0 ? "Couldn't turn those into tasks." : $"Added {n} task{(n == 1 ? "" : "s")} to the planner.", n == 0);
    }

    // ---------------- conversation view

    async void EnsureWeb()
    {
        if (web != null || webFailed) return;
        web = new WebView2 { DefaultBackgroundColor = Theme.Bg, Visible = false };
        Controls.Add(web);
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.DataDir, "webview"), WebOpts.Tiny());
            await web.EnsureCoreWebView2Async(env);
            var st = web.CoreWebView2.Settings;
            st.AreDevToolsEnabled = false; st.IsStatusBarEnabled = false; st.AreDefaultContextMenusEnabled = false; st.IsZoomControlEnabled = false;
            web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith("data:") || e.Uri.StartsWith("about:")) return;
                e.Cancel = true;
                if (e.Uri.StartsWith("https://") || e.Uri.StartsWith("http://")) try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
            };
            web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            web.CoreWebView2.WebMessageReceived += (_, e) => OnMessage(e.TryGetWebMessageAsString());
            webReady = true;
            Render();
        }
        catch
        {
            webFailed = true;
            Controls.Remove(web); web = null;
            plain = new RichTextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Bg, ForeColor = Theme.Text2, ReadOnly = true, DetectUrls = false, TabStop = false, ScrollBars = RichTextBoxScrollBars.Vertical, Font = Theme.Font(Theme.Body, 10.5f) };
            Theme.DarkScroll(plain);
            Controls.Add(plain);
            Render();
        }
    }

    void OnMessage(string msg)
    {
        if (msg == null) return;
        if (msg.StartsWith("h:") && int.TryParse(msg.Substring(2), out int h)) { if (h != contentPx) { contentPx = h; Relayout(); } }
        else if (msg == "clear") { log.Clear(); Save(); contentPx = 0; Render(); Sync(); }
        else if (msg == "fold") { collapsed = !collapsed; Render(); Relayout(); }
        else if (msg.StartsWith("copy:") && int.TryParse(msg.Substring(5), out int ci) && ci >= 0 && ci < log.Count) { try { Clipboard.SetText(log[ci].A.Replace("\r\n", "\n").Replace("\n", "\r\n")); Status("Copied that answer.", false); } catch { } }
        else if (msg.StartsWith("plan:") && int.TryParse(msg.Substring(5), out int pi)) AddToPlanner(pi);
        else if (msg.StartsWith("again:") && int.TryParse(msg.Substring(6), out int ai) && ai >= 0 && ai < log.Count) { string q = log[ai].Q; log.RemoveAt(ai); Save(); _ = Ask(q); }
    }

    void Render()
    {
        if (log.Count == 0 && pendingQ == null) { contentPx = 0; Relayout(); return; }
        EnsureWeb();
        if (plain != null)
        {
            var sb = new StringBuilder();
            foreach (var x in log) sb.Append("**").Append(x.Q).Append("**\n\n").Append(x.A).Append("\n\n");
            if (pendingQ != null) sb.Append("**").Append(pendingQ).Append("**\n\nThinking…");
            plain.Rtf = DocRenderer.Answer(sb.ToString());
            plain.SelectionStart = plain.TextLength; plain.ScrollToCaret();
            contentPx = Math.Min(360, 60 + 22 * sb.ToString().Split('\n').Length);
            Relayout();
            return;
        }
        if (!webReady) return;
        web.NavigateToString(Page());
    }

    string Page()
    {
        static string E(string s) => System.Net.WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data:\"><style>")
          .Append(Md.Css).Append(@"
:root{--bg:#000}html,body{background:#000;overflow:hidden;height:100%}
#bar{display:flex;align-items:center;gap:4px;height:32px;padding:0 4px;border-top:1px solid var(--s2);user-select:none}
#bar .t{font-size:11px;font-weight:600;letter-spacing:.14em;color:var(--faint);flex:1}
button{background:none;border:0;color:var(--soft);font:12px 'Segoe UI';padding:4px 9px;border-radius:999px;cursor:pointer}button:hover{background:var(--s3);color:var(--ink)}
#list{overflow-y:auto;padding:4px 10px 10px 4px}
.q{display:flex;gap:10px;align-items:baseline;margin:14px 0 6px}.q:first-child{margin-top:4px}
.q .who{font-size:10.5px;font-weight:600;letter-spacing:.12em;color:var(--ice);flex:none;width:28px}
.q .txt{color:var(--ink);font-weight:600;font-size:14.5px;flex:1}.q .tag{font-size:10.5px;color:var(--faint);border:1px solid var(--s3);border-radius:999px;padding:0 7px;margin-left:8px;font-weight:400}
.a{margin-left:38px;padding:10px 14px;background:#0c0d10;border:1px solid var(--s2);border-radius:14px}.a.err{color:#ffb4a8}
.tools{margin:4px 0 0 34px;opacity:0;transition:opacity .12s}.turn:hover .tools{opacity:1}
.dots span{display:inline-block;width:6px;height:6px;margin-right:5px;border-radius:50%;background:var(--ice);animation:b 1.1s infinite ease-in-out}.dots span:nth-child(2){animation-delay:.15s}.dots span:nth-child(3){animation-delay:.3s}
@keyframes b{0%,80%,100%{opacity:.25;transform:scale(.8)}40%{opacity:1;transform:scale(1)}}
</style></head><body><div id=""bar""><span class=""t"">ASK AI</span>")
          .Append(log.Count > 0 ? @"<button id=""clr"" title=""Forget this conversation"">Clear</button>" : "")
          .Append(@"<button id=""fold"">").Append(collapsed ? "Show" : "Hide").Append(@"</button></div><div id=""list"">");
        for (int i = 0; i < log.Count; i++)
        {
            var x = log[i];
            bool bullets = !x.Failed && Regex.IsMatch(x.A, @"(?m)^\s*(?:[-*•]|\d+[.)])\s+\S");
            sb.Append(@"<div class=""turn""><div class=""q""><span class=""who"">YOU</span><span class=""txt"">").Append(E(x.Q)).Append(x.All ? @"<span class=""tag"">all meetings</span>" : "").Append("</span></div>")
              .Append(@"<div class=""a").Append(x.Failed ? " err" : "").Append(@""">").Append(Md.ToHtml(x.A)).Append("</div>")
              .Append(@"<div class=""tools""><button data-m=""copy:").Append(i).Append(@""">Copy</button>")
              .Append(bullets ? @"<button data-m=""plan:" + i + @""" title=""Add each bullet as a planner task"">Add to planner</button>" : "")
              .Append(@"<button data-m=""again:").Append(i).Append(@""">Ask again</button></div></div>");
        }
        if (pendingQ != null)
            sb.Append(@"<div class=""turn""><div class=""q""><span class=""who"">YOU</span><span class=""txt"">").Append(E(pendingQ)).Append(@"</span></div><div class=""a""><span class=""dots""><span></span><span></span><span></span></span></div></div>");
        sb.Append(@"</div><script>
const post = m => window.chrome.webview.postMessage(m);
const list = document.getElementById('list'), bar = document.getElementById('bar');
function size(){ post('h:' + Math.ceil(bar.getBoundingClientRect().height + list.scrollHeight + 2)); }
function fit(){ list.style.height = Math.max(0, window.innerHeight - bar.getBoundingClientRect().height) + 'px'; }
window.addEventListener('load', () => { size(); fit(); setTimeout(() => { fit(); const t = list.querySelectorAll('.turn'); if (t.length) t[t.length - 1].scrollIntoView({ block: 'start' }); }, 60); });
window.addEventListener('resize', fit);
document.addEventListener('click', e => { const m = e.target.getAttribute && e.target.getAttribute('data-m'); if (m) { post(m); if (m.startsWith('copy')) { e.target.textContent = 'Copied'; } } });
const c = document.getElementById('clr'); if (c) c.onclick = () => post('clear');
document.getElementById('fold').onclick = () => post('fold');
</script></body></html>");
        return sb.ToString();
    }
}
