using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VowenLite;

// Roadmap 2.5, 2.6, 3.4, 4.4: the assistant talks first (never in quiet hours: Nudge.Quiet).
//   Evening rollover (Settings.Rollover): one Nudge: done today, what's slipping, what Schedule moved; click = the full list.
//   Escalation (Settings.Escalate): due tonight and not started -> card (evening), then spoken, then a phone alert (a Google event at that time).
//   Morning brief v2: due today, today's blocks, the Now pick, what Overnight produced. Used by "good morning" / "daily briefing".
//   Sunday review: hours by course (TimeLog), what's due next week, grade outlook (Grades).
// State ("already did X for Y today") lives in coach.json so a restart never repeats a card.
static class Coach
{
    sealed class State
    {
        public string Roll { get; set; } = "";                          // day the rollover card was shown
        public string Week { get; set; } = "";                          // day the Sunday review card was shown
        public Dictionary<string, int> Esc { get; set; } = new();       // "yyyy-MM-dd|taskId" -> last escalation step done (1 card, 2 spoken, 3 phone)
    }

    static readonly object gate = new();
    static string StatePath => Path.Combine(Paths.DataDir, "coach.json");
    internal static Action<string> Say = t => Assistant.Speak(t);       // tests replace it

    static State LoadState()
    {
        try { if (File.Exists(StatePath)) return JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new(); } catch { }
        return new();
    }
    static void SaveState(State st) { try { File.WriteAllText(StatePath, JsonSerializer.Serialize(st)); } catch { } }

    // ---------------- pure helpers

    internal static string DayKey(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // "21:30" reached by now? (bad / empty spec = never)
    internal static bool TimeReached(string hhmm, DateTime now) =>
        TimeSpan.TryParseExact((hhmm ?? "").Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var t) && now.TimeOfDay >= t;

    // 0 = too early, 1 = card (18:00), 2 = spoken (20:00), 3 = phone alert (21:00)
    internal static int EscStep(DateTime now) => now.Hour >= 21 ? 3 : now.Hour >= 20 ? 2 : now.Hour >= 18 ? 1 : 0;

    internal static DateTime WeekStart(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));   // Monday

    internal static string Left(int min) => min >= 90 ? (min / 60.0).ToString("0.#", CultureInfo.InvariantCulture) + " h left" : min + " min left";

    internal static string DueWord(DateTime due, DateTime now)
    {
        int d = (due.Date - now.Date).Days;
        return d < 0 ? "overdue" : d == 0 ? "due today" : d == 1 ? "due tomorrow" : "due " + due.ToString("dddd", CultureInfo.InvariantCulture);
    }

