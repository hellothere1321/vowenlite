using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace VowenLite;

// The Plan window: a small web app (PlanUi.html, embedded) in a WebView2 pane, talking to the planner through messages.
// Today / Calendar (day, week, month) / Tasks / Upcoming, repeating items, lists, quick add in plain words, two-way
// Google sync. `VowenLite.exe --plan [today|week|month|day|tasks|upcoming]`. One window at a time: a second launch
// just brings the open one forward (and switches its view).
sealed class PlanForm : Form
{
    public static readonly string ViewFile = Path.Combine(Paths.DataDir, "plan-view.txt");
    const string ShowEvent = "Local\\VowenLite.PlanShow";

    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Bg };
    readonly System.Windows.Forms.Timer tick = new() { Interval = 1500 };
    readonly string startView;
    readonly EventWaitHandle showSignal;
    DateTime from = DateTime.Today.AddDays(-7), to = DateTime.Today.AddDays(45), seen = DateTime.MinValue, lastSync = DateTime.MinValue;
    string lastGoogle = "";
    bool ready;
    public Action Loaded;                      // for --planshot

    public PlanForm(string view)
    {
        startView = string.IsNullOrWhiteSpace(view) ? "today" : view;
        Text = "VowenLite · Plan";
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg; ForeColor = Theme.Ink;
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        ClientSize = new Size(Math.Min(1320, (int)(wa.Width * 0.82)), Math.Min(860, (int)(wa.Height * 0.86)));
        MinimumSize = new Size(820, 560);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
        try { Icon = Theme.AppIcon(); } catch { }
        Controls.Add(web);

        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
        new Thread(() =>
        {
            while (true)
            {
                try { showSignal.WaitOne(); } catch { return; }
                try { BeginInvoke(new Action(BringForward)); } catch { return; }
            }
        }) { IsBackground = true, Name = "plan-show" }.Start();

        Load += async (_, _) => await Start();
        tick.Tick += async (_, _) => await Tick();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitleBar(Handle); }

    void BringForward()
    {
        string v = "";
        try { v = File.Exists(ViewFile) ? File.ReadAllText(ViewFile).Trim() : ""; File.Delete(ViewFile); } catch (Exception ex) { Log("view file: " + ex.Message); }
        Log("brought forward" + (v.Length > 0 ? " (" + v + ")" : ""));
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        TopMost = true; TopMost = false; Activate();
        if (v.Length > 0) Send(new JsonObject { ["type"] = "nav", ["view"] = v });
        Send(new JsonObject { ["type"] = "focus" });
    }

    static string Html()
    {
        var asm = Assembly.GetExecutingAssembly();
        string name = asm.GetManifestResourceNames().First(n => n.EndsWith("PlanUi.html", StringComparison.OrdinalIgnoreCase));
        using var s = asm.GetManifestResourceStream(name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    async Task Start()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.DataDir, "webview-plan"));
            await web.EnsureCoreWebView2Async(env);
            var st = web.CoreWebView2.Settings;
            st.AreDevToolsEnabled = false; st.IsStatusBarEnabled = false; st.AreBrowserAcceleratorKeysEnabled = false;
            st.IsGeneralAutofillEnabled = false; st.IsPasswordAutosaveEnabled = false; st.IsZoomControlEnabled = false; st.IsSwipeNavigationEnabled = false;
            web.CoreWebView2.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("data:") && !e.Uri.StartsWith("about:")) e.Cancel = true; };
            web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            web.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            web.CoreWebView2.WebMessageReceived += (_, e) => { try { OnMessage(e.TryGetWebMessageAsString()); } catch (Exception ex) { Log("message: " + ex.Message); Toast("Something went wrong: " + ex.Message, true); } };
            web.NavigateToString(Html());
            tick.Start();
        }
        catch (Exception ex)
        {
            Log("webview failed: " + ex.Message);
            MessageBox.Show("The planner needs the Microsoft Edge WebView2 runtime (preinstalled on Windows 11).\n\n" + ex.Message, "VowenLite Plan");
            Close();
        }
    }

    public Task<string> RunScript(string js) => web.CoreWebView2.ExecuteScriptAsync(js);
    public Task CaptureAsync(Stream to) => web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, to);

    static void Log(string s) { try { File.AppendAllText(Path.Combine(Paths.DataDir, "daemon.log"), DateTime.Now.ToString("s") + " plan: " + s + Environment.NewLine); } catch { } }

    void Send(JsonNode n) { try { if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(n.ToJsonString()); } catch { } }
    void Toast(string text, bool error = false) => Send(new JsonObject { ["type"] = "toast", ["text"] = text, ["error"] = error });

    JsonObject GoogleJson() => new() { ["connected"] = GoogleSync.Connected, ["hasClient"] = GoogleSync.HasClient, ["status"] = GoogleSync.Status };

    void Push()
    {
        seen = Planner.LastWrite;
        var o = Planner.Snapshot(from, to);
        o["google"] = GoogleJson();
        lastGoogle = GoogleSync.Status;
        Send(o);
    }

    async Task Tick()
    {
        if (!ready) return;
        if (GoogleSync.Connected && DateTime.Now - lastSync > TimeSpan.FromSeconds(60)) { lastSync = DateTime.Now; _ = GoogleSync.SyncNowAsync(); }
        if (Planner.LastWrite != seen || GoogleSync.Status != lastGoogle) Push();
        await Task.CompletedTask;
    }

    static DateTime Dt(JsonNode n) => DateTime.Parse((string)n, CultureInfo.InvariantCulture);
    static DateTime? DtOrNull(JsonNode n) => n == null || string.IsNullOrWhiteSpace((string)n) ? null : Dt(n);

    void OnMessage(string json)
    {
        var m = JsonNode.Parse(json);
        string cmd = (string)m["cmd"];
        switch (cmd)
        {
            case "ready":
                ready = true;
                Push();
                if (startView != "today") Send(new JsonObject { ["type"] = "nav", ["view"] = startView });
                if (GoogleSync.Connected) { lastSync = DateTime.Now; _ = GoogleSync.SyncNowAsync(); }
                Loaded?.Invoke();
                break;
            case "range":
                from = Dt(m["from"]); to = Dt(m["to"]);
                Push();
                break;
            case "parse":
            {
                string kind = (string)m["kind"];
                var p = Planner.Parse((string)m["text"], DateTime.Now, kind is "task" or "event" ? kind : null);
                Send(new JsonObject
                {
                    ["type"] = "parsed", ["kind"] = p.Kind, ["title"] = p.Title, ["reminder"] = p.Reminder, ["when"] = WhenText(p),
                    ["repeat"] = p.Repeat?.Describe(p.Start ?? DateTime.Today), ["cat"] = p.Cat, ["color"] = Planner.CatColor(p.Cat), ["prio"] = p.Prio
                });
                break;
            }
            case "add":
            {
                string kind = (string)m["kind"];
                var p = Planner.Parse((string)m["text"], DateTime.Now, kind is "task" or "event" ? kind : null);
                if (p.Kind == "event" && p.Start == null) { Toast("Add a day or time for the event, e.g. “friday 3pm”", true); break; }
                if (p.Title.Length == 0) { Toast("What should it be called?", true); break; }
                Planner.Add(p);
                Toast(Planner.AddedText(p));
                Push();
                break;
            }
            case "save": Save(m); Push(); break;
            case "toggle":
            {
                string id = (string)m["id"];
                var before = Planner.Live().FirstOrDefault(i => i.Id == id);
                Planner.SetDone(id, before != null && !before.Done);
                var after = Planner.Live().FirstOrDefault(i => i.Id == id);
                if (before != null && after != null && !before.Done)
                    Toast(!after.Done && after.Start != null ? "Done · back on " + Planner.DayWord(after.Start.Value) : "Done");
                Push();
                break;
            }
            case "delete":
            {
                string id = (string)m["id"];
                if ((string)m["scope"] == "one" && m["occ"] != null) Planner.SkipOccurrence(id, Dt(m["occ"]));
                else Planner.Remove(id);
                Toast("Deleted");
                Push();
                break;
            }
            case "move": Move(m); Push(); break;
            case "sync":
                if (!GoogleSync.Connected) { Toast("Connect Google first", true); break; }
                Toast("Syncing…");
                _ = Task.Run(async () =>
                {
                    string s = await GoogleSync.SyncNowAsync();
                    BeginInvoke(new Action(() => { Push(); Toast(s, s.StartsWith("Sync failed")); }));
                });
                break;
            case "connect": _ = Connect(); break;
            case "disconnect": GoogleSync.Disconnect(); Push(); Toast("Disconnected from Google"); break;
        }
    }

    // "Fri Nov 13 · 3–4 PM"
    static string WhenText(Parsed p)
    {
        if (p.Start == null) return "";
        string day = Planner.DayWord(p.Start.Value);
        if (p.Kind == "event" && !p.AllDay)
            return day + " · " + p.Start.Value.ToString("h:mm tt") + (p.End != null && !p.Reminder ? " – " + p.End.Value.ToString("h:mm tt") : "");
        return day + (p.Kind == "event" ? " · all day" : "");
    }

    async Task Connect()
    {
        if (!GoogleSync.HasClient)
        {
            using var dlg = new OpenFileDialog { Title = "Choose the OAuth client file from Google Cloud (client_secret_….json)", Filter = "JSON|*.json|All files|*.*" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try { GoogleSync.ImportClient(dlg.FileName); } catch (Exception ex) { Toast("Couldn't read that file: " + ex.Message, true); return; }
        }
        Toast("Opening Google sign-in in your browser…");
        string res = await GoogleSync.SignInAsync();
        Toast(res, !res.StartsWith("Connected"));
        Push();
    }

    void Save(JsonNode m)
    {
        string id = (string)m["id"], kind = (string)m["kind"], title = ((string)m["title"] ?? "").Trim(), notes = (string)m["notes"] ?? "";
        string cat = Planner.Catify((string)m["cat"]);
        int prio = Math.Clamp((int?)m["prio"] ?? 0, 0, 3);
        var rule = Rule.Parse((string)m["rrule"]);
        bool allDay = (bool?)m["allDay"] ?? false;
        string scope = (string)m["scope"] ?? "all";
        if (title.Length == 0) return;

        if (kind == "task")
        {
            var due = DtOrNull(m["date"])?.Date;
            if (rule != null && due != null) due = rule.FirstOn(due.Value);
            if (id == null) Planner.AddTask(title, due, notes, rule, cat, prio);
            else Planner.Update(id, x => { x.Title = When.Cap(title); x.Notes = notes; x.Start = due; x.Cat = cat; x.Priority = prio; x.Repeat = due == null ? null : rule?.ToRrule(); });
            Toast(id == null ? "Task added" : "Saved");
            return;
        }

        DateTime start = allDay ? Dt(m["date"]).Date : Dt(m["start"]);
        DateTime end = allDay ? (DtOrNull(m["endDate"]) ?? start).Date : Dt(m["end"]);
        if (!allDay && end <= start) end = start.AddMinutes(30);
        if (allDay && end < start) end = start;
        int alert = (int?)m["alert"] ?? 10;
        if (id == null)
        {
            if (rule != null) { var f = rule.FirstOn(start); if (f != start) { var span = end - start; start = f; end = start + span; } }
            Planner.AddEvent(title, start, end, allDay, alert, rule, cat, prio, notes);
            Toast("Event added");
            return;
        }
        var cur = Planner.Live().FirstOrDefault(i => i.Id == id);
        if (cur == null) return;
        DateTime? occ = DtOrNull(m["occ"]);
        if (scope == "one" && cur.Repeat != null && occ != null)
        {
            Planner.SkipOccurrence(id, occ.Value);                          // this one leaves the series and becomes its own event
            Planner.AddEvent(title, start, end, allDay, alert, null, cat, prio, notes);
            Toast("Saved this event only");
            return;
        }
        Planner.Update(id, x =>
        {
            var old = x.Start ?? start;
            var delta = occ != null && x.Repeat != null ? start - (allDay ? occ.Value.Date : occ.Value) : start - old;
            x.Title = When.Cap(title); x.Notes = notes; x.Cat = cat; x.Priority = prio; x.Alert = alert;
            x.AllDay = allDay;
            x.Start = (x.Start ?? start) + delta;
            x.End = x.Start + (end - start) + (allDay ? TimeSpan.FromDays(1) : TimeSpan.Zero);
            string was = x.Repeat;
            x.Repeat = rule?.ToRrule(allDay);
            if (x.Repeat != null && Rule.Parse(x.Repeat) is { Freq: "WEEKLY" } r2 && r2.Days.Count == 0) { r2.Days = new() { x.Start.Value.DayOfWeek }; x.Repeat = r2.ToRrule(allDay); }
            if (delta != TimeSpan.Zero || was != x.Repeat) x.Skip.Clear();
            x.Fired = false; x.FiredFor = null;
        });
        Toast("Saved");
    }

    // Drag on the calendar. A repeating event: only that occurrence moves (it leaves the series); a one-off just moves.
    void Move(JsonNode m)
    {
        string id = (string)m["id"];
        DateTime start = Dt(m["start"]), end = Dt(m["end"]);
        var cur = Planner.Live().FirstOrDefault(i => i.Id == id);
        if (cur == null) return;
        if (cur.Repeat != null && m["occ"] != null)
        {
            Planner.SkipOccurrence(id, Dt(m["occ"]));
            Planner.AddEvent(cur.Title, start, end, false, cur.Alert, null, cur.Cat, cur.Priority, cur.Notes);
            Toast("Moved this one");
            return;
        }
        Planner.Update(id, x => { x.Start = start; x.End = end; x.Fired = false; x.FiredFor = null; });
        Toast("Moved to " + Planner.DayWord(start) + " " + start.ToString("h:mm tt"));
    }
}

