using System.Text.Json;
using System.Text.RegularExpressions;

namespace VowenLite;

// Roadmap 4.2-4.3: learns from TimeLog and finished tasks (habits.json).
//   Estimate vs actual per kind of task (essay, quiz, reading, lab, code...): EstimateFactor ("essays take you 1.7x your guess").
//   Real working hours: which hours of the week you actually work, so Schedule puts blocks there.
static class Habits
{
    sealed class KindStat { public double F { get; set; } = 1; public int N { get; set; } }
    sealed class Data
    {
        public string Day { get; set; } = "";
        public Dictionary<string, KindStat> Kinds { get; set; } = new();
        public Dictionary<string, int[]> Hours { get; set; } = new();   // weekday number -> hours, best first (absent = too little data)
    }
    static Data data;
    static readonly object gate = new();
    static string FilePath => Path.Combine(Paths.DataDir, "habits.json");

    static Data Get()
    {
        lock (gate)
        {
            if (data != null) return data;
            try { data = File.Exists(FilePath) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new() : new(); } catch { data = new(); }
            return data;
        }
    }

    // ---------------- pure helpers (SelfChecks)

    internal static double Median(List<double> v)
    {
        if (v.Count == 0) return 1;
        var s = v.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    // Actual / estimated ratios of finished tasks -> one factor: needs 3+ samples, clamped 0.5..3.
    internal static double FactorFrom(List<double> ratios) => ratios.Count < 3 ? 1 : Math.Clamp(Median(ratios), 0.5, 3);

    // Average minutes of activity per hour of the day -> the busiest hours (>= 10 min), best first, at most 8.
    internal static int[] RankHours(double[] avgMinutes) =>
        Enumerable.Range(0, 24).Where(h => avgMinutes[h] >= 10).OrderByDescending(h => avgMinutes[h]).ThenBy(h => h).Take(8).ToArray();

    static string HoursText(int[] hs)
    {
        var sorted = hs.OrderBy(h => h).ToList();
        var parts = new List<string>();
        for (int i = 0; i < sorted.Count;)
        {
            int j = i; while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(Clock(sorted[i]) + "-" + Clock(sorted[j] + 1));
            i = j + 1;
        }
        return string.Join(", ", parts);
    }
    static string Clock(int h) { h %= 24; return (h % 12 == 0 ? 12 : h % 12) + (h < 12 ? "am" : "pm"); }

    // ---------------- daily recompute

    public static void Tick(Settings s, DateTime now)
    {
        string today = TimeLog.DayKey(now);
        lock (gate) { if (Get().Day == today) return; }
        try { Recompute(now); } catch (Exception ex) { Luna.Log("habits: " + ex.Message); }
    }

    internal static void Recompute(DateTime now)
    {
        var d = new Data { Day = TimeLog.DayKey(now) };
        var since = now.AddDays(-90);
        var ratios = new Dictionary<string, List<double>>();
        foreach (var t in Planner.Live().Where(i => i.Kind == "task" && i.Done && i.Est > 0 && i.Spent > 0))
        {
            var when = t.LastDone ?? t.Updated.ToLocalTime();
            if (when < since) continue;
            string k = Kind(t.Title);
            if (!ratios.TryGetValue(k, out var l)) ratios[k] = l = new();
            l.Add((double)t.Spent / t.Est);
        }
        foreach (var kv in ratios) d.Kinds[kv.Key] = new KindStat { F = Math.Round(FactorFrom(kv.Value), 2), N = kv.Value.Count };

        var all = TimeLog.Load();
        var sum = new Dictionary<DayOfWeek, double[]>(); var count = new Dictionary<DayOfWeek, int>(); int withData = 0;
        for (int i = 1; i <= 28; i++)
        {
            var day = now.Date.AddDays(-i);
            count[day.DayOfWeek] = count.GetValueOrDefault(day.DayOfWeek) + 1;
            if (!all.TryGetValue(TimeLog.DayKey(day), out var dl) || dl.Hours.Sum() == 0) continue;
            withData++;
            if (!sum.TryGetValue(day.DayOfWeek, out var a)) sum[day.DayOfWeek] = a = new double[24];
            for (int h = 0; h < 24; h++) a[h] += dl.Hours[h] / 60.0;
        }
        if (withData >= 7)
            foreach (var kv in sum)
            {
                var r = RankHours(kv.Value.Select(x => x / count[kv.Key]).ToArray());
                if (r.Length > 0) d.Hours[((int)kv.Key).ToString()] = r;
            }
        lock (gate) data = d;
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(d)); } catch { }
    }

    internal static void Reset() { lock (gate) data = null; }

    // ---------------- queries

    public static double EstimateFactor(string title)   // actual / estimated for tasks like this one (1 = no data)
    {
        var d = Get();
        return d.Kinds.TryGetValue(Kind(title), out var k) && k.N >= 3 ? k.F : 1.0;
    }

    public static string TryLocal(string l, Settings s)
    {
        Match m;
        if ((m = Regex.Match(l, @"^how long (?:do|does|did|will) (?:my |the )?(.+?) (?:usually |normally |generally )?take(?: me)?$", RegexOptions.IgnoreCase)).Success)
        {
            string kind = KindOfPlural(m.Groups[1].Value);
            if (kind == null) return null;
            if (Luna.DryRun) return Luna.Dry("look up how long " + kind + " tasks take you");
            var d = Get();
            string label = kind == "quiz" ? "Quizzes" : char.ToUpper(kind[0]) + kind.Substring(1) + (kind == "homework" ? "" : "s");
            if (!d.Kinds.TryGetValue(kind, out var k) || k.N < 3)
                return $"I don't have enough finished {kind} tasks with time tracked yet ({d.Kinds.GetValueOrDefault(kind)?.N ?? 0} so far, I need 3).";
            return $"{label} take you about {k.F:0.#}x your estimate ({k.N} tasks).";
        }
        if (Regex.IsMatch(l, @"^when do i (?:usually |normally |typically )?(?:work|study|get work done)$", RegexOptions.IgnoreCase))
        {
            if (Luna.DryRun) return Luna.Dry("show your usual working hours");
            var d = Get();
            if (d.Hours.Count == 0) return "I haven't tracked enough days yet to say (I need about a week of use).";
            var lines = new List<string>();
            foreach (var wd in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
                if (d.Hours.TryGetValue(((int)wd).ToString(), out var hs)) lines.Add($"- {wd}: {HoursText(hs)}");
            return "You usually work:\n\n" + string.Join("\n", lines);
        }
        return null;
    }

    // "essays" / "quizzes" / "lab reports" -> kind (null when it isn't a kind of task)
    static string KindOfPlural(string phrase)
    {
        string p = phrase.Trim().ToLowerInvariant();
        foreach (var c in new[] { p, Regex.Replace(p, @"zes$", "z"), Regex.Replace(p, @"(?<=\w{3})s$", ""), Regex.Replace(p, @"ies$", "y") })
        {
            string k = Kind(c);
            if (k != "other") return k;
        }
        return null;
    }


    // The kind of a task from its title: quiz | exam | essay | reading | lab | homework | project | code | discussion | other.
    // Shared by Schedule (estimates) and Habits (estimate vs actual per kind): keep the two in step by changing only this.
    public static string Kind(string title)
    {
        string t = (title ?? "").ToLowerInvariant();
        bool Has(params string[] w) => w.Any(x => System.Text.RegularExpressions.Regex.IsMatch(t, @"\b" + x + @"\b"));
        if (Has("quiz", "quizzes")) return "quiz";
        if (Has("exam", "midterm", "final", "test")) return "exam";
        if (Has("essay", "paper", "report", "write-?up", "thesis", "draft")) return "essay";
        if (Has("read", "reading", "readings", "chapter", "ch\\.?", "textbook")) return "reading";
        if (Has("lab", "labs")) return "lab";
        if (Has("project", "presentation", "slides")) return "project";
        if (Has("code", "coding", "bug", "fix", "implement", "build", "repo", "commit", "pr")) return "code";
        if (Has("discussion", "post", "forum", "reply", "respond")) return "discussion";
        if (Has("homework", "hw", "assignment", "worksheet", "problem set", "pset")) return "homework";
        return "other";
    }

    // Starting estimate in minutes for a kind (before Habits has data on you).
    public static int DefaultMinutes(string kind) => kind switch
    {
        "quiz" => 45, "exam" => 180, "essay" => 180, "reading" => 45, "lab" => 120, "homework" => 60,
        "project" => 240, "code" => 90, "discussion" => 30, _ => 30,
    };
    static readonly int[] DefaultHours = { 16, 17, 18, 19, 20, 21, 22 };
    public static int[] WorkHours(DayOfWeek day)   // preferred hours for work blocks, best first
    {
        var d = Get();
        return d.Hours.TryGetValue(((int)day).ToString(), out var h) && h.Length > 0 ? h : DefaultHours;
    }

    public static void SelfChecks(Action<bool, string> check)
    {
        check(Median(new() { 3, 1, 2 }) == 2 && Median(new() { 1, 2, 3, 10 }) == 2.5, "habits: median");
        check(FactorFrom(new() { 2, 2 }) == 1, "habits: fewer than 3 samples = no factor");
        check(FactorFrom(new() { 1.5, 1.7, 1.9 }) == 1.7, "habits: factor is the median");
        check(FactorFrom(new() { 9, 8, 7 }) == 3 && FactorFrom(new() { 0.1, 0.2, 0.3 }) == 0.5, "habits: factor clamped 0.5..3");
        var avg = new double[24]; avg[9] = 5; avg[20] = 40; avg[19] = 25; avg[21] = 25; avg[3] = 12;
        var r = RankHours(avg);
        check(r.SequenceEqual(new[] { 20, 19, 21, 3 }), "habits: hours ranked, quiet hours dropped");
        var all = Enumerable.Repeat(30.0, 24).ToArray();
        check(RankHours(all).Length == 8, "habits: at most 8 hours");
        check(KindOfPlural("essays") == "essay" && KindOfPlural("quizzes") == "quiz" && KindOfPlural("weekends") == null, "habits: plural kinds");
        check(HoursText(new[] { 19, 17, 18, 21 }) == "5pm-8pm, 9pm-10pm", "habits: hours text");
    }

    public static int Test(string[] args)
    {
        int bad = 0;
        void C(bool ok, string what) { if (!ok) { bad++; Console.WriteLine("FAIL " + what); } }
        Reset(); TimeLog.Reset();
        var now = DateTime.Now;
        // 6 finished essays (spent ~1.7x of an estimate of 100) and 2 finished quizzes (too few)
        foreach (double r in new[] { 1.5, 1.7, 1.9, 1.6, 1.8 })
        {
            var t = Planner.AddTask("Essay " + r, now.Date);
            Planner.Edit(l => { var x = l.Find(i => i.Id == t.Id); x.Done = true; x.LastDone = now.AddDays(-5); x.Est = 100; x.Spent = (int)(100 * r); });
        }
        for (int i = 0; i < 2; i++)
        {
            var t = Planner.AddTask("Quiz " + i, now.Date);
            Planner.Edit(l => { var x = l.Find(i2 => i2.Id == t.Id); x.Done = true; x.LastDone = now.AddDays(-5); x.Est = 40; x.Spent = 40; });
        }
        // 20 days of activity, evenings 19-21 busy
        for (int i = 1; i <= 20; i++)
        {
            var h = TimeLog.Day(now.Date.AddDays(-i)).Hours;
            h[19] = 2400; h[20] = 3000; h[21] = 1800; h[9] = 120;
        }
        Recompute(now);
        Reset();
        Console.WriteLine(TryLocal("how long do essays take me", null));
        Console.WriteLine(TryLocal("when do I usually work", null));
        C(Math.Abs(EstimateFactor("Essay on Rome") - 1.7) < 0.01, "essay factor 1.7");
        C(EstimateFactor("Quiz 3") == 1, "quiz: too few samples");
        C(TryLocal("how long do essays take me", null).Contains("1.7x") && TryLocal("how long do essays take me", null).Contains("(5 tasks)"), "essay answer");
        var wh = WorkHours(DayOfWeek.Tuesday);
        C(wh.Length == 3 && wh[0] == 20 && wh.Contains(19) && wh.Contains(21), "work hours from the log");
        TimeLog.Reset();
        foreach (var k in TimeLog.Load().Keys.ToList()) TimeLog.Load().Remove(k);
        Recompute(now); Reset();
        C(WorkHours(DayOfWeek.Monday).SequenceEqual(DefaultHours), "too little data falls back to the default");
        if (bad == 0) Console.WriteLine("habittest ok");
        return bad == 0 ? 0 : 1;
    }
}