    // A time written in a task title ("Quiz at 9pm", "Essay by 11:59 pm"); null = none.
    internal static TimeSpan? TitleTime(string title)
    {
        var m = Regex.Match(title ?? "", @"\b(\d{1,2})(?::(\d{2}))?\s*(am|pm)\b", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        int h = int.Parse(m.Groups[1].Value) % 12, min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        if (m.Groups[3].Value.Equals("pm", StringComparison.OrdinalIgnoreCase)) h += 12;
        return h < 24 && min < 60 ? new TimeSpan(h, min, 0) : null;
    }

    // Due tonight: dated today, or undated and the title says "tonight"; never started.
    internal static bool DueTonight(Item t, DateTime now) =>
        t.Kind == "task" && !t.Done && !t.Deleted && t.Spent == 0 && string.IsNullOrEmpty(t.BlockFor)
        && (t.Start != null ? t.Start.Value.Date == now.Date : Regex.IsMatch(t.Title ?? "", @"\btonight\b", RegexOptions.IgnoreCase));

    internal static bool DoneOn(Item t, DateTime day) =>
        t.Kind == "task" && !t.Deleted && ((t.Done && t.Updated.ToLocalTime().Date == day.Date) || (t.LastDone != null && t.LastDone.Value.Date == day.Date));

    internal static string Hours(double sec) => sec < 5400 ? Math.Round(sec / 60) + " min" : (sec / 3600).ToString("0.#", CultureInfo.InvariantCulture) + " h";

    static string Time(DateTime d) => d.ToString("h:mm tt", CultureInfo.InvariantCulture);

    // ---------------- tick

    public static void Tick(Settings s, DateTime now)
    {
        if (Nudge.Quiet(s, now)) return;
        lock (gate)
        {
            var st = LoadState();
            bool changed = false;
            string key = DayKey(now);

            // old escalation records are useless after the day ends
            foreach (var k in st.Esc.Keys.Where(k => !k.StartsWith(key)).ToList()) { st.Esc.Remove(k); changed = true; }

            if (!string.IsNullOrWhiteSpace(s.Rollover) && TimeReached(s.Rollover, now) && st.Roll != key)
            {
                st.Roll = key; changed = true;
                Guard("rollover", () => ShowRollover(now));
            }
            if (now.DayOfWeek == DayOfWeek.Sunday && now.Hour >= 18 && st.Week != key)
            {
                st.Week = key; changed = true;
                Guard("week", () => ShowWeek(now));
            }
            if (s.Escalate && EscStep(now) > 0)
                changed |= Escalate(s, now, st);
            if (changed) SaveState(st);
        }
    }

    static void Guard(string what, Action a)
    {
        try { a(); } catch (Exception ex) { Luna.Log($"coach {what}: {ex.GetType().Name}: {ex.Message}"); }
    }

    static bool Escalate(Settings s, DateTime now, State st)
    {
        bool changed = false;
        int step = EscStep(now);
        foreach (var t in Planner.OpenTasks().Where(t => DueTonight(t, now)))
        {
            string k = DayKey(now) + "|" + t.Id;
            if (st.Esc.GetValueOrDefault(k) >= step) continue;
            st.Esc[k] = step; changed = true;   // a late start jumps straight to the latest step, never a burst of all three
            var tm = TitleTime(t.Title);
            string when = tm != null ? " at " + Time(DateTime.Today.Add(tm.Value)) : "";
            try
            {
                if (step == 1)
                {
                    var task = t;
                    Nudge.Show("coach-esc-" + t.Id, $"{t.Title} is due tonight{when} · not started", "Tap to start it now",
                        click: () => { string r = Focus.Start(task, s); if (!string.IsNullOrEmpty(r)) Overlay.Notice(r); }, ttl: TimeSpan.FromHours(2));
                }
                else if (step == 2)
                {
                    if (s.SpeakReplies != "off") Say($"{t.Title} is due tonight{when} and you haven't started it.");
                }
                else Planner.AddEvent("Due tonight: " + t.Title, now.AddMinutes(2), now.AddMinutes(17), false, 0);
            }
            catch (Exception ex) { Luna.Log("coach escalate: " + ex.Message); }
        }
        return changed;
    }

    // ---------------- evening rollover

    sealed class Roll { public List<Item> Done = new(), Slipping = new(), Moved = new(); public string Tomorrow; }

    static Roll Gather(DateTime now)
    {
        var all = Planner.Live();
        var r = new Roll();
        r.Done = all.Where(t => DoneOn(t, now)).OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
        r.Slipping = all.Where(t => t.Kind == "task" && !t.Done && t.Start != null && t.Start.Value.Date <= now.Date.AddDays(1))
            .OrderBy(t => t.Start).ThenByDescending(t => t.Priority).ToList();
        try { r.Moved = Schedule.MovedToday() ?? new(); } catch { }
        var tom = now.Date.AddDays(1);
        var ev = Planner.Events(tom, tom.AddDays(1)).Where(o => !o.AllDay).OrderBy(o => o.Start).FirstOrDefault();
        if (ev != null) r.Tomorrow = Time(ev.Start) + " · " + ev.Title;
        else
        {
            var task = all.Where(t => t.Kind == "task" && !t.Done && t.Start != null && t.Start.Value.Date == tom).OrderByDescending(t => t.Priority).FirstOrDefault();
            if (task != null) r.Tomorrow = task.Title + " (due)";
            else { var ad = Planner.Events(tom, tom.AddDays(1)).FirstOrDefault(); if (ad != null) r.Tomorrow = ad.Title + " · all day"; }
        }
        return r;
    }

    static string SlipLine(Item t, DateTime now) => $"{t.Title} {DueWord(t.Start.Value, now)} · {Left(Schedule.EstimateFor(t))}";

    internal static string RollMarkdown(DateTime now)
    {
        var r = Gather(now);
        var sb = new StringBuilder("## Today\n");
        if (r.Done.Count > 0) { sb.Append($"\n### Done ({r.Done.Count})\n"); foreach (var t in r.Done.Take(10)) sb.Append("• ").Append(t.Title).Append('\n'); }
        else sb.Append("\nNothing ticked off today.\n");
        if (r.Slipping.Count > 0) { sb.Append($"\n### Slipping ({r.Slipping.Count})\n"); foreach (var t in r.Slipping.Take(8)) sb.Append("• ").Append(SlipLine(t, now)).Append('\n'); }
        if (r.Moved.Count > 0) { sb.Append("\n### Moved\n"); foreach (var b in r.Moved.Take(6)) sb.Append("• ").Append(b.Title).Append(b.Start != null ? " · now " + b.Start.Value.ToString("ddd h:mm tt", CultureInfo.InvariantCulture) : "").Append('\n'); }
        if (r.Tomorrow != null) sb.Append("\n### Tomorrow starts with\n• ").Append(r.Tomorrow).Append('\n');
        return sb.ToString().TrimEnd();
    }

    static void ShowRollover(DateTime now)
    {
        var r = Gather(now);
        if (r.Done.Count == 0 && r.Slipping.Count == 0) return;
        string sec = r.Slipping.Count > 0 ? SlipLine(r.Slipping[0], now) : r.Tomorrow != null ? "Tomorrow: " + r.Tomorrow : "";
        string md = RollMarkdown(now);
        Nudge.Show("coach-roll", $"Today: {r.Done.Count} done · {r.Slipping.Count} slipping", sec, click: () => Luna.Show("Today", md), ttl: TimeSpan.FromHours(3));
    }

    // ---------------- Sunday review

    internal static string WeekMarkdown(DateTime now)
    {
        var all = Planner.Live();
        var from = WeekStart(now);
        var byList = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var apps = new Dictionary<string, int>();
        long taskSec = 0, appSec = 0;
        var log = TimeLog.Load();
        for (var d = from; d <= now.Date; d = d.AddDays(1))
        {
            if (!log.TryGetValue(TimeLog.DayKey(d), out var day)) continue;
            foreach (var (id, sec) in day.Tasks)
            {
                var it = all.Find(x => x.Id == id);
                string list = it == null ? "Other" : string.IsNullOrEmpty(it.Cat) ? "Tasks" : it.Cat;
                byList[list] = byList.GetValueOrDefault(list) + sec; taskSec += sec;
            }
            foreach (var (app, sec) in day.Apps) { apps[app] = apps.GetValueOrDefault(app) + sec; appSec += sec; }
        }
        var sb = new StringBuilder("## Your week\n");
        sb.Append("\n### Hours by list\n");
        if (byList.Count == 0) sb.Append("No tracked task time yet.\n");
        foreach (var kv in byList.OrderByDescending(k => k.Value)) sb.Append("• ").Append(kv.Key).Append(" · ").Append(Hours(kv.Value)).Append('\n');
        if (appSec > taskSec && apps.Count > 0)
        {
            sb.Append($"\n### Not on a task ({Hours(appSec - taskSec)}) · top apps\n");
            foreach (var kv in apps.OrderByDescending(k => k.Value).Take(5)) sb.Append("• ").Append(kv.Key).Append(" · ").Append(Hours(kv.Value)).Append('\n');
        }
        var done = all.Where(t => t.Kind == "task" && !t.Deleted && ((t.Done && t.Updated.ToLocalTime().Date >= from && t.Updated.ToLocalTime().Date <= now.Date)
                    || (t.LastDone != null && t.LastDone.Value.Date >= from && t.LastDone.Value.Date <= now.Date))).ToList();
        sb.Append($"\n### Done this week ({done.Count})\n");
        foreach (var t in done.Take(10)) sb.Append("• ").Append(t.Title).Append('\n');
        var next = all.Where(t => t.Kind == "task" && !t.Done && !t.Deleted && t.Start != null && t.Start.Value.Date > now.Date && t.Start.Value.Date <= now.Date.AddDays(7)).OrderBy(t => t.Start).ToList();
        sb.Append($"\n### Due next week ({next.Count})\n");
        foreach (var t in next.Take(10)) sb.Append("• ").Append(t.Title).Append(" · ").Append(t.Start.Value.ToString("ddd MMM d", CultureInfo.InvariantCulture)).Append('\n');
        string outlook = null;
        try { outlook = Grades.Outlook(); } catch { }
        if (!string.IsNullOrWhiteSpace(outlook)) sb.Append("\n### Grades\n").Append(outlook.Trim()).Append('\n');
        return sb.ToString().TrimEnd();
    }

    static void ShowWeek(DateTime now)
    {
        string md = WeekMarkdown(now);
        int due = Planner.OpenTasks().Count(t => t.Start != null && t.Start.Value.Date > now.Date && t.Start.Value.Date <= now.Date.AddDays(7));
        Nudge.Show("coach-week", "Your week", $"{due} due next week · tap for the review", click: () => Luna.Show("Your week", md), ttl: TimeSpan.FromHours(4));
    }

    // ---------------- morning brief

    public static string MorningBrief(DateTime now)
    {
        var sb = new StringBuilder();
        sb.Append("## ").Append(now.ToString("dddd, MMMM d", CultureInfo.InvariantCulture)).Append('\n');
        var tasks = Planner.OpenTasks();
        var due = tasks.Where(t => t.Start != null && t.Start.Value.Date <= now.Date).ToList();
        if (due.Count > 0)
        {
            sb.Append("\n### Due today\n");
            foreach (var t in due.Take(6)) sb.Append("• ").Append(t.Title).Append(t.Start.Value.Date < now.Date ? " · overdue" : "").Append(" · ").Append(Left(Schedule.EstimateFor(t))).Append('\n');
        }
        var occ = Planner.Occurrences(now.Date, now.Date.AddDays(1)).Where(o => o.AllDay ? o.Start.Date == now.Date : o.Start >= now.Date).ToList();
        var blocks = occ.Where(o => !string.IsNullOrEmpty(o.Item.BlockFor)).ToList();
        var events = occ.Where(o => string.IsNullOrEmpty(o.Item.BlockFor)).ToList();
        if (blocks.Count > 0)
        {
            sb.Append("\n### Work blocks\n");
            foreach (var o in blocks.Take(6)) sb.Append("• ").Append(Time(o.Start)).Append("–").Append(Time(o.End)).Append(" · ").Append(o.Title).Append('\n');
        }
        if (events.Count > 0)
        {
            sb.Append("\n### On your calendar\n");
            foreach (var o in events.Take(6)) sb.Append("• ").Append(o.AllDay ? "all day" : Time(o.Start)).Append(" · ").Append(o.Title).Append('\n');
        }
        Item pick = null;
        try { pick = Schedule.NowPick(now); } catch { }
        if (pick != null) sb.Append("\n### Start with\n• ").Append(pick.Title).Append(" · ").Append(Left(Schedule.EstimateFor(pick))).Append('\n');
        List<string> ready = null;
        try { ready = Overnight.ReadyToday(); } catch { }
        if (ready != null && ready.Count > 0) { sb.Append("\n### Ready from overnight\n"); foreach (var l in ready.Take(5)) sb.Append("• ").Append(l).Append('\n'); }
        int other = tasks.Count - due.Count;
        if (other > 0) sb.Append($"\n{other} other open task{(other == 1 ? "" : "s")}\n");
        return sb.ToString().TrimEnd();
    }

    // ---------------- local commands

    public static string TryLocal(string l, Settings s)
    {
        if (Regex.IsMatch(l, @"^(?:how (?:did i do|did today go|was my day)(?: today)?|how'?s my day going)$", RegexOptions.IgnoreCase))
            return Luna.DryRun ? "DRY RUN: show today's rollover" : RollMarkdown(DateTime.Now);
        if (Regex.IsMatch(l, @"^(?:(?:give me |show me |do )?(?:a |my |the )?(?:weekly|week) (?:review|recap|summary)|how was my week|how did my week go|how did i do this week)$", RegexOptions.IgnoreCase))
            return Luna.DryRun ? "DRY RUN: show the weekly review" : WeekMarkdown(DateTime.Now);
        return null;
    }

    // ---------------- checks

    public static void SelfChecks(Action<bool, string> check)
    {
        var d = new DateTime(2026, 11, 22);   // a Sunday
        check(TimeReached("21:30", d.AddHours(21.5)) && !TimeReached("21:30", d.AddHours(21)) && !TimeReached("", d) && !TimeReached("junk", d), "coach: rollover time");
        check(EscStep(d.AddHours(17.9)) == 0 && EscStep(d.AddHours(18)) == 1 && EscStep(d.AddHours(20)) == 2 && EscStep(d.AddHours(21)) == 3, "coach: escalation steps");
        check(WeekStart(d) == new DateTime(2026, 11, 16) && WeekStart(d.AddDays(-6)) == new DateTime(2026, 11, 16) && WeekStart(d.AddDays(1)) == d.AddDays(1), "coach: week starts Monday");
        check(Left(45) == "45 min left" && Left(120) == "2 h left" && Left(100) == "1.7 h left", "coach: work left text");
        check(DueWord(d.AddDays(1), d) == "due tomorrow" && DueWord(d, d) == "due today" && DueWord(d.AddDays(-1), d) == "overdue" && DueWord(d.AddDays(3), d) == "due Wednesday", "coach: due words");
        check(TitleTime("Quiz at 9pm") == new TimeSpan(21, 0, 0) && TitleTime("Essay by 11:59 PM") == new TimeSpan(23, 59, 0) && TitleTime("Quiz 13.2") == null && TitleTime("lab 12am") == TimeSpan.Zero, "coach: time in title");
        var t = new Item { Title = "Quiz", Start = d };
        check(DueTonight(t, d.AddHours(18)), "coach: due today is due tonight");
        t.Spent = 10; check(!DueTonight(t, d.AddHours(18)), "coach: started tasks do not escalate");
        t.Spent = 0; t.Start = d.AddDays(1); check(!DueTonight(t, d), "coach: due tomorrow is not tonight");
        t.Start = null; t.Title = "Call Sam tonight"; check(DueTonight(t, d), "coach: undated tonight task");
        var done = new Item { Kind = "task", Done = true, Updated = d.AddHours(10).ToUniversalTime() };
        check(DoneOn(done, d) && !DoneOn(done, d.AddDays(1)), "coach: done today");
        check(Hours(1800) == "30 min" && Hours(7200) == "2 h", "coach: hours text");
    }

    public static int Test(string[] args)
    {
        int bad = 0;
        void Check(bool ok, string what) { if (!ok) { bad++; Console.WriteLine("FAIL " + what); } }
        var today = DateTime.Today;
        TimeLog.Reset();
        var quiz = Planner.AddTask("Quiz 13.2", today, "", null, "Math");
        var read = Planner.AddTask("Read chapter 4", today, "", null, "Art");
        var essay = Planner.AddTask("Essay", today.AddDays(1), "", null, "Art");
        var lab = Planner.AddTask("Lab report", today.AddDays(5), "", null, "Science");
        Planner.Edit(l => l.Find(i => i.Id == read.Id).Spent = 20);
        Planner.SetDone(lab.Id, true);
        var block = Planner.AddEvent("Work on Essay", today.AddHours(15), today.AddHours(16), false, 10);
        Planner.Edit(l => l.Find(i => i.Id == block.Id).BlockFor = essay.Id);
        Planner.AddEvent("Class", today.AddDays(1).AddHours(9), null, false, 10);
        // time log for the week that ends on the last Sunday (or today)
        var sunday = today.AddDays(-(int)today.DayOfWeek);
        TimeLog.Add(sunday.AddHours(10), "code", quiz.Id, 3600);
        TimeLog.Add(sunday.AddHours(11), "chrome", essay.Id, 1800);
        TimeLog.Add(sunday.AddHours(12), "spotify", null, 900);
        TimeLog.Add(sunday.AddHours(13), "chrome", null, 600);

        var s = new Settings { SpeakReplies = "short", Rollover = "21:30", Escalate = true };
        var said = new List<string>();
        Say = t => said.Add(t);
        string qk = "coach-esc-" + quiz.Id;
        int Phone() => Planner.Live().Count(i => i.Kind == "event" && i.Title == "Due tonight: Quiz 13.2");

        Tick(s, today.AddHours(3)); Check(!Nudge.Has(qk) && said.Count == 0 && Phone() == 0 && !Nudge.Has("coach-roll"), "quiet hours fire nothing");
        Tick(s, today.AddHours(17.9)); Check(!Nudge.Has(qk), "nothing before 18:00");
        Tick(s, today.AddHours(18)); Check(Nudge.Has(qk), "18:00 card");
        Check(!Nudge.Has("coach-esc-" + read.Id), "started task is left alone");
        Nudge.Remove(qk); Tick(s, today.AddHours(18.5)); Check(!Nudge.Has(qk), "18:00 card shows once");
        Tick(s, today.AddHours(20)); Check(said.Count == 1 && said[0].Contains("Quiz 13.2"), "20:00 spoken");
        Tick(s, today.AddHours(20.5)); Check(said.Count == 1, "spoken once");
        Tick(s, today.AddHours(21)); Check(Phone() == 1, "21:00 phone alert event");
        Tick(s, today.AddHours(21.2)); Check(Phone() == 1, "phone alert once");
        Check(!Nudge.Has("coach-roll"), "rollover waits for its time");
        Tick(s, today.AddHours(21.5)); Check(Nudge.Has("coach-roll"), "rollover card");
        Nudge.Remove("coach-roll"); Tick(s, today.AddHours(22)); Check(!Nudge.Has("coach-roll"), "rollover once a day");
        Planner.SetDone(quiz.Id, true);
        Planner.AddTask("Quiz two", today, "", null, "Math");
        said.Clear();
        Tick(s, today.AddHours(22)); Check(said.Count == 0, "a task first seen at 22:00 jumps to the phone step, no speech");
        var s2 = new Settings { SpeakReplies = "off", Rollover = "", Escalate = true };
        Tick(s2, today.AddHours(20)); Check(said.Count == 0, "no speech when replies are off");

        string md = RollMarkdown(today.AddHours(21.5));
        Check(md.Contains("Lab report") && md.Contains("Essay") && md.Contains("Class"), "rollover text");
        Check(TryLocal("how did i do today", s).Contains("Lab report"), "how did i do today");
        Check(TryLocal("good morning", s) == null && TryLocal("what's my day", s) == null, "does not steal morning phrases");
        string mb = MorningBrief(today.AddHours(8));
        Check(mb.Contains("Quiz two") && mb.Contains("Work on Essay") && !mb.Contains("Class"), "morning brief due + blocks");

        var sun = sunday.AddHours(18);
        Tick(s2, sun); Check(Nudge.Has("coach-week"), "Sunday review card");
        Nudge.Remove("coach-week"); Tick(s2, sun.AddMinutes(30)); Check(!Nudge.Has("coach-week"), "Sunday review once");
        string wk = WeekMarkdown(sun);
        Check(wk.Contains("Math · 60 min") && wk.Contains("Art · 30 min") && wk.Contains("chrome"), "week hours by list and app");
        Check(TryLocal("weekly review", s) != null && TryLocal("how was my week", s) != null, "weekly review phrases");

        Console.WriteLine(bad == 0 ? "coachtest ok" : $"coachtest: {bad} failed");
        return bad == 0 ? 0 : 1;
    }
}