// `VowenLite.exe --planshot <png> [view] [width height]`: opens the Plan window on throw-away demo data, saves a screenshot, exits.
static class PlanShot
{
    public static int Run(string[] args)
    {
        string png = args[1], view = args.Length > 2 ? args[2] : "today";
        int w = args.Length > 4 ? int.Parse(args[3]) : 1320, h = args.Length > 4 ? int.Parse(args[4]) : 860;
        string dir = Environment.GetEnvironmentVariable("VOWENLITE_DATA") ?? Path.Combine(Path.GetTempPath(), "vowenlite-planshot");
        try { Directory.Delete(dir, true); } catch { }
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("VOWENLITE_DATA", dir);     // before anything touches Paths
        Seed();
        ApplicationConfiguration.Initialize();
        var f = new PlanForm(view);
        f.ClientSize = new Size(w, h);
        f.Loaded = () => f.BeginInvoke(new Action(async () =>
        {
            await Task.Delay(1800);
            string js = Environment.GetEnvironmentVariable("PLANSHOT_JS");
            if (!string.IsNullOrEmpty(js)) { await f.RunScript(js); await Task.Delay(900); }
            using var fs = File.Create(png);
            await f.CaptureAsync(fs);
            f.Close();
        }));
        Application.Run(f);
        return 0;
    }

