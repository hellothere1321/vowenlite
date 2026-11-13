using System.Diagnostics;
using System.Drawing.Text;

namespace VowenLite;

// Panel that double-buffers and repaints on resize (plain Panel flickers when custom-painted).
sealed class Surface : Panel
{
    public Surface() { DoubleBuffered = true; ResizeRedraw = true; }
}

sealed class MainForm : Form
{
    readonly Settings settings = Settings.Load();
    readonly Pill btnRecord = new("Record", PillKind.Primary) { Dot = Theme.Rec };
    readonly Pill btnMic = new("Default mic", PillKind.Quiet) { Chevron = true };
    readonly Pill btnPlan = new("Plan", PillKind.Ghost);
    readonly Pill btnTranscribe = new("Transcribe");
    readonly Pill btnSummarise = new("Summarise");
    readonly Pill btnCopy = new("Copy");
    readonly Pill btnOpen = new("Open folder");
    readonly Pill btnDelete = new("Delete", PillKind.Danger);
    readonly Toggle chkKeep = new("Keep audio after transcribing");
    readonly Label lblStatus = new();
    readonly Surface docHead = new();
    string titleText = "VowenLite", metaText = "";
    readonly RowList lstMeetings = new();
    readonly RichTextBox rtb = new();
    readonly AskPanel ask = new();
    bool snapping;
    Task snapTask = Task.CompletedTask;
    readonly System.Windows.Forms.Timer tick = new() { Interval = 500 };
    readonly System.Windows.Forms.Timer disarm = new() { Interval = 3000 };
    readonly TextBox txtSearch = new();
    readonly System.Windows.Forms.Timer searchDelay = new() { Interval = 250 };
    string query = "";

    Recorder recorder;
    string recordingFolder, renderedKey;
    bool transcribing, summarising;
    EventWaitHandle quit;

