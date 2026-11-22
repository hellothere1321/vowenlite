using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VowenLite;

// Roadmap 2.4 + 4.1: Start sets up the work, and time tracking.
//   Start(task): opens task.Link (URL / file / folder; a repo in VS Code), starts a focus timer for the block, minimizes the
//                Settings.Distractions apps. While focused, one of those coming to the front shows a quiet Nudge ("12 min left on lab report").
//   Sample (every 15 s, daemon): front window's process + user idle time -> TimeLog.Add (seconds); the active task's Item.Spent grows.
static class Focus
{
    sealed class State { public string TaskId { get; set; } public string Title { get; set; } public DateTime StartedAt { get; set; } public DateTime EndsAt { get; set; } }
    static State cur;
    static bool loaded;
    static readonly object gate = new();
    static string FilePath => Path.Combine(Paths.DataDir, "focus.json");

    // Tests swap this: (process name of the front window or null, seconds since the last key/mouse input).
    internal static Func<(string App, int IdleSec)> Foreground = RealForeground;

    static DateTime lastNudge = DateTime.MinValue, lastFlush = DateTime.MinValue;
    static string pendingId;
    static int pendingSec;

    public static string ActiveTaskId { get { lock (gate) return Current(DateTime.Now)?.TaskId; } }

    static State Current(DateTime now)
    {
        if (!loaded)
        {
            loaded = true;
            try { if (File.Exists(FilePath)) cur = JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath)); } catch { cur = null; }
        }
        if (cur != null && (now >= cur.EndsAt || TimerGone())) { cur = null; timerSeen = false; Save(); }   // the block is over, or "stop focus" / "cancel the timers" ended it
        return cur;
    }

    // The existing "stop focus" / "cancel timers" commands only cancel the focus timer. Once it was seen running in this process and
    // is gone, the focus is over (after a restart, before the timer is seen, only EndsAt counts).
    static bool timerSeen;
    static bool TimerGone()
    {
        bool running;
        try { running = Commands.Running().Any(t => t.Id == "focus"); } catch { return false; }
        if (running) timerSeen = true;
        return timerSeen && !running;
    }

    static void Save()
    {
        try { if (cur == null) File.Delete(FilePath); else File.WriteAllText(FilePath, JsonSerializer.Serialize(cur)); } catch { }
    }

    internal static void Reset() { lock (gate) { cur = null; loaded = false; pendingId = null; pendingSec = 0; lastNudge = lastFlush = DateTime.MinValue; } }

    // ---------------- Start / end

    public static string Start(Item task, Settings s)
    {
        if (task == null) return null;
        var now = DateTime.Now;
        int mins = 25;
        try
        {
            var blk = Planner.Occurrences(now, now.AddMinutes(1)).FirstOrDefault(o => o.Item.BlockFor == task.Id);
            if (blk != null && (blk.End - now).TotalMinutes >= 1) mins = (int)Math.Round((blk.End - now).TotalMinutes);
        }
        catch { }
        var mini = (s?.Distractions ?? new()).Select(NormApp).Where(x => x.Length > 0).ToList();
        string link = (task.Link ?? "").Trim();
        if (Luna.DryRun)
            return Luna.Dry($"start {task.Title} · {mins} min" + (link.Length > 0 ? ", open " + link : "") + (mini.Count > 0 ? ", minimize " + string.Join("/", mini) : ""));

        if (link.Length > 0) OpenLink(link);
        Commands.CancelTimer("focus");
        Commands.Schedule(TimeSpan.FromMinutes(mins), "Focus", "Focus", $"Focus block done: {task.Title}.", quiet: true, id: "focus");
        MinimizeDistractions(mini);
        lock (gate) { Current(now); cur = new State { TaskId = task.Id, Title = task.Title, StartedAt = now, EndsAt = now.AddMinutes(mins) }; Save(); lastNudge = DateTime.MinValue; }
        Luna.Log($"focus: started {mins} min");
        return $"✓ Started {task.Title} · {mins} min";
    }

    static void OpenLink(string link)
    {
        try
        {
            if (Directory.Exists(link) && Directory.Exists(Path.Combine(link, ".git")))
                Process.Start(new ProcessStartInfo("cmd.exe", "/c code \"" + link + "\"") { CreateNoWindow = true, UseShellExecute = false });
            else _ = Luna.Open(link);
        }
        catch (Exception ex) { Luna.Log("focus: open link failed: " + ex.Message); }
    }

    static string EndFocus()
    {
        State st;
        lock (gate) { st = Current(DateTime.Now); cur = null; Save(); FlushSpent(DateTime.Now, true); }
        if (st == null) return null;
        Commands.CancelTimer("focus");
        Nudge.Remove("focus-dist");
        return "✓ Focus ended";
    }

    // ---------------- commands

    static readonly string[] Stop = { "the", "a", "an", "on", "my", "to", "for", "of", "and", "up", "that", "this", "it" };
    static List<string> Words(string t) => Regex.Matches((t ?? "").ToLowerInvariant(), @"[a-z0-9]+").Select(m => m.Value).ToList();

    // An open task whose title contains every significant word of the phrase (words may be prefixes); shortest title wins.
    internal static Item FuzzyTask(string phrase, IEnumerable<Item> tasks)
    {
        var q = Words(phrase).Where(w => !Stop.Contains(w)).ToList();
        if (q.Count == 0 || q.Any(w => w is "timer" or "pomodoro" or "focus" or "minutes" or "minute" or "hour" or "hours" or "stopwatch" or "alarm")) return null;
        if (q.All(w => w.Length < 3)) return null;
        return tasks.Where(t => t.Kind == "task" && !t.Done)
            .Where(t => { var tw = Words(t.Title); return q.All(w => tw.Any(x => x == w || (w.Length >= 3 && x.StartsWith(w)))); })
            .OrderBy(t => Words(t.Title).Count).FirstOrDefault();
    }

    static string Done(Item t)
    {
        if (Luna.DryRun) return Luna.Dry($"mark {t.Title} done and end focus");
        Planner.SetDone(t.Id, true);
        EndFocus();
        return $"✓ Done: {t.Title}";
    }

    public static string TryLocal(string l, Settings s)
    {
        Match m;
        if (Regex.IsMatch(l, @"^(?:start|begin)(?: on)?(?: the)?(?: next(?: thing| task| one)?| now| working| something)$", RegexOptions.IgnoreCase))
        {
            var pick = Schedule.NowPick(DateTime.Now);
            return pick == null ? "Nothing to start: no open tasks." : Start(pick, s);
        }
        if ((m = Regex.Match(l, @"^(?:start|begin)(?: on| working on)? (.+)$", RegexOptions.IgnoreCase)).Success)
        {
            var t = FuzzyTask(m.Groups[1].Value, Planner.OpenTasks());
            return t == null ? null : Start(t, s);
        }
        // "with it/this/that" is required: a bare "done" / "I'm done" resumes Luna's waiting request (Threads.IsContinue), even mid-focus.
        if (Regex.IsMatch(l, @"^(?:(?:i'?m|i am) )?(?:done|finished) with (?:it|this|that)$", RegexOptions.IgnoreCase))
        {
            string id = ActiveTaskId;
            var t = id == null ? null : Planner.OpenTasks().FirstOrDefault(x => x.Id == id);
            return t == null ? null : Done(t);
        }
        if ((m = Regex.Match(l, @"^(?:(?:i'?m|i am) )?(?:done|finished) with (.+)$", RegexOptions.IgnoreCase)).Success)
        {
            var t = FuzzyTask(m.Groups[1].Value, Planner.OpenTasks());
            return t == null ? null : Done(t);
        }
        if (Regex.IsMatch(l, @"^(?:stop|end|finish|exit|cancel)(?: the| my)? focus(?: session| mode| block)?$", RegexOptions.IgnoreCase))
        {
            if (Luna.DryRun) return Luna.Dry("end focus");
            return EndFocus() ?? "✓ No focus session running";
        }
        return null;
    }

    // ---------------- distractions

    static string NormApp(string n) => Regex.Replace((n ?? "").Trim().ToLowerInvariant(), @"\.exe$", "");

    delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO i);
    struct LASTINPUTINFO { public uint cbSize, dwTime; }

    static string ProcName(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return NormApp(p.ProcessName); } catch { return null; }
    }

    static void MinimizeDistractions(List<string> names)
    {
        if (names.Count == 0) return;
        var byPid = new Dictionary<uint, string>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (!byPid.TryGetValue(pid, out var n)) byPid[pid] = n = ProcName(pid);
            if (n != null && names.Contains(n)) ShowWindow(h, 6 /* SW_MINIMIZE */);
            return true;
        }, IntPtr.Zero);
    }

    static (string App, int IdleSec) RealForeground()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        int idle = GetLastInputInfo(ref li) ? (int)((uint)Environment.TickCount - li.dwTime) / 1000 : 0;
        string app = null;
        var h = GetForegroundWindow();
        if (h != IntPtr.Zero) { GetWindowThreadProcessId(h, out uint pid); app = ProcName(pid); }
        return (app, idle);
    }

    // ---------------- sampling (daemon, every 15 s)

    public static void Sample(Settings s, DateTime now)
    {
        var (app, idle) = Foreground();
        if (idle > 120) return;   // away from the keyboard: nothing counts
        string taskId; State st;
        lock (gate) { st = Current(now); taskId = st?.TaskId; }
        if (st != null && app != null && (s?.Distractions ?? new()).Select(NormApp).Contains(app) && now - lastNudge >= TimeSpan.FromMinutes(5))
        {
            lastNudge = now;
            int left = Math.Max(1, (int)Math.Ceiling((st.EndsAt - now).TotalMinutes));
            Nudge.Show("focus-dist", $"{left} min left on {st.Title}", "Back to it?", ttl: TimeSpan.FromMinutes(2));
        }
        if (s != null && !s.TrackTime) return;
        TimeLog.Add(now, app, taskId, 15);
        if (taskId != null)
        {
            lock (gate)
            {
                if (pendingId != taskId) { FlushSpent(now, true); pendingId = taskId; lastFlush = now; }
                pendingSec += 15;
                FlushSpent(now, false);
            }
        }
    }

    // Adds the whole minutes worked on the active task to Item.Spent (local-only field: Edit, so Google isn't pushed).
    static void FlushSpent(DateTime now, bool force)
    {
        if (pendingId == null || pendingSec < 60) return;
        if (!force && now - lastFlush < TimeSpan.FromMinutes(5)) return;
        int add = pendingSec / 60; string id = pendingId;
        pendingSec -= add * 60; lastFlush = now;
        try { Planner.Edit(l => { var x = l.Find(i => i.Id == id); if (x != null) x.Spent += add; }); } catch { }
    }

    // ---------------- tests

    public static void SelfChecks(Action<bool, string> check)
    {
        var tasks = new List<Item> { new() { Id = "1", Kind = "task", Title = "Lab 4 report" }, new() { Id = "2", Kind = "task", Title = "Email Sam" } };
        check(FuzzyTask("the lab report", tasks)?.Id == "1", "focus: fuzzy task match");
        check(FuzzyTask("email", tasks)?.Id == "2", "focus: fuzzy single word");
        check(FuzzyTask("spotify", tasks) == null && FuzzyTask("a timer for 5 minutes", tasks) == null && FuzzyTask("a pomodoro", tasks) == null, "focus: unrelated phrases are not tasks");
        check(NormApp("Discord.exe") == "discord", "focus: app name normalised");
    }

    public static int Test(string[] args)
    {
        int bad = 0;
        void C(bool ok, string what) { if (!ok) { bad++; Console.WriteLine("FAIL " + what); } }
        Reset(); TimeLog.Reset();
        var s = Settings.Load();
        string dir = Path.Combine(Paths.DataDir, "focusdir"); Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "notes.txt"); File.WriteAllText(file, "x");
        var t = Planner.AddTask("Test essay", DateTime.Today.AddDays(3));
        Planner.Edit(l => { var x = l.Find(i => i.Id == t.Id); x.Link = file; x.Est = 60; });
        var task = Planner.Live().First(i => i.Id == t.Id);

        Luna.DryRun = true;
        string dry = Start(task, s);
        Console.WriteLine(dry);
        C(dry != null && dry.StartsWith("DRY RUN") && dry.Contains("notes.txt") && ActiveTaskId == null, "dry run reports, does nothing");
        foreach (var p in new[] { "start a pomodoro", "start spotify", "start a timer for 5 minutes" })
            C(TryLocal(p, s) == null, "not taken: " + p);
        C(TryLocal("start test essay", s)?.StartsWith("DRY RUN") == true, "start <task title> matched");
        Luna.DryRun = false;

        // focus state set directly (a real Start would open and minimise things)
        lock (gate) { cur = new State { TaskId = t.Id, Title = task.Title, StartedAt = DateTime.Now, EndsAt = DateTime.Now.AddMinutes(30) }; Save(); }
        C(ActiveTaskId == t.Id, "active task");
        Reset(); C(ActiveTaskId == t.Id, "focus.json survives a restart");
        Foreground = () => ("code", 5);
        var at = DateTime.Today.AddHours(14);
        for (int i = 0; i < 8; i++) Sample(s, at.AddSeconds(15 * i));   // 120 s
        Foreground = () => ("code", 500);
        Sample(s, at.AddMinutes(5));   // idle: nothing
        var day = TimeLog.Day(at);
        C(day.Tasks.GetValueOrDefault(t.Id) == 120 && day.Apps.GetValueOrDefault("code") == 120, "sample adds to TimeLog (idle ignored)");
        lock (gate) FlushSpent(at.AddMinutes(6), true);
        C(Planner.Live().First(i => i.Id == t.Id).Spent == 2, "Spent grew by 2 min");
        EndFocus();
        C(ActiveTaskId == null, "focus ended");
        Foreground = RealForeground;
        if (bad == 0) Console.WriteLine("focustest ok");
        return bad == 0 ? 0 : 1;
    }
}