    static void Seed()
    {
        var t = DateTime.Today;
        DateTime At(int d, int hh, int mm = 0) => t.AddDays(d).AddHours(hh).AddMinutes(mm);
        var wk = new Rule { Freq = "WEEKLY", Days = new() { DayOfWeek.Monday, DayOfWeek.Wednesday } };
        var wd = new Rule { Freq = "WEEKLY", Days = new() { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday } };
        var gym = new Rule { Freq = "WEEKLY", Days = new() { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday } };
        DateTime Mon0(DateTime d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
        Planner.AddEvent("Digital design lecture", Mon0(t).AddHours(9), Mon0(t).AddHours(10.25), false, 10, wk, "School");
        Planner.AddEvent("Standup", Mon0(t).AddHours(9.5), Mon0(t).AddHours(9.75), false, 5, wd);
        Planner.AddEvent("Gym", Mon0(t).AddHours(18), Mon0(t).AddHours(19), false, 15, gym, "Health");
        Planner.AddEvent("Lunch with Sam", At(0, 12, 30), At(0, 13, 30), false, 10, null, "Personal");
        Planner.AddEvent("Study group", At(0, 16), At(0, 18), false, 10, null, "School");
        Planner.AddEvent("Dentist", At(1, 15), At(1, 16), false, 30, new Rule { Freq = "MONTHLY", Interval = 6 }, "Health");
        Planner.AddEvent("Hackathon", t.AddDays(4), t.AddDays(5), true, 10, null, "School");
        Planner.AddTask("Lab report: op-amp circuits", t.AddDays(-1), "", null, "School", 3);
        Planner.AddTask("Email Prof. Lee about the extension", t, "", null, "School", 2);
        Planner.AddTask("Water the plants", t, "", new Rule { Freq = "WEEKLY", Days = new() { t.DayOfWeek } });
        Planner.AddTask("Pay rent", t.AddDays(2), "", new Rule { Freq = "MONTHLY" }, "Personal", 3);
        Planner.AddTask("Read chapter 7", t.AddDays(3), "", null, "School", 1);
        Planner.AddTask("Renew passport", null, "", null, "Personal");
    }
}