    sealed class Meeting
    {
        public string Folder;
        public DateTime? When;
        public int Seconds = -1;
        public bool HasTranscript, HasSummary, HasAudio, Live;
    }

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public MainForm()
    {
        Text = "VowenLite";
        AutoScaleMode = AutoScaleMode.None;
        Font = Theme.Font(Theme.Body, 9.5f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Ink;
        ClientSize = new Size(S(1040), S(680));
        MinimumSize = new Size(S(800), S(480));
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        try { Icon = Theme.AppIcon(); } catch { }

        // ----- header: wordmark left, mic + record right
        var header = new Surface { Dock = DockStyle.Top, Height = S(64), BackColor = Theme.Bg };
        var fWord = Theme.Font(Theme.BodyBold, 12.5f);
        header.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Theme.DrawMark(e.Graphics, new RectangleF(S(28), S(20), S(15), S(24)), Theme.Ice);
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            TextRenderer.DrawText(e.Graphics, "VowenLite", fWord, new Rectangle(S(54), 0, S(200), header.Height), Theme.Ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.Left);
        };
        var headRight = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Theme.Bg, Padding = new Padding(0, S(14), S(24), 0) };
        btnMic.MaxWidth = S(260);
        btnRecord.MinWidth = S(150);
        btnRecord.Margin = Padding.Empty;
        btnPlan.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(Environment.ProcessPath, "--plan") { UseShellExecute = false }); } catch { } };
        headRight.Controls.AddRange(new Control[] { btnPlan, btnMic, btnRecord });
        header.Controls.Add(headRight);
        var rule = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.S2 };

        // ----- footer: status + keep-audio switch
        var footer = new Surface { Dock = DockStyle.Bottom, Height = S(40), BackColor = Theme.Bg, Padding = new Padding(S(28), 0, S(24), 0) };
        footer.Paint += (_, e) => { using var p = new Pen(Theme.S2); e.Graphics.DrawLine(p, 0, 0, footer.Width, 0); };
        lblStatus.Dock = DockStyle.Fill;
        lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        lblStatus.AutoEllipsis = true;
        lblStatus.Font = Theme.Font(Theme.Body, 9f);
        lblStatus.BackColor = Theme.Bg;
        SetStatus("Ready. Hold Right Shift anywhere to dictate.");
        chkKeep.Dock = DockStyle.Right;
        chkKeep.AutoSize = false;
        chkKeep.Width = chkKeep.GetPreferredSize(Size.Empty).Width + S(4);
        footer.Controls.Add(lblStatus);
        footer.Controls.Add(chkKeep);

        // ----- sidebar: meeting list
        var side = new Surface { Dock = DockStyle.Left, Width = S(284), BackColor = Theme.S1 };
        var fCap = Theme.Font(Theme.BodyBold, 8.5f);
        var sideHead = new Surface { Dock = DockStyle.Top, Height = S(46), BackColor = Theme.S1 };
        sideHead.Paint += (_, e) =>
        {
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            TextRenderer.DrawText(e.Graphics, "MEETINGS", fCap, new Rectangle(S(22), S(6), S(200), sideHead.Height - S(6)), Theme.Faint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.Left);
        };
        lstMeetings.Dock = DockStyle.Fill;
        var searchHost = new Surface { Dock = DockStyle.Top, Height = S(52), BackColor = Theme.S1 };
        txtSearch.BorderStyle = BorderStyle.None;
        txtSearch.BackColor = Theme.S2;
        txtSearch.ForeColor = Theme.Ink;
        txtSearch.Font = Theme.Font(Theme.Body, 9.5f);
        txtSearch.PlaceholderText = "Search meetings";
        searchHost.Controls.Add(txtSearch);
        searchHost.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = Theme.Round(new RectangleF(S(14), S(6), searchHost.Width - S(28), S(34)), S(17));
            using var br = new SolidBrush(Theme.S2);
            e.Graphics.FillPath(br, path);
        };
        void LayoutSearch() => txtSearch.SetBounds(S(32), S(6) + (S(34) - txtSearch.Height) / 2, Math.Max(10, searchHost.Width - S(64)), txtSearch.Height);
        searchHost.Resize += (_, _) => LayoutSearch();
        LayoutSearch();
        txtSearch.TextChanged += (_, _) => { searchDelay.Stop(); searchDelay.Start(); };
        searchDelay.Tick += (_, _) => { searchDelay.Stop(); query = txtSearch.Text.Trim(); RefreshList(null); };
        side.Controls.Add(lstMeetings);
        side.Controls.Add(searchHost);
        side.Controls.Add(sideHead);

        // ----- right pane: title, actions, document
        var pane = new Surface { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        docHead.Dock = DockStyle.Top; docHead.Height = S(156); docHead.BackColor = Theme.Bg; docHead.Padding = new Padding(S(32), 0, S(24), 0);
        var fTitle = Theme.Font(Theme.Display, 22f);
        var fMeta = Theme.Font(Theme.Body, 9.5f);
        docHead.Paint += (_, e) =>
        {
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            const TextFormatFlags f = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
            int w = docHead.Width - S(32) - S(24);
            TextRenderer.DrawText(e.Graphics, titleText, fTitle, new Rectangle(S(32), S(24), w, S(42)), Theme.Ink, f);
            TextRenderer.DrawText(e.Graphics, metaText, fMeta, new Rectangle(S(32), S(68), w, S(24)), Theme.Soft, f);
        };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = S(62), WrapContents = false, BackColor = Theme.Bg, Padding = new Padding(0, S(8), 0, S(10)) };
        actions.Controls.AddRange(new Control[] { btnTranscribe, btnSummarise, btnCopy, btnOpen, btnDelete });
        docHead.Controls.Add(actions);

        var docHost = new Surface { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(S(32), 0, 0, S(8)) };
        rtb.Dock = DockStyle.Fill;
        rtb.BorderStyle = BorderStyle.None;
        rtb.BackColor = Theme.Bg;
        rtb.ForeColor = Theme.Text2;
        rtb.Font = Theme.Font(Theme.Body, 11f);
        rtb.ReadOnly = true;
        rtb.DetectUrls = false;
        rtb.HideSelection = false;
        rtb.TabStop = false;
        rtb.ScrollBars = RichTextBoxScrollBars.Vertical;
        Theme.DarkScroll(rtb);
        docHost.Controls.Add(rtb);

        // Ask AI: a chat about the selected meeting, under the document (AskPanel.cs).
        ask.Asker = (folder, q, history, all, ct) => Summariser.AskAsync(folder, settings, q, history, all, ct);
        ask.Status = (text, error) => SetStatus(text, error);
        pane.Controls.Add(docHost);
        pane.Controls.Add(ask);
        pane.Controls.Add(docHead);

        var body = new Surface { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        body.Controls.Add(pane);
        body.Controls.Add(side);

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(rule);
        Controls.Add(header);

        chkKeep.Checked = settings.KeepWavs;
        chkKeep.CheckedChanged += (_, _) => { settings.KeepWavs = chkKeep.Checked; settings.Save(); };
        btnRecord.Click += (_, _) => { if (recorder == null) StartRecording(); else StopRecording(true); };
        btnMic.Click += (_, _) => ShowMicMenu();
        lstMeetings.SelectedIndexChanged += (_, _) => { Disarm(); ShowSelected(); };
        btnTranscribe.Click += async (_, _) =>
        {
            if (Selected() is not { } m) return;
            if (m.Live) await SnapshotTranscribe(m.Folder); else await Transcribe(m.Folder);
        };
        btnSummarise.Click += (_, _) => SendToCodex();
        btnCopy.Click += (_, _) => CopyText();
        btnOpen.Click += (_, _) => { if (Selected() is { } m) Process.Start("explorer.exe", m.Folder); };
        btnDelete.Click += (_, _) => DeleteSelected();
        disarm.Tick += (_, _) => Disarm();
        tick.Tick += (_, _) =>
        {
            if (recorder == null) return;
            btnRecord.Text = "Stop · " + Transcriber.Stamp((long)recorder.Elapsed.TotalMilliseconds);
            btnRecord.DotLit = !btnRecord.DotLit;
            btnRecord.Invalidate();
        };

        UpdateMicLabel();
        CleanOldAudio();
        RefreshList(null);
        UpdateButtons();

        // The daemon's second Ctrl+` sets this event; close cleanly so the WAV headers get written.
        quit = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEvent);
        var t = new Thread(() => { if (quit.WaitOne()) try { BeginInvoke(Close); } catch { } }) { IsBackground = true };
        t.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (recorder != null) StopRecording(false); // audio kept; "Transcribe" picks it up next time
        base.OnFormClosing(e);
    }

    void SetStatus(string text, bool error = false)
    {
        lblStatus.Text = text;
        lblStatus.ForeColor = error ? Theme.Err : Theme.Soft;
    }

    // ---------- microphone

    void UpdateMicLabel()
    {
        string name = "Default mic";
        try { name = Recorder.Mics().FirstOrDefault(m => m.Id == settings.MicDeviceId && m.Id != "").Name ?? "Default mic"; } catch { }
        btnMic.Text = name;
        btnMic.Redraw();
    }

    void ShowMicMenu()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(), BackColor = Theme.S2, ForeColor = Theme.Ink, Font = Theme.Font(Theme.Body, 9.5f),
            ShowImageMargin = false, ShowCheckMargin = true, MinimumSize = new Size(btnMic.Width, 0),
        };
        var list = new List<(string Id, string Name)> { ("", "Default mic") };
        try { list.AddRange(Recorder.Mics()); }
        catch (Exception ex) { SetStatus("No audio devices: " + ex.Message, true); }
        foreach (var (id, name) in list)
        {
            var item = new ToolStripMenuItem(name) { Checked = id == settings.MicDeviceId, Padding = new Padding(0, S(5), 0, S(5)) };
            string captured = id;
            item.Click += (_, _) => { settings.MicDeviceId = captured; settings.Save(); UpdateMicLabel(); };
            menu.Items.Add(item);
        }
        menu.Show(btnMic, new Point(0, btnMic.Height + S(6)));
    }

    // ---------- recording / transcription

    void StartRecording()
    {
        string name = DateTime.Now.ToString("yyyy-MM-dd_HH-mm"), folder = Path.Combine(Paths.MeetingsDir, name);
        for (int n = 2; Directory.Exists(folder); n++) folder = Path.Combine(Paths.MeetingsDir, name + "-" + n);
        Directory.CreateDirectory(folder);
        try
        {
            recorder = new Recorder(folder, settings.MicDeviceId);
            recorder.Problem += msg => BeginInvoke(() => SetStatus(msg, true));
            recorder.Start();
        }
        catch (Exception ex)
        {
            recorder?.Dispose();
            recorder = null;
            MessageBox.Show(this, "Couldn't start recording:\n" + ex.Message, "VowenLite", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        recordingFolder = folder;
        btnRecord.Text = "Stop · 00:00";
        SetStatus("Recording your mic and the PC's sound.");
        tick.Start();
        RefreshList(folder);
        UpdateButtons();
    }

    async void StopRecording(bool transcribe)
    {
        tick.Stop();
        var r = recorder;
        recorder = null;
        r.Dispose();
        string folder = recordingFolder;
        btnRecord.DotLit = true;
        SetStatus("Saved " + Path.GetFileName(folder));
        UpdateButtons();
        try { await snapTask; } catch { }   // a mid-meeting snapshot must finish (it won't write once recorder is null)
        if (transcribe) await Transcribe(folder);   // full transcript, then the auto summary
    }

    // Mid-meeting "Transcribe so far": the recording keeps going; Ask AI then works on what is transcribed.
    async Task SnapshotTranscribe(string folder)
    {
        if (snapping || recorder == null || folder != recordingFolder) return;
        snapping = true;
        UpdateButtons();
        snapTask = RunSnapshot(folder);
        await snapTask;
    }

    async Task RunSnapshot(string folder)
    {
        try
        {
            var progress = new Progress<string>(s => SetStatus(s));
            await Transcriber.RunAsync(folder, settings, progress, () => recorder != null && folder == recordingFolder);
        }
        catch (Exception ex) { SetStatus("Transcribe so far failed: " + ex.Message.Split('\n')[0], true); }
        snapping = false;
        RefreshList(folder);
        UpdateButtons();
    }

    async Task Transcribe(string folder)
    {
        if (transcribing) return;
        transcribing = true;
        UpdateButtons();
        bool ok = false;
        try
        {
            var progress = new Progress<string>(s => SetStatus(s));
            string md = await Transcriber.RunAsync(folder, settings, progress);
            ok = !File.ReadAllText(md).Contains("_No speech found._");
        }
        catch (Exception ex)
        {
            SetStatus("Transcription failed: " + ex.Message.Split('\n')[0], true);
            MessageBox.Show(this, ex.Message, "VowenLite", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        transcribing = false;
        RefreshList(folder);
        UpdateButtons();
        if (ok && settings.AutoSummarise && Summariser.FindCodex(settings) != null) await Summarise(folder);
    }

    async Task Summarise(string folder)
    {
        if (summarising) return;
        summarising = true;
        UpdateButtons();
        SetStatus("Summarising with Codex (" + settings.CodexModel + "). This takes a few minutes.");
        try
        {
            await Summariser.RunAsync(folder, settings);
            SetStatus("Summary saved");
        }
        catch (Exception ex)
        {
            SetStatus("Summary failed: " + ex.Message.Split('\n')[0], true);
            MessageBox.Show(this, ex.Message, "VowenLite", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        summarising = false;
        RefreshList(folder);
        UpdateButtons();
    }

    // ---------- meeting list + viewer

    static string Friendly(DateTime d)
    {
        var today = DateTime.Today;
        if (d.Date == today) return "Today";
        if (d.Date == today.AddDays(-1)) return "Yesterday";
        if ((today - d.Date).TotalDays < 7) return d.ToString("dddd");
        return d.ToString(d.Year == today.Year ? "ddd d MMM" : "ddd d MMM yyyy");
    }

    static string Duration(int s) => s < 60 ? Math.Max(1, s) + " sec" : (s + 30) / 60 + " min";

    Meeting Describe(string dir)
    {
        var m = new Meeting { Folder = dir };
        string name = Path.GetFileName(dir);
        if (name.Length >= 16 && DateTime.TryParseExact(name.Substring(0, 16), "yyyy-MM-dd_HH-mm", null, System.Globalization.DateTimeStyles.None, out var when)) m.When = when;
        string mic = Path.Combine(dir, "mic.wav");
        m.HasAudio = File.Exists(mic);
        if (m.HasAudio) m.Seconds = (int)Math.Max(0, (new FileInfo(mic).Length - 44) / (Recorder.Rate * 2));
        m.HasTranscript = File.Exists(Path.Combine(dir, "transcript.md"));
        m.HasSummary = File.Exists(Path.Combine(dir, "summary.md"));
        m.Live = dir == recordingFolder && recorder != null;
        return m;
    }

    void RefreshList(string select)
    {
        select ??= Selected()?.Folder;
        lstMeetings.BeginUpdate();
        lstMeetings.Items.Clear();
        foreach (var dir in Directory.GetDirectories(Paths.MeetingsDir).OrderByDescending(d => d, StringComparer.Ordinal))
        {
            var m = Describe(dir);
            if (query.Length > 0 && !Matches(m, query)) continue;
            var parts = new List<string>();
            if (m.When != null) parts.Add(m.When.Value.ToString("HH:mm"));
            if (m.Seconds >= 0) parts.Add(Duration(m.Seconds));
            parts.Add(m.Live ? "Recording" : !m.HasTranscript ? "Not transcribed" : m.HasSummary ? "Summarised" : "Transcribed");
            lstMeetings.Items.Add(new RowList.Row
            {
                Title = m.When != null ? Friendly(m.When.Value) : Path.GetFileName(dir),
                Sub = string.Join("  ·  ", parts),
                Dot = m.Live ? Theme.Rec : null,
                Tag = m,
            });
        }
        lstMeetings.EndUpdate();
        for (int i = 0; i < lstMeetings.Items.Count; i++)
            if (((RowList.Row)lstMeetings.Items[i]).Tag is Meeting m && m.Folder == select) lstMeetings.SelectedIndex = i;
        if (lstMeetings.SelectedIndex < 0 && lstMeetings.Items.Count > 0) lstMeetings.SelectedIndex = 0;
        renderedKey = null; // the files may have changed
        ShowSelected();
    }

    // Search: folder name, the friendly date, or any text in the transcript/summary.
    bool Matches(Meeting m, string q)
    {
        const StringComparison ic = StringComparison.OrdinalIgnoreCase;
        if (Path.GetFileName(m.Folder).Contains(q, ic)) return true;
        if (m.When != null && (Friendly(m.When.Value) + " " + m.When.Value.ToString("dddd d MMMM")).Contains(q, ic)) return true;
        foreach (var f in new[] { "transcript.md", "summary.md" })
        {
            try { string p = Path.Combine(m.Folder, f); if (File.Exists(p) && File.ReadAllText(p).Contains(q, ic)) return true; } catch { }
        }
        return false;
    }

    // "You 38% · Others 62%" of the words spoken.
    static string TalkShare(string transcriptPath)
    {
        try
        {
            int you = 0, others = 0;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(transcriptPath), @"(?m)^\[[\d:]+\]\s*(You|Others):\s*(.*)$"))
            {
                int words = m.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                if (m.Groups[1].Value == "You") you += words; else others += words;
            }
            if (you + others < 20 || you == 0 || others == 0) return null;
            int yp = (int)Math.Round(100.0 * you / (you + others));
            return $"You {yp}%  ·  Others {100 - yp}%";
        }
        catch { return null; }
    }

    // Settings.AudioKeepDays > 0: drop the big WAVs of old meetings once they have a transcript.
    void CleanOldAudio()
    {
        if (settings.AudioKeepDays <= 0) return;
        foreach (var dir in Directory.GetDirectories(Paths.MeetingsDir))
        {
            var m = Describe(dir);
            if (m.When == null || m.Live || !m.HasTranscript || (DateTime.Now - m.When.Value).TotalDays <= settings.AudioKeepDays) continue;
            foreach (var f in new[] { "mic.wav", "system.wav" }) { try { File.Delete(Path.Combine(dir, f)); } catch { } }
        }
    }

    Meeting Selected() => lstMeetings.SelectedRow?.Tag as Meeting;

    void ShowSelected()
    {
        var m = Selected();
        string rtf, key;
        if (m == null)
        {
            titleText = "VowenLite";
            metaText = query.Length > 0 ? "No matches" : "No meetings yet";
            docHead.Invalidate();
            key = "none|" + query;
            rtf = query.Length > 0
                ? DocRenderer.Message("No meetings match", "Nothing contains “" + query + "”.")
                : DocRenderer.Message("Nothing recorded yet", "Press Record to capture your mic and the PC's sound. Hold Right Shift anywhere to dictate.");
        }
        else
        {
            titleText = m.When != null ? m.When.Value.ToString(m.When.Value.Year == DateTime.Today.Year ? "dddd d MMMM" : "dddd d MMMM yyyy") : Path.GetFileName(m.Folder);
            var meta = new List<string>();
            if (m.When != null) meta.Add(m.When.Value.ToString("HH:mm"));
            if (m.Seconds >= 0) meta.Add(Duration(m.Seconds));
            meta.Add(m.Live ? (m.HasTranscript ? "Recording · transcript so far" : "Recording") : !m.HasTranscript ? "Not transcribed" : m.HasSummary ? "Transcript and summary" : "Transcript only");
            if (m.HasTranscript && TalkShare(Path.Combine(m.Folder, "transcript.md")) is { } share) meta.Add(share);
            metaText = string.Join("  ·  ", meta);
            docHead.Invalidate();

            string sp = Path.Combine(m.Folder, "summary.md"), tp = Path.Combine(m.Folder, "transcript.md");
            key = m.Folder + "|" + m.Live + "|" + Stamp(sp) + "|" + Stamp(tp);
            if (key == renderedKey) { UpdateButtons(); return; }
            if (m.Live && !m.HasTranscript) rtf = DocRenderer.Message("Recording…", "Press “Transcribe so far” to read and ask about the meeting while it runs. The full transcript and summary come after you stop.");
            else if (m.HasTranscript) rtf = DocRenderer.Document(m.HasSummary ? File.ReadAllText(sp) : "", File.ReadAllText(tp));
            else rtf = DocRenderer.Message("No transcript yet", m.HasAudio ? "Press Transcribe to turn the audio into text." : "This meeting has no audio to transcribe.");
        }
        renderedKey = key;
        rtb.Rtf = rtf;
        rtb.SelectionStart = 0;
        rtb.ScrollToCaret();
        UpdateButtons();
    }

    static string Stamp(string path) => File.Exists(path) ? new FileInfo(path).LastWriteTimeUtc.Ticks.ToString() : "-";

    void UpdateButtons()
    {
        var m = Selected();
        bool live = m != null && m.Live;
        btnRecord.Text = recorder == null ? "Record" : btnRecord.Text.StartsWith("Stop") ? btnRecord.Text : "Stop";
        btnRecord.Enabled = !transcribing;
        btnMic.Enabled = recorder == null;
        btnTranscribe.Enabled = live ? !snapping && recorder != null : m != null && !transcribing && m.HasAudio;
        btnTranscribe.Text = live ? (snapping ? "Transcribing…" : "Transcribe so far") : transcribing ? "Transcribing…" : "Transcribe";
        btnSummarise.Enabled = m != null && !summarising && m.HasTranscript;
        btnSummarise.Text = summarising ? "Summarising…" : m != null && m.HasSummary ? "Re-summarise" : "Summarise";
        btnCopy.Enabled = m != null && m.HasTranscript;
        ask.SetMeeting(m?.Folder, m != null && m.HasTranscript);
        btnOpen.Enabled = m != null;
        btnDelete.Enabled = m != null && !live && !transcribing;
        foreach (var b in new[] { btnRecord, btnTranscribe, btnSummarise, btnCopy, btnOpen, btnDelete }) b.Redraw();
    }

    void CopyText()
    {
        var m = Selected();
        if (m == null) return;
        string text = "";
        string sp = Path.Combine(m.Folder, "summary.md"), tp = Path.Combine(m.Folder, "transcript.md");
        if (File.Exists(sp)) text += File.ReadAllText(sp).Trim() + "\r\n\r\n---\r\n\r\n";
        if (File.Exists(tp)) text += File.ReadAllText(tp);
        if (text.Length == 0) return;
        Clipboard.SetText(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        SetStatus("Copied the summary and transcript.");
    }

    // Delete asks for a second click instead of a dialog.
    void Disarm()
    {
        disarm.Stop();
        if (btnDelete.Text != "Delete") { btnDelete.Text = "Delete"; btnDelete.Redraw(); }
    }

    void DeleteSelected()
    {
        var m = Selected();
        if (m == null) return;
        if (btnDelete.Text == "Delete")
        {
            btnDelete.Text = "Move to Recycle Bin?";
            btnDelete.Redraw();
            disarm.Start();
            return;
        }
        Disarm();
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(m.Folder,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            SetStatus("Moved " + Path.GetFileName(m.Folder) + " to the Recycle Bin.");
        }
        catch (Exception ex) { SetStatus("Couldn't delete: " + ex.Message, true); }
        RefreshList(null);
    }

    // The app never summarises itself: Codex's CLI writes summary.md. Without the CLI the prompt
    // and transcript go to the clipboard and the Codex app opens.
    async void SendToCodex()
    {
        var m = Selected();
        if (m == null) return;
        if (Summariser.FindCodex(settings) != null) { await Summarise(m.Folder); return; }
        Clipboard.SetText(Summariser.Prompt(File.ReadAllText(Path.Combine(m.Folder, "transcript.md"))));
        try { Process.Start("explorer.exe", "shell:AppsFolder\\" + settings.CodexAppId); } catch { }
        SetStatus("Codex CLI not found: prompt copied, paste it into Codex.", true);
    }
}
