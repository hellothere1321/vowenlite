using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VowenLite;

// schedule.json: what Schedule remembers between ticks (and between the daemon and a one-off process running a voice command).
sealed class SchedState
{
    public DateTime Rebalanced { get; set; }                              // last rebalance
    public DateTime Stamp { get; set; }                                   // planner.json write time right after it, so our own writes don't count as a change
    public DateTime PausedUntil { get; set; }                             // "clear my blocks": no new blocks before this
    public List<string> NoBlocks { get; set; } = new();                   // task ids: "don't schedule <task>"
    public Dictionary<string, DateTime> Snoozed { get; set; } = new();    // task id -> left off the Now card until (x on the card)
    public Dictionary<string, string> Short { get; set; } = new();        // task id -> day the "can't fit" nudge last showed
    public Dictionary<string, BlockSeen> Seen { get; set; } = new();      // block id -> what TimeLog had on its task when the block began
    public List<string> Checked { get; set; } = new();                    // past blocks already judged as worked
    public List<BlockMove> Moves { get; set; } = new();                   // blocks re-planned in the last few days (MovedToday)
}
sealed class BlockSeen { public int Base { get; set; } public int Minutes { get; set; } }   // Base = seconds, Minutes = ticks Focus said it was active
sealed class BlockMove { public DateTime Day { get; set; } public string Task { get; set; } public DateTime From { get; set; } public List<string> To { get; set; } = new(); }

// Roadmap 2.1-2.3: the planner schedules itself.
//   Estimates: every open task gets Item.Est (guessed from its title/kind, times Habits.EstimateFactor); "that's 2 hours" corrects it.
//   Work blocks (Settings.AutoBlocks): events with Item.BlockFor = task Id, placed in free time before the due date (inside
//                Habits.WorkHours), at most Settings.BlockMinutes each, never in quiet hours; moved when a real event lands on them or one is skipped.
//   The Now card: one row (id "now") with the single next thing, ranked by due time x Grades.Weight x work left x free time before it's due.
//                Click = Focus.Start(task); x = "later" (snooze it for an hour).
static class Schedule
{
    internal const int Gap = 10, MinBlock = 25, DayCap = 180, Horizon = 14, Lead = 15, Crumb = 10;   // minutes (Horizon: days)
    const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal readonly record struct Slot(DateTime Start, DateTime End) { public int Min => (int)Math.Round((End - Start).TotalMinutes); }

    // ================= pure scheduling math (no files, no clock): SelfChecks covers it

    internal static DateTime Ceil5(DateTime t)
    {
        var m = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Kind);
        if (m < t) m = m.AddMinutes(1);
        return m.Minute % 5 == 0 ? m : m.AddMinutes(5 - m.Minute % 5);
    }

    static bool Overlaps(Slot a, Slot b) => a.Start < b.End && b.Start < a.End;

    // Quiet hours of one calendar day ("23:00-07:00" -> 00:00-07:00 and 23:00-24:00). Same format and meaning as Nudge.Quiet.
    internal static List<Slot> QuietSpans(DateTime day, string spec)
    {
        var res = new List<Slot>();
        if (string.IsNullOrWhiteSpace(spec)) return res;
        var p = spec.Split('-', StringSplitOptions.TrimEntries);
        if (p.Length != 2 || !TimeSpan.TryParseExact(p[0], @"hh\:mm", Inv, out var a) || !TimeSpan.TryParseExact(p[1], @"hh\:mm", Inv, out var b)) return res;
        var d = day.Date;
        if (a < b) res.Add(new Slot(d + a, d + b));
        else if (a > b) { if (b > TimeSpan.Zero) res.Add(new Slot(d, d + b)); res.Add(new Slot(d + a, d.AddDays(1))); }
        return res;
    }

    internal static List<Slot> Subtract(List<Slot> from, IEnumerable<Slot> cut)
    {
        var res = from.ToList();
        foreach (var c in cut)
        {
            if (c.End <= c.Start) continue;
            var next = new List<Slot>(res.Count + 1);
            foreach (var f in res)
            {
                if (!Overlaps(f, c)) { next.Add(f); continue; }
                if (c.Start > f.Start) next.Add(new Slot(f.Start, c.Start));
                if (c.End < f.End) next.Add(new Slot(c.End, f.End));
            }
            res = next;
        }
        return res.OrderBy(x => x.Start).ToList();
    }

    // The time of one day blocks may use: each listed hour h is [h:00, h+1:00) of that calendar day, minus quiet hours.
    internal static List<Slot> Allowed(DateTime day, IEnumerable<int> hours, string quiet)
    {
        var d = day.Date;
        var res = new List<Slot>();
        foreach (int h in (hours ?? Array.Empty<int>()).Where(h => h is >= 0 and <= 23).Distinct().OrderBy(h => h))
        {
            var s = d.AddHours(h);
            if (res.Count > 0 && res[^1].End == s) res[^1] = new Slot(res[^1].Start, s.AddHours(1)); else res.Add(new Slot(s, s.AddHours(1)));
        }
        return Subtract(res, QuietSpans(d, quiet));
    }

    // Free time in [from, to): inside each day's allowed hours, minus busy intervals widened by `pad` minutes on each side. Slots never cross midnight.
    internal static List<Slot> FreeSlots(DateTime from, DateTime to, IEnumerable<Slot> busy, Func<DateTime, int[]> hours, string quiet, int pad = Gap)
    {
        var res = new List<Slot>();
        if (to <= from) return res;
        var padded = busy.Select(b => new Slot(b.Start.AddMinutes(-pad), b.End.AddMinutes(pad))).Where(b => b.End > from && b.Start < to).ToList();
        for (var d = from.Date; d < to; d = d.AddDays(1))
        {
            var day = Allowed(d, hours(d), quiet).Select(x => new Slot(x.Start < from ? from : x.Start, x.End > to ? to : x.End)).Where(x => x.End > x.Start).ToList();
            res.AddRange(Subtract(day, padded));
        }
        return res;
    }

    internal sealed class Need { public string Id; public DateTime Deadline; public int Minutes; }

    // Earliest deadline first (ties keep the caller's order): each need gets MinBlock..maxBlock-minute blocks in the earliest free time before
    // its deadline, Gap minutes apart, at most DayCap minutes of blocks per day (dayUsed = minutes already there; updated). busy grows with
    // every new block so the next need keeps clear of it. shortBy gets the minutes that didn't fit.
    internal static Dictionary<string, List<Slot>> Place(IEnumerable<Need> needs, List<Slot> busy, DateTime from, Func<DateTime, int[]> hours, string quiet,
                                                         int maxBlock, Dictionary<DateTime, int> dayUsed, Dictionary<string, int> shortBy)
    {
        maxBlock = Math.Clamp(maxBlock, MinBlock, DayCap);
        var res = new Dictionary<string, List<Slot>>();
        foreach (var n in needs.OrderBy(n => n.Deadline))
        {
            int left = n.Minutes;
            var got = new List<Slot>();
            foreach (var free in FreeSlots(from, n.Deadline, busy, hours, quiet))
            {
                var t = Ceil5(free.Start);
                while (left >= Crumb)
                {
                    // 70 min with 60-min blocks is 45 + 25, not 60 + a 25-min block for 10 minutes
                    int want = left <= maxBlock ? Math.Max(MinBlock, left) : left - maxBlock < MinBlock ? Math.Max(MinBlock, left - MinBlock) : maxBlock;
                    int room = (int)(free.End - t).TotalMinutes / 5 * 5;
                    int len = Math.Min(want, Math.Min(room, DayCap - dayUsed.GetValueOrDefault(t.Date)));
                    if (len < MinBlock) break;
                    var b = new Slot(t, t.AddMinutes(len));
                    got.Add(b);
                    left -= len;
                    dayUsed[t.Date] = dayUsed.GetValueOrDefault(t.Date) + len;
                    t = Ceil5(b.End.AddMinutes(Gap));
                }
                if (left < Crumb) break;
            }
            busy.AddRange(got);
            if (got.Count > 0) res[n.Id] = got;
            if (left >= Crumb) shortBy[n.Id] = left;
        }
        return res;
    }

    // A task's due date is a day (due by the end of it) unless it carries a time of day (some feeds give one).
    internal static DateTime? Deadline(Item t) => t?.Start is DateTime d ? (d.TimeOfDay == TimeSpan.Zero ? d.Date.AddDays(1) : d) : null;

    internal sealed class Cand { public Item Task; public DateTime? Deadline; public int Left, FreeMin; public double Weight; public DateTime? BlockEnd; }

    // Due soon, work that barely fits in the free time left before it's due, course weight, priority. A block happening now beats everything.
    internal static double Score(Cand c, DateTime now)
    {
        int prio = c.Task?.Priority ?? 0;
        if (c.BlockEnd > now) return 100 + prio;
        double due = 0.02, press = 0;
        if (c.Deadline is DateTime dl)
        {
            double days = (dl - now).TotalDays;
            due = days <= 0 ? 1.5 : 1 / (1 + days);                                   // overdue 1.5, due tonight ~0.6, tomorrow ~0.4, next week ~0.13
            if (c.Left > 0) press = Math.Min(2, c.Left / Math.Max(30.0, c.FreeMin));  // >= 1: it doesn't fit any more
        }
        return due + press + 0.5 * Math.Clamp(c.Weight, 0, 1) + 0.1 * prio;
    }

    internal static Cand Top(IEnumerable<Cand> cands, DateTime now) => cands.Where(c => c?.Task != null)
        .OrderByDescending(c => Score(c, now)).ThenBy(c => c.Deadline ?? DateTime.MaxValue).ThenBy(c => c.Task.Title, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

    // Starting estimate: the kind's default times how long tasks like it really take you, to the nearest 5 minutes.
    internal static int Guess(string title, double factor)
    {
        double m = Habits.DefaultMinutes(Habits.Kind(title)) * (factor > 0 ? factor : 1);
        return Math.Max(5, (int)Math.Round(m / 5) * 5);
    }

    internal static string Dur(int min) => min < 60 ? min + " min" : min % 60 == 0 ? min / 60 + " h" : (min / 60.0).ToString("0.#", Inv) + " h";
    internal static string DayName(DateTime d, DateTime now) => d.Date == now.Date ? "today" : d.Date == now.Date.AddDays(1) ? "tomorrow"
        : d.Date > now.Date && d.Date < now.Date.AddDays(7) ? d.ToString("ddd", Inv) : d.ToString("MMM d", Inv);
    static string Clock(DateTime t) => t.ToString(t.Minute == 0 ? "h tt" : "h:mm tt", Inv).ToLowerInvariant();
    static string DueWord(Item t, DateTime now)
    {
        var d = t.Start.Value;
        bool timed = d.TimeOfDay != TimeSpan.Zero;
        if (timed ? d <= now : d.Date < now.Date) return "overdue";
        return "due " + DayName(d, now) + (timed ? " " + Clock(d) : "");
    }

    // "2 hours", "an hour and a half", "45 min", "two hours", "another 30 minutes" -> minutes (0 = not a duration). more = on top of what's done.
    static readonly string[] Ones = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve" };
    static int Tens(string w) => w switch { "twenty" => 20, "thirty" => 30, "forty" => 40, "fifty" => 50, "sixty" => 60, "ninety" => 90, _ => 0 };
    static readonly Regex DurRx = new(@"^(?:(?<h>\d+(?:\.\d+)?|an?)(?<half> and a half)?\s*(?:hours?|hrs?|h)(?<half> and a half)?)?(?:(?:,?\s*and)?\s*(?<m>\d+)\s*(?:minutes?|mins?|m))?$", IC);
    internal static int ParseMinutes(string text) => ParseMinutes(text, out _);
    internal static int ParseMinutes(string text, out bool more)
    {
        string d = " " + (text ?? "").ToLowerInvariant().Trim().TrimEnd('.', '!', '?') + " ";
        more = Regex.IsMatch(d, @"\b(?:another|more|left|to go|extra)\b");
        d = Regex.Replace(d, @"\banother\s+(?=hours?\b)", "an ");
        d = Regex.Replace(d, @"\b(?:another|more|left|to go|extra|about|around|like|roughly|maybe|approximately|probably|only|just|at least|at most|or so|tops|max|in total|total|of work)\b", " ");
        d = Regex.Replace(d, @"\b(twenty|thirty|forty|fifty)[\s-](one|two|three|four|five|six|seven|eight|nine)\b", m => (Tens(m.Groups[1].Value) + Array.IndexOf(Ones, m.Groups[2].Value)).ToString(Inv));
        d = Regex.Replace(d, @"\b(twenty|thirty|forty|fifty|sixty|ninety)\b", m => Tens(m.Groups[1].Value).ToString(Inv));
        d = Regex.Replace(d, @"\bfifteen\b", "15");
        for (int i = Ones.Length - 1; i >= 1; i--) d = Regex.Replace(d, @"\b" + Ones[i] + @"\b", i.ToString(Inv));
        d = Regex.Replace(d, @"\ba couple(?: of)?\b", "2");
        d = Regex.Replace(d, @"\b(?:half an?|a half) hour\b|\bhalf hour\b", "30 minutes");
        d = Regex.Replace(d, @"\ba quarter (?:of an? )?hour\b", "15 minutes");
        d = Regex.Replace(d, @"\s+", " ").Trim();
        var m = DurRx.Match(d);
        if (!m.Success || (!m.Groups["h"].Success && !m.Groups["m"].Success)) return 0;
        double h = !m.Groups["h"].Success ? 0 : m.Groups["h"].Value is "a" or "an" ? 1 : double.Parse(m.Groups["h"].Value, Inv);
        if (m.Groups["half"].Success) h += 0.5;
        int mins = (int)Math.Round(h * 60) + (m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, Inv) : 0);
        return mins is >= 5 and <= 40 * 60 ? mins : 0;
    }

    // Best fuzzy title match (>= 60% of the spoken words found in it), preferring the shorter title on ties (same rule as the planner's).
    static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase) { "the", "a", "an", "my", "to", "task", "for", "and", "on", "of", "it", "that", "this", "work" };
    static List<string> Toks(string s) => Regex.Matches((s ?? "").ToLowerInvariant(), @"[a-z0-9]+").Select(x => x.Value).Where(w => !Stop.Contains(w)).ToList();
    internal static Item FindTask(IEnumerable<Item> pool, string q)
    {
        var qw = Toks(q);
        if (qw.Count == 0) return null;
        Item best = null; double bs = 0; int bl = int.MaxValue;
        foreach (var it in pool)
        {
            var tw = Toks(it.Title);
            int hit = qw.Count(w => tw.Any(t => t == w || (w.Length >= 4 && (t.StartsWith(w) || w.StartsWith(t) && t.Length >= 4))));
            double sc = (double)hit / qw.Count;
            if (sc >= 0.6 && (sc > bs || (sc == bs && tw.Count < bl))) { best = it; bs = sc; bl = tw.Count; }
        }
        return best;
    }

    // ================= state

    static string StatePath => Path.Combine(Paths.DataDir, "schedule.json");
    static readonly object gate = new();
    static SchedState st;
    static DateTime stStamp;

    static SchedState State()
    {
        lock (gate)
        {
            var stamp = File.Exists(StatePath) ? File.GetLastWriteTimeUtc(StatePath) : DateTime.MinValue;
            if (st != null && stamp == stStamp) return st;
            try { st = File.Exists(StatePath) ? JsonSerializer.Deserialize<SchedState>(File.ReadAllText(StatePath)) ?? new() : new(); } catch { st = new(); }
            stStamp = stamp;
            return st;
        }
    }

    static void SaveState()
    {
        lock (gate)
        {
            if (st == null) return;
            try
            {
                string tmp = StatePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(st));
                File.Move(tmp, StatePath, true);
                stStamp = File.GetLastWriteTimeUtc(StatePath);
            }
            catch (Exception ex) { Luna.Log("schedule: save state: " + ex.Message); }
        }
    }

    // The planner as of its last write, re-read only when planner.json changed (Tick and the card never parse it every minute).
    static List<Item> items;
    static DateTime itemsStamp;
    static bool Sync()
    {
        lock (gate)
        {
            var stamp = Planner.LastWrite;
            if (items != null && stamp == itemsStamp) return false;
            items = Planner.Load(); itemsStamp = stamp;
            return true;
        }
    }

    static IEnumerable<Item> Blocks(IEnumerable<Item> all) => all.Where(i => i.Kind == "event" && !i.Deleted && !i.AllDay && i.BlockFor != null && i.Start != null && i.End != null);
    static bool IsOpen(Item t) => t != null && t.Kind == "task" && !t.Done && !t.Deleted;
    static int Minutes(Item b) => (int)Math.Round((b.End.Value - b.Start.Value).TotalMinutes);
    static Func<DateTime, int[]> WorkHours => d => Habits.WorkHours(d.DayOfWeek);
    static List<Slot> RealBusy(List<Item> all, DateTime from, DateTime to) =>   // all-day events don't take time; blocks aren't "real"
        Planner.Occurrences(from, to, all).Where(o => !o.AllDay && o.Item.BlockFor == null).Select(o => new Slot(o.Start, o.End)).ToList();

    // Same as Planner.Remove, inside an Edit we already hold: a block that reached Google stays as a tombstone until the delete is pushed.
    static void Drop(List<Item> l, Item x) { if (x.GId == null) l.Remove(x); else { x.Deleted = true; x.Dirty = true; x.Updated = DateTime.UtcNow; } }

    // ================= estimates

    public static int EstimateFor(Item task)   // minutes of work still left
    {
        if (task == null) return 0;
        int est = task.Est > 0 ? task.Est : Guess(task.Title, Habits.EstimateFactor(task.Title));
        return Math.Max(0, est - task.Spent);
    }

    // Every open task gets a guessed estimate. Est is local-only, so Planner.Edit (not Update): Google isn't pushed for it.
    static void EnsureEstimates()
    {
        if (Luna.DryRun || !items.Any(i => IsOpen(i) && i.Est == 0)) return;
        int n = 0;
        Planner.Edit(l => { foreach (var t in l.Where(i => IsOpen(i) && i.Est == 0)) { t.Est = Guess(t.Title, Habits.EstimateFactor(t.Title)); n++; } });
        Luna.Log($"schedule: estimated {n} tasks");
        Sync();
    }

    // ================= the daemon tick

    static int ticking;

    public static void Tick(Settings s, DateTime now)
    {
        if (Interlocked.Exchange(ref ticking, 1) == 1) return;
        try
        {
            if (Sync()) EnsureEstimates();
            Watch(s, now);
            if (s.AutoBlocks && RebalanceDue(State(), now)) Rebalance(s, now, false);
            Refresh(s, now);
        }
        finally { Interlocked.Exchange(ref ticking, 0); }
    }

    // A block that is on now: remember what TimeLog had on its task when it began (and count the minutes Focus has that task active),
    // so once it's over we can tell a worked block from a skipped one. TimeLog only keeps day totals, so this is the "during it" part.
    internal static void Watch(Settings s, DateTime now)
    {
        if (!s.TrackTime) return;
        List<Item> all; lock (gate) all = items;
        if (all == null) return;
        bool added = false;
        lock (gate)
        {
            var state = State();
            foreach (var b in Blocks(all).Where(b => b.Start <= now && now < b.End))
            {
                if (!state.Seen.TryGetValue(b.Id, out var seen))
                {
                    state.Seen[b.Id] = seen = new BlockSeen { Base = TimeLog.Day(now).Tasks.GetValueOrDefault(b.BlockFor) };
                    added = true;
                }
                if (Focus.ActiveTaskId == b.BlockFor) seen.Minutes++;
            }
            if (added) SaveState();
        }
    }

    // Minutes worked on a finished block's task while it ran.
    static int Worked(Item b, SchedState state)
    {
        int day = TimeLog.Day(b.Start.Value).Tasks.GetValueOrDefault(b.BlockFor);
        if (!state.Seen.TryGetValue(b.Id, out var seen)) return day / 60;   // the daemon wasn't running when it began: the day's total is all we have
        return Math.Max(seen.Minutes, (day - seen.Base) / 60);
    }

    // At most every 15 min, and only when something changed: the planner file, a block ending (worked or skipped?), a new day, a pause running out.
    static bool RebalanceDue(SchedState state, DateTime now)
    {
        if (now >= state.Rebalanced && now - state.Rebalanced < TimeSpan.FromMinutes(15)) return false;
        if (itemsStamp != state.Stamp || now.Date != state.Rebalanced.Date) return true;
        if (state.PausedUntil > state.Rebalanced && state.PausedUntil <= now) return true;
        List<Item> all; lock (gate) all = items;
        return all != null && Blocks(all).Any(b => b.End > state.Rebalanced && b.End <= now);
    }

    internal sealed class Result { public List<Item> Added = new(); public int Removed; public List<(Item Task, int Min)> Short = new(); }

    // Removes the blocks that are no longer right and books the work still missing. Never touches events without BlockFor, and leaves
    // blocks that are fine where they are (they sync to Google Calendar and the phone). force = "plan my week": ignores a "clear my blocks" pause.
    internal static Result Rebalance(Settings s, DateTime now, bool force)
    {
        var res = new Result();
        lock (gate)
        {
            var state = State();
            var all = Planner.Load();
            var tasks = all.Where(i => i.Kind == "task" && !i.Deleted).GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
            var blocks = Blocks(all).ToList();
            var real = RealBusy(all, now.AddDays(-2), now.AddDays(Horizon + 2));
            var remove = new HashSet<string>();
            var moved = new Dictionary<string, DateTime>();   // task id -> start of the first block of it that had to go

            foreach (var b in blocks)
            {
                var bs = new Slot(b.Start.Value, b.End.Value);
                tasks.TryGetValue(b.BlockFor, out var t);
                bool open = IsOpen(t);
                if (bs.Start > now)
                {
                    if (!open || state.NoBlocks.Contains(b.BlockFor) || (Deadline(t) is DateTime dl && bs.End > dl)) remove.Add(b.Id);
                    else if (real.Any(r => Overlaps(r, bs))) { remove.Add(b.Id); moved.TryAdd(b.BlockFor, bs.Start); }
                }
                else if (bs.End <= now && open && s.TrackTime && bs.End > now.AddHours(-36) && !state.Checked.Contains(b.Id))
                {
                    if (Worked(b, state) < 5) { remove.Add(b.Id); moved.TryAdd(b.BlockFor, bs.Start); }   // skipped: its minutes go back into the plan
                    else state.Checked.Add(b.Id);
                    state.Seen.Remove(b.Id);
                }
            }

            // Est went down (or work went faster): drop whole blocks from the end while the surplus covers them.
            int Planned(string tid) => blocks.Where(b => b.BlockFor == tid && !remove.Contains(b.Id) && b.End > now)
                .Sum(b => (int)Math.Round((b.End.Value - (b.Start.Value > now ? b.Start.Value : now)).TotalMinutes))
                + (s.TrackTime ? 0 : blocks.Where(b => b.BlockFor == tid && b.End <= now).Sum(Minutes));   // no time tracking: past blocks count as done
            foreach (var g in blocks.Where(b => b.Start > now && !remove.Contains(b.Id) && IsOpen(tasks.GetValueOrDefault(b.BlockFor))).GroupBy(b => b.BlockFor))
            {
                int surplus = Planned(g.Key) - EstimateFor(tasks[g.Key]);
                foreach (var b in g.OrderByDescending(b => b.Start))
                {
                    if (surplus < Minutes(b)) break;
                    remove.Add(b.Id); surplus -= Minutes(b);
                }
            }

            var from = Ceil5(now.AddMinutes(Lead));
            var needs = new List<Need>();
            if (force || state.PausedUntil <= now)
            {
                foreach (var t in tasks.Values.Where(t => IsOpen(t) && t.Start != null && t.Start.Value.Date <= now.Date.AddDays(Horizon) && !state.NoBlocks.Contains(t.Id))
                             .OrderBy(t => Deadline(t)).ThenByDescending(t => t.Priority).ThenByDescending(t => Grades.Weight(t)))
                {
                    var dl = Deadline(t).Value;
                    int need = EstimateFor(t) - Planned(t.Id);
                    if (dl > from && need >= Crumb) needs.Add(new Need { Id = t.Id, Deadline = dl, Minutes = need });
                }
            }
            var keep = blocks.Where(b => !remove.Contains(b.Id)).ToList();
            var busy = real.Concat(keep.Select(b => new Slot(b.Start.Value, b.End.Value))).ToList();
            var dayUsed = keep.GroupBy(b => b.Start.Value.Date).ToDictionary(g => g.Key, g => g.Sum(Minutes));
            var shortBy = new Dictionary<string, int>();
            var placed = Place(needs, busy, from, WorkHours, s.QuietHours, s.BlockMinutes, dayUsed, shortBy);

            // write: one Edit for the removals, AddEvent per new block, one Edit to mark them as blocks
            if (remove.Count > 0)
            {
                Planner.Edit(l => { foreach (var x in l.Where(i => remove.Contains(i.Id) && i.BlockFor != null).ToList()) Drop(l, x); });
                GoogleSync.Kick();
            }
            var made = new Dictionary<string, string>();
            foreach (var (tid, spans) in placed)
            {
                var t = tasks[tid];
                foreach (var sp in spans)
                {
                    var e = Planner.AddEvent("Work: " + t.Title, sp.Start, sp.End, false, 5, null, t.Cat);
                    e.BlockFor = tid; made[e.Id] = tid; res.Added.Add(e);
                }
            }
            if (made.Count > 0) Planner.Edit(l => { foreach (var x in l) if (made.TryGetValue(x.Id, out var tid)) x.BlockFor = tid; });
            res.Removed = remove.Count;
            res.Short = shortBy.Select(kv => (tasks[kv.Key], kv.Value)).ToList();

            // what moved, and saying so (not in quiet hours; a "can't fit" once per task per day)
            string today = now.ToString("yyyy-MM-dd", Inv);
            bool quiet = Nudge.Quiet(s, now);
            foreach (var (tid, fromStart) in moved)
            {
                var to = placed.GetValueOrDefault(tid);
                state.Moves.Add(new BlockMove { Day = now.Date, Task = tid, From = fromStart, To = made.Where(kv => kv.Value == tid).Select(kv => kv.Key).ToList() });
                if (!quiet && to is { Count: > 0 } && !shortBy.ContainsKey(tid))
                    Nudge.Show("sched-moved:" + tid, "Moved " + tasks[tid].Title, "to " + DayName(to[0].Start, now) + " " + Clock(to[0].Start) + " · " + Dur(EstimateFor(tasks[tid])) + " left",
                               () => Planner.OpenWindow("week"), ttl: TimeSpan.FromHours(3));
            }
            foreach (var (tid, m) in shortBy)
            {
                if (quiet || state.Short.GetValueOrDefault(tid) == today) continue;
                state.Short[tid] = today;
                var t = tasks[tid];
                string when = t.Start.Value.Date == now.Date ? "today" : "before " + DayName(t.Start.Value, now);
                Nudge.Show("sched-short:" + tid, $"Can't fit {t.Title} {when}", Dur(m) + " short", () => Planner.OpenWindow("week"), ttl: TimeSpan.FromHours(6));
            }

            // forget what no longer matters
            var alive = new HashSet<string>(Blocks(all).Where(b => !remove.Contains(b.Id) && b.End > now.AddDays(-3)).Select(b => b.Id));
            state.Checked.RemoveAll(id => !alive.Contains(id));
            foreach (var id in state.Seen.Keys.Where(id => !alive.Contains(id)).ToList()) state.Seen.Remove(id);
            state.Moves.RemoveAll(m => m.Day < now.Date.AddDays(-3));
            foreach (var id in state.Snoozed.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()) state.Snoozed.Remove(id);
            foreach (var id in state.Short.Where(kv => kv.Value != today).Select(kv => kv.Key).ToList()) state.Short.Remove(id);
            state.NoBlocks.RemoveAll(id => !IsOpen(tasks.GetValueOrDefault(id)));
            state.Rebalanced = now;
            state.Stamp = Planner.LastWrite;
            SaveState();
            if (res.Added.Count > 0 || remove.Count > 0) Luna.Log($"schedule: +{res.Added.Count} -{remove.Count} blocks, {shortBy.Count} short");
        }
        Sync();
        return res;
    }

    // Removes future blocks (all of them, or one task's). Returns how many.
    static int ClearBlocks(DateTime now, string taskId)
    {
        int n = 0;
        lock (gate)
        {
            var all = Planner.Load();
            if (!Blocks(all).Any(b => b.Start > now && (taskId == null || b.BlockFor == taskId))) return 0;
            Planner.Edit(l => { foreach (var x in Blocks(l).Where(b => b.Start > now && (taskId == null || b.BlockFor == taskId)).ToList()) { Drop(l, x); n++; } });
            GoogleSync.Kick();
        }
        Sync();
        return n;
    }

    public static List<Item> MovedToday() => MovedOn(DateTime.Today);   // blocks moved today (the evening rollover reports them)

    internal static List<Item> MovedOn(DateTime day)
    {
        var ids = State().Moves.Where(m => m.Day.Date == day.Date).SelectMany(m => m.To).ToHashSet();
        return ids.Count == 0 ? new() : Planner.Live().Where(i => ids.Contains(i.Id)).OrderBy(i => i.Start).ToList();
    }

    // ================= the Now card

    sealed class Pick { public Item Task; public string Secondary, Why; }
    static volatile Pick pick;
    static volatile bool cardOn;

    static Pick Compute(List<Item> all, SchedState state, Settings s, DateTime now)
    {
        if (all == null) return null;
        var open = all.Where(i => IsOpen(i) && !(state.Snoozed.TryGetValue(i.Id, out var u) && u > now)).ToList();
        if (open.Count == 0) return null;
        var current = Blocks(all).Where(b => b.Start <= now && now < b.End).ToList();
        var real = RealBusy(all, now, now.AddDays(Horizon));
        var cands = open.Select(t =>
        {
            var dl = Deadline(t);
            var c = new Cand { Task = t, Deadline = dl, Left = EstimateFor(t), Weight = Grades.Weight(t), BlockEnd = current.FirstOrDefault(b => b.BlockFor == t.Id)?.End };
            if (dl > now) c.FreeMin = FreeSlots(now, dl.Value < now.AddDays(Horizon) ? dl.Value : now.AddDays(Horizon), real, WorkHours, s.QuietHours, 0).Sum(x => x.Min);
            return c;
        }).ToList();
        var top = Top(cands, now);
        if (top == null) return null;
        var why = new List<string>();
        string sec;
        if (top.BlockEnd > now)
        {
            sec = "block until " + top.BlockEnd.Value.ToString("h:mm", Inv);
            why.Add("its work block runs until " + Clock(top.BlockEnd.Value));
        }
        else
        {
            var parts = new List<string>();
            if (top.Task.Start != null) { parts.Add(DueWord(top.Task, now)); why.Add(parts[0] == "overdue" ? "it's overdue" : "it's " + parts[0]); }
            if (top.Left > 0) { parts.Add("~" + Dur(top.Left) + " left"); why.Add("about " + Dur(top.Left) + " of work left"); }
            if (top.Deadline > now) why.Add(Dur(top.FreeMin) + " free before then");
            sec = parts.Count > 0 ? string.Join(" · ", parts) : "no due date";
        }
        if (top.Weight > 0) why.Add((int)Math.Round(top.Weight * 100) + "% of the grade");
        if (top.Task.Priority == 3) why.Add("high priority");
        return new Pick { Task = top.Task, Secondary = sec, Why = why.Count == 0 ? "it's the only thing on your list" : string.Join(", ", why) };
    }

    // Recomputes the pick from the cached planner (re-read only if it changed) and whether the card may show right now.
    static void Refresh(Settings s, DateTime now)
    {
        Sync();
        List<Item> all; lock (gate) all = items;
        var p = Compute(all, State(), s, now);
        bool on = s.NowCard && !Nudge.Quiet(s, now);
        var old = pick;
        pick = p; cardOn = on;
        if (old?.Task.Id != p?.Task.Id || old?.Secondary != p?.Secondary) Overlay.ChipRefresh();
    }

    public static Item NowPick(DateTime now) => NowPick(now, Proactive.S());   // the task the Now card shows (null = nothing to do)

    internal static Item NowPick(DateTime now, Settings s)
    {
        Sync();
        List<Item> all; lock (gate) all = items;
        return Compute(all, State(), s, now)?.Task;
    }

    public static void Register()   // Cards.AddSource("now", ...); the card shows while Settings.NowCard and not in quiet hours
    {
        Cards.AddSource("now", Rows, Activate, Dismiss);
        Task.Run(() => Proactive.Safe("schedule first pick", () => Refresh(Proactive.S(), DateTime.Now)));
    }

    // Called about once a second by the overlay: only the cached pick, no file reads.
    static IEnumerable<Row> Rows()
    {
        var p = pick;
        if (!cardOn || p == null) yield break;
        yield return new Row(CardIcon.Play, "Now: " + p.Task.Title, p.Secondary, 0, "now", "Why: " + p.Why);
    }

    static void Activate(string id)
    {
        var p = pick;
        if (p == null) return;
        Task.Run(() => Proactive.Safe("schedule start", () =>
        {
            string r = Focus.Start(p.Task, Proactive.S());
            if (r == null) Overlay.Notice("Start: " + p.Task.Title); else if (r.Length > 0) Overlay.Notice(r);
        }));
    }

    // x = later: that task leaves the card for an hour and the next pick shows.
    static void Dismiss(string id)
    {
        var p = pick;
        if (p == null) return;
        pick = null;
        Snooze(p.Task.Id, DateTime.Now.AddHours(1));
        Task.Run(() => Proactive.Safe("schedule snooze", () => Refresh(Proactive.S(), DateTime.Now)));
    }

    internal static void Snooze(string taskId, DateTime until)
    {
        lock (gate) { State().Snoozed[taskId] = until; SaveState(); }
    }

    static Item CurrentPick(Settings s) => pick?.Task ?? NowPick(DateTime.Now, s);

    // ================= voice / typed (l arrives cleaned: lead-ins and a trailing "now" are gone)

    const string Take = @"(?:'ll|\s+will|\s+should|\s+would|\s+might|\s+is\s+going\s+to|'s\s+going\s+to|\s+is\s+gonna|'s\s+gonna)(?:\s+probably|\s+only)?\s+take";
    static readonly Regex EstThat = new(@"^(?:that|this|it)(?:'s|\s+is|" + Take + @"|\s+takes|\s+needs)(?:\s+me)?\s+(?<dur>.+)$", IC);
    static readonly Regex EstNamed = new(@"^(?:the\s+|my\s+)?(?<t>.+?)(?:" + Take + @"|\s+takes|\s+needs)(?:\s+me)?\s+(?<dur>.+)$", IC);
    static readonly Regex EstVerb = new(@"^(?:estimate|budget)\s+(?:the\s+|my\s+)?(?<t>.+?)\s+(?:at|as|to be|for)\s+(?<dur>.+)$", IC);
    internal static readonly Regex WhatNow = new(@"^(?:so\s+)?(?:what\s+(?:should|shall|do|can|could)\s+i\s+(?:do|work\s+on|start(?:\s+on)?|focus\s+on|tackle)(?:\s+(?:next|first|now|today|right\s+now))?|what(?:'s|\s+is)\s+next(?:\s+(?:to\s+do|on\s+my\s+list|for\s+me))?|what(?:'s|\s+is)\s+my\s+next\s+task|what\s+now|what(?:'s|\s+is)\s+the\s+next\s+thing\s+(?:to\s+do|i\s+should\s+do))$", IC);
    internal static readonly Regex PlanWeek = new(@"^(?:(?:plan|schedule|organi[sz]e|time\s*block|block\s+out|map\s+out|sort\s+out)(?:\s+out)?\s+(?:my\s+|the\s+)?(?:week|next\s+(?:7|seven)\s+days|coming\s+week|this\s+week|rest\s+of\s+(?:my\s+|the\s+)?week)|(?:re-?balance|re-?plan|redo|refresh)\s+(?:my\s+|the\s+)?(?:work\s+|study\s+)?blocks|re-?plan\s+my\s+week)$", IC);
    internal static readonly Regex ClearRx = new(@"^(?:clear|remove|delete|cancel|drop|get\s+rid\s+of|wipe)(?:\s+all)?(?:\s+of)?\s+(?:my\s+|the\s+)?(?:work\s+|study\s+)?blocks(?:\s+(?:this\s+week|from\s+my\s+calendar|on\s+my\s+calendar))?$", IC);
    internal static readonly Regex NoSched = new(@"^(?:don'?t|do\s+not|stop|never|no\s+more)\s+(?:schedul(?:e|ing)|block(?:ing)?\s+(?:out\s+)?time\s+for|block(?:ing)?\s+out|making\s+blocks\s+for|blocks\s+for)\s+(?:the\s+|my\s+)?(?<t>.+?)(?:\s+any\s*more)?$", IC);
    internal static readonly Regex Resume = new(@"^(?:(?:start|resume|go\s+back\s+to)\s+(?:scheduling|blocking\s+(?:out\s+)?time\s+for|planning)\s+(?:the\s+|my\s+)?(?<t>.+?)(?:\s+again)?|(?:schedule|block\s+time\s+for)\s+(?:the\s+|my\s+)?(?<t>.+?)\s+again)$", IC);

    public static string TryLocal(string l, Settings s)   // "what should I do now", "that's 2 hours", "plan my week", "clear my blocks", "don't schedule <task>"
    {
        if (string.IsNullOrWhiteSpace(l)) return null;
        string r = TryEstimate(l, s);
        if (r != null) return r;
        Match m;
        if (WhatNow.IsMatch(l))
        {
            var p = Compute(Planner.Load(), State(), s, DateTime.Now);
            if (p == null) return "✓ Nothing on your list right now";
            return $"✓ {p.Task.Title}: {p.Why}";
        }
        if (PlanWeek.IsMatch(l))
        {
            if (Luna.DryRun) return "DRY RUN: re-plan the work blocks for the next 14 days and list this week's";
            if (!s.AutoBlocks) return "✓ Work blocks are off · turn on AutoBlocks in settings first";
            lock (gate) { State().PausedUntil = default; SaveState(); }
            var now = DateTime.Now;
            return WeekSummary(Rebalance(s, now, true), now);
        }
        if (ClearRx.IsMatch(l))
        {
            if (Luna.DryRun) return "DRY RUN: remove every future work block and pause new ones until tomorrow";
            var now = DateTime.Now;
            int n = ClearBlocks(now, null);
            lock (gate) { State().PausedUntil = now.Date.AddDays(1); SaveState(); }
            return n == 0 ? "✓ No work blocks to clear" : $"✓ Cleared {n} work block{(n == 1 ? "" : "s")} · no new ones until tomorrow (say “plan my week” to bring them back)";
        }
        if ((m = NoSched.Match(l)).Success)
        {
            var t = FindTask(Planner.OpenTasks(), m.Groups["t"].Value);
            if (t == null) return null;
            if (Luna.DryRun) return "DRY RUN: no more work blocks for " + t.Title;
            lock (gate) { var state = State(); if (!state.NoBlocks.Contains(t.Id)) state.NoBlocks.Add(t.Id); SaveState(); }
            int n = ClearBlocks(DateTime.Now, t.Id);
            return "✓ No work blocks for " + t.Title + (n > 0 ? $" · removed {n}" : "");
        }
        if ((m = Resume.Match(l)).Success)
        {
            var t = FindTask(Planner.OpenTasks(), m.Groups["t"].Value);
            if (t == null || !State().NoBlocks.Contains(t.Id)) return null;
            if (Luna.DryRun) return "DRY RUN: schedule work blocks for " + t.Title + " again";
            lock (gate) { State().NoBlocks.Remove(t.Id); SaveState(); }
            int added = s.AutoBlocks ? Rebalance(s, DateTime.Now, false).Added.Count(b => b.BlockFor == t.Id) : 0;
            return "✓ Work blocks for " + t.Title + " are back" + (added > 0 ? $" · {added} added" : "");
        }
        return null;
    }

    // "that's 2 hours" (the Now pick), "the essay will take 3 hours", "estimate the lab at 90 minutes", "that'll take another hour" (on top of what's done).
    static string TryEstimate(string l, Settings s)
    {
        Item t; Match m;
        if ((m = EstThat.Match(l)).Success)
        {
            if (ParseMinutes(m.Groups["dur"].Value) == 0) return null;
            t = CurrentPick(s);
            if (t == null) return "✓ Which task? Say “the essay will take 2 hours”";
        }
        else if ((m = EstVerb.Match(l)).Success || (m = EstNamed.Match(l)).Success)
        {
            if (ParseMinutes(m.Groups["dur"].Value) == 0) return null;
            t = FindTask(Planner.OpenTasks(), m.Groups["t"].Value);
            if (t == null) return null;
        }
        else return null;
        int min = ParseMinutes(m.Groups["dur"].Value, out bool more);
        int est = more ? t.Spent + min : min;
        if (Luna.DryRun) return $"DRY RUN: estimate {t.Title} at {Dur(est)}";
        Planner.Edit(list => { var x = list.Find(i => i.Id == t.Id); if (x != null) x.Est = est; });
        Proactive.Safe("schedule refresh", () => Refresh(s, DateTime.Now));
        return $"✓ {t.Title} · about {Dur(est)}" + (more && t.Spent > 0 ? $" in all ({Dur(min)} to go)" : "");
    }

    static string WeekSummary(Result r, DateTime now)
    {
        var all = Planner.Load();
        var titles = all.Where(i => i.Kind == "task").GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First().Title);
        var week = Blocks(all).Where(b => b.End > now && b.Start < now.Date.AddDays(7)).OrderBy(b => b.Start).ToList();
        if (week.Count == 0 && r.Short.Count == 0) return "✓ Nothing to block out this week";
        var sb = new StringBuilder("## Work blocks this week\n\n");
        foreach (var g in week.GroupBy(b => b.Start.Value.Date))
        {
            string day = When.Cap(DayName(g.Key, now));
            sb.Append("- **").Append(day).Append("** (").Append(Dur(g.Sum(Minutes))).Append("): ")
              .Append(string.Join(" · ", g.Select(b => Clock(b.Start.Value) + "–" + Clock(b.End.Value) + " " + titles.GetValueOrDefault(b.BlockFor, b.Title)))).Append('\n');
        }
        if (r.Short.Count > 0)
            sb.Append("\n**Can't fit:** ").Append(string.Join(", ", r.Short.Select(x => $"{x.Task.Title} ({Dur(x.Min)} short before {DayName(x.Task.Start.Value, now)})"))).Append('\n');
        sb.Append($"\n{r.Added.Count} added, {r.Removed} removed or moved. Blocks move by themselves when a real event lands on one or one is skipped.");
        return sb.ToString();
    }

    // ================= tests

    public static void SelfChecks(Action<bool, string> check)
    {
        var d = new DateTime(2026, 11, 23);   // a Monday
        Func<DateTime, int[]> eve = _ => new[] { 16, 17, 18, 19, 20, 21, 22 };
        Slot S(double h0, double h1, int day = 0) => new(d.AddDays(day).AddHours(h0), d.AddDays(day).AddHours(h1));

        foreach (var spec in new[] { "00:30-08:00", "23:00-07:00", "22:00-00:00", "", "junk", "12:00-12:00" })
        {
            var q = QuietSpans(d, spec);
            bool ok = true;
            for (int m = 0; m < 24 * 60; m += 5) { var t = d.AddMinutes(m); if (q.Any(x => x.Start <= t && t < x.End) != Nudge.InWindow(spec, t)) ok = false; }
            check(ok, "schedule: quiet spans agree with quiet hours (" + spec + ")");
        }
        check(Ceil5(d.AddHours(9).AddMinutes(2).AddSeconds(5)) == d.AddHours(9).AddMinutes(5) && Ceil5(d.AddHours(9)) == d.AddHours(9), "schedule: times round up to 5 minutes");

        var free = FreeSlots(d.AddHours(9), d.AddDays(1), new[] { S(17, 18) }, eve, "00:30-08:00");
        check(free.Count == 2 && free[0] == new Slot(d.AddHours(16), d.AddHours(16).AddMinutes(50)) && free[1] == new Slot(d.AddHours(18).AddMinutes(10), d.AddHours(23)), "schedule: free time skips an event plus a 10-minute gap");
        check(FreeSlots(d.AddHours(9), d.AddDays(1), new[] { S(15, 16.5), S(16.25, 23.5) }, eve, "").Count == 0, "schedule: overlapping events cover the evening");
        check(FreeSlots(d.AddHours(19.5), d.AddDays(1), new Slot[0], eve, "")[0].Start == d.AddHours(19.5), "schedule: free time starts no earlier than now");

        Func<DateTime, int[]> late = _ => new[] { 21, 22, 23, 0, 1 };
        var night = FreeSlots(d, d.AddDays(2), new Slot[0], late, "23:30-00:45");
        bool clear = night.All(x => { for (var t = x.Start; t < x.End; t = t.AddMinutes(5)) if (Nudge.InWindow("23:30-00:45", t)) return false; return true; });
        check(clear && night.Contains(S(0.75, 2)) && night.Contains(S(21, 23.5)) && night.Contains(S(0.75, 2, 1)) && night.Contains(S(21, 23.5, 1)),
              "schedule: quiet hours across midnight cut late work hours");

        var sh = new Dictionary<string, int>();
        var got = Place(new[] { new Need { Id = "q", Deadline = d.AddDays(1), Minutes = 45 } }, new List<Slot>(), d.AddHours(14), eve, "", 60, new(), sh);
        check(got.TryGetValue("q", out var q1) && q1.Count == 1 && q1[0] == S(16, 16.75) && sh.Count == 0, "schedule: due today gets one block this evening");
        sh.Clear();
        got = Place(new[] { new Need { Id = "q", Deadline = d.AddDays(1), Minutes = 45 } }, new List<Slot> { S(15, 23.5) }, d.AddHours(14), eve, "", 60, new(), sh);
        check(!got.ContainsKey("q") && sh.GetValueOrDefault("q") == 45, "schedule: no room means short by all of it");
        sh.Clear();
        got = Place(new[] { new Need { Id = "q", Deadline = d.AddHours(23), Minutes = 45 } }, new List<Slot>(), d.AddHours(22).AddMinutes(40), eve, "", 60, new(), sh);
        check(!got.ContainsKey("q") && sh["q"] == 45, "schedule: under 25 minutes of room is no block");
        sh.Clear();
        got = Place(new[] { new Need { Id = "e", Deadline = d.AddDays(3), Minutes = 150 } }, new List<Slot>(), d.AddHours(9), eve, "", 60, new(), sh);
        var e1 = got.GetValueOrDefault("e") ?? new();
        check(e1.Select(x => x.Min).SequenceEqual(new[] { 60, 60, 30 }) && e1.Zip(e1.Skip(1)).All(p => (p.Second.Start - p.First.End).TotalMinutes >= Gap) && e1.All(x => x.Start.Date == d),
              "schedule: 150 min becomes 60 + 60 + 30 with 10-minute gaps");
        got = Place(new[] { new Need { Id = "e", Deadline = d.AddDays(3), Minutes = 70 } }, new List<Slot>(), d.AddHours(9), eve, "", 60, new(), sh);
        check(got["e"].Select(x => x.Min).SequenceEqual(new[] { 45, 25 }), "schedule: 70 min splits 45 + 25");
        sh.Clear();
        got = Place(new[] { new Need { Id = "big", Deadline = d.AddDays(2), Minutes = 400 } }, new List<Slot>(), d.AddHours(9), eve, "", 60, new(), sh);
        var byDay = got["big"].GroupBy(x => x.Start.Date).ToDictionary(g => g.Key, g => g.Sum(x => x.Min));
        check(byDay.Count == 2 && byDay.Values.All(v => v == DayCap) && sh["big"] == 40, "schedule: at most 3 h of blocks a day, the rest is short");
        sh.Clear();
        got = Place(new[] { new Need { Id = "q", Deadline = d.AddDays(3), Minutes = 45 } }, new List<Slot>(), d.AddHours(9), eve, "", 60, new() { [d] = 170 }, sh);
        check(got["q"].Single().Start.Date == d.AddDays(1), "schedule: a full day pushes the block to the next");
        sh.Clear();
        got = Place(new[] { new Need { Id = "late", Deadline = d.AddDays(4), Minutes = 120 }, new Need { Id = "soon", Deadline = d.AddDays(1), Minutes = 60 } },
                    new List<Slot> { S(18, 19) }, d.AddHours(9), eve, "", 60, new(), sh);
        check(got["soon"][0] == S(16, 17) && got["late"].All(x => !Overlaps(x, S(18, 19)) && x.Start >= d.AddHours(17).AddMinutes(Gap)), "schedule: earliest deadline first, others keep clear of it");
        var dueAt = new Item { Kind = "task", Start = d.AddHours(14) };
        check(Deadline(new Item { Kind = "task", Start = d }) == d.AddDays(1) && Deadline(dueAt) == d.AddHours(14) && Deadline(new Item()) == null, "schedule: deadline is the end of the due day, or its time");

        var now = d.AddHours(9);
        Item T(string title, int prio = 0) => new() { Kind = "task", Title = title, Priority = prio };
        var today = new Cand { Task = T("Quiz"), Deadline = d.AddDays(1), Left = 45, FreeMin = 300 };
        var week = new Cand { Task = T("Essay"), Deadline = d.AddDays(7), Left = 180, FreeMin = 2400 };
        var undated = new Cand { Task = T("Someday"), Left = 30 };
        check(Top(new[] { week, undated, today }, now) == today, "rank: due today beats next week and undated");
        check(Top(new[] { today, new Cand { Task = T("Old"), Deadline = d, Left = 30 } }, now).Task.Title == "Old", "rank: overdue first");
        var inBlock = new Cand { Task = T("Lab"), Deadline = d.AddDays(5), Left = 60, FreeMin = 1000, BlockEnd = now.AddMinutes(30) };
        check(Top(new[] { today, inBlock }, now) == inBlock, "rank: a block happening now wins");
        check(Top(new[] { today, new Cand { Task = T("Lab"), Deadline = d.AddDays(5), BlockEnd = now } }, now) == today, "rank: a block that just ended doesn't count");
        var tight = new Cand { Task = T("Project"), Deadline = d.AddDays(3), Left = 600, FreeMin = 400 };
        var easy = new Cand { Task = T("Reading"), Deadline = d.AddDays(2), Left = 30, FreeMin = 800 };
        check(Top(new[] { easy, tight }, now) == tight, "rank: work that no longer fits beats a small task due sooner");
        var art = new Cand { Task = T("Art quiz"), Deadline = d.AddDays(2), Left = 45, FreeMin = 600, Weight = 0.3 };
        var bio = new Cand { Task = T("Bio quiz"), Deadline = d.AddDays(2), Left = 45, FreeMin = 600 };
        check(Top(new[] { bio, art }, now) == art, "rank: course weight breaks a tie");
        check(Top(new[] { bio, new Cand { Task = T("Chem quiz", 3), Deadline = d.AddDays(2), Left = 45, FreeMin = 600 } }, now).Task.Title == "Chem quiz", "rank: priority breaks a tie");
        check(Top(new Cand[0], now) == null, "rank: nothing to do");

        check(ParseMinutes("2 hours") == 120 && ParseMinutes("an hour and a half") == 90 && ParseMinutes("half an hour") == 30 && ParseMinutes("1.5 hours") == 90
              && ParseMinutes("45 min") == 45 && ParseMinutes("2 hours and 30 minutes") == 150 && ParseMinutes("two hours") == 120 && ParseMinutes("about 90 minutes") == 90
              && ParseMinutes("forty five minutes") == 45 && ParseMinutes("one and a half hours") == 90 && ParseMinutes("an hour") == 60 && ParseMinutes("3h") == 180,
              "schedule: spoken durations");
        check(ParseMinutes("fine") == 0 && ParseMinutes("5 o'clock") == 0 && ParseMinutes("") == 0 && ParseMinutes("2 days") == 0 && ParseMinutes("minutes") == 0, "schedule: not durations");
        check(ParseMinutes("another hour", out bool more) == 60 && more && ParseMinutes("30 more minutes", out more) == 30 && more && ParseMinutes("2 hours", out more) == 120 && !more, "schedule: 'another hour' adds to the work done");
        check(Guess("Essay 2 draft", 1.0) == 180 && Guess("Quiz 13.2", 1.7) == 75 && Guess("Call Sam", 1.0) == 30, "schedule: estimate from the kind of task");
        check(EstimateFor(new Item { Est = 60, Spent = 20 }) == 40 && EstimateFor(new Item { Est = 30, Spent = 50 }) == 0, "schedule: work left is the estimate minus time spent");
        check(Dur(45) == "45 min" && Dur(120) == "2 h" && Dur(90) == "1.5 h", "schedule: durations read naturally");
        check(DayName(d.AddDays(4), d.AddHours(9)) == "Fri" && DayName(d, d.AddHours(9)) == "today" && DayName(d.AddDays(1), d) == "tomorrow", "schedule: day names");
        var pool = new[] { new Item { Title = "Essay 2: final draft" }, new Item { Title = "Read chapter 4" } };
        check(FindTask(pool, "essay")?.Title == "Essay 2: final draft" && FindTask(pool, "chapter 4 reading")?.Title == "Read chapter 4" && FindTask(pool, "dentist") == null, "schedule: fuzzy task names");

        check(WhatNow.IsMatch("what should I do") && WhatNow.IsMatch("what should I work on next") && WhatNow.IsMatch("what's next") && !WhatNow.IsMatch("what should I do about Sam"), "schedule: what-now phrases");
        check(PlanWeek.IsMatch("plan my week") && PlanWeek.IsMatch("replan my blocks") && !PlanWeek.IsMatch("plan my day"), "schedule: plan-my-week phrases");
        check(ClearRx.IsMatch("clear my blocks") && ClearRx.IsMatch("delete all my work blocks") && !ClearRx.IsMatch("clear my calendar"), "schedule: clear-blocks phrases");
        check(NoSched.Match("don't schedule the essay").Groups["t"].Value == "essay" && !NoSched.IsMatch("start a pomodoro"), "schedule: don't-schedule phrases");
        check(EstNamed.Match("the essay will take 3 hours") is { Success: true } en && en.Groups["t"].Value == "essay" && en.Groups["dur"].Value == "3 hours"
              && EstThat.IsMatch("that'll take 30 minutes") && EstThat.IsMatch("that's 2 hours") && !EstThat.IsMatch("remind me in 10 minutes"), "schedule: estimate phrases");
    }

    // --schedtest: a scratch planner with tasks and events -> blocks, a new event moves one, a skipped one is re-planned, a done task loses its blocks.
    public static int Test(string[] args)
    {
        int fails = 0;
        void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL " + what); } }
        bool Mine(Item i) => i.Title.StartsWith("Schedtest", StringComparison.OrdinalIgnoreCase) || i.Title.StartsWith("Work: Schedtest", StringComparison.OrdinalIgnoreCase);
        int others = Planner.Live().Count(i => !Mine(i));
        if (others > 0) { Console.WriteLine($"schedtest needs an empty scratch planner (VOWENLITE_DATA has {others} other items)"); return 2; }
        Planner.Edit(l => l.RemoveAll(Mine));
        try { File.Delete(StatePath); } catch { }
        lock (gate) { st = null; items = null; }
        TimeLog.Reset();
        var s = new Settings { QuietHours = "00:30-08:00", AutoBlocks = true, BlockMinutes = 60, NowCard = true, TrackTime = true, SpeakReplies = "off" };

        // a simulated day (tomorrow, so every block is also in the real future): class 4-5:30 pm, a quiz due that day, an essay due 2 days later
        var D = DateTime.Today.AddDays(1);
        var t0 = D.AddHours(9);
        var essay = Planner.AddTask("Schedtest essay", D.AddDays(2));
        var quiz = Planner.AddTask("Schedtest quiz 3", D);
        var read = Planner.AddTask("Schedtest read chapter 4", null);
        Planner.AddEvent("Schedtest class", D.AddHours(16), D.AddHours(17.5), false);
        Planner.AddEvent("Schedtest trip", D.AddDays(1), null, true);
        Item Get(Item t) => Planner.Load().First(i => i.Id == t.Id);
        List<Item> BlocksOf(Item t) => Blocks(Planner.Load()).Where(b => b.BlockFor == t.Id).OrderBy(b => b.Start).ToList();
        List<Slot> RealNow() => RealBusy(Planner.Load(), D.AddDays(-1), D.AddDays(5));
        bool Clean(List<Item> bl) => bl.All(b => !RealNow().Any(r => Overlaps(r, new Slot(b.Start.Value, b.End.Value))));

        Sync(); EnsureEstimates();
        Check(Get(essay).Est == 180 && Get(quiz).Est == 45 && Get(read).Est == 45, $"estimates from the kind of task (essay {Get(essay).Est}, quiz {Get(quiz).Est}, reading {Get(read).Est})");
        var r = Rebalance(s, t0, true);
        var qb = BlocksOf(quiz); var eb = BlocksOf(essay);
        Check(qb.Sum(Minutes) >= 45 && qb.All(b => b.Start.Value.Date == D && b.End <= D.AddDays(1)), "quiz due that day: blocks that day (" + qb.Sum(Minutes) + " min)");
        Check(eb.Sum(Minutes) >= 180 && eb.All(b => b.End <= Deadline(Get(essay))), "essay: all 3 h booked before it's due (" + eb.Sum(Minutes) + " min)");
        Check(Clean(qb.Concat(eb).ToList()), "no block on top of the class");
        Check(qb.Concat(eb).All(b => b.Start >= t0.AddMinutes(Lead) && b.Start.Value.Hour >= 16 && b.End.Value <= b.Start.Value.Date.AddHours(23)), "blocks inside work hours");
        Check(eb.Any(b => b.Start.Value.Date == D.AddDays(1)), "an all-day event doesn't block time");
        Check(qb.Concat(eb).GroupBy(b => b.Start.Value.Date).All(g => g.Sum(Minutes) <= DayCap), "at most 3 h of blocks a day");
        var allB = qb.Concat(eb).OrderBy(b => b.Start).ToList();
        Check(allB.Zip(allB.Skip(1)).All(p => (p.Second.Start.Value - p.First.End.Value).TotalMinutes >= Gap), "10-minute gaps between blocks");
        Check(eb.All(b => b.Title == "Work: Schedtest essay" && b.Alert == 5 && b.Cat == essay.Cat), "block title, alert and list");
        Check(BlocksOf(read).Count == 0, "no blocks for a task without a due date");
        if (qb.Count == 0 || eb.Count < 3) { Console.WriteLine($"FAIL no blocks to work with (quiz {qb.Count}, essay {eb.Count})"); return 1; }
        Check(qb[0].Start < eb[0].Start, "earliest deadline gets the earliest time");
        r = Rebalance(s, t0.AddMinutes(1), true);
        Check(r.Added.Count == 0 && r.Removed == 0, $"a second pass changes nothing (+{r.Added.Count} -{r.Removed})");

        Check(NowPick(t0, s)?.Id == quiz.Id, "Now: the quiz due today first (got " + NowPick(t0, s)?.Title + ")");
        var during = eb[0].Start.Value.AddMinutes(5);
        Check(NowPick(during, s)?.Id == essay.Id, "Now: the task whose block is on");
        Snooze(quiz.Id, t0.AddHours(1));
        Check(NowPick(t0, s)?.Id != quiz.Id && NowPick(t0, s) != null, "Now: a snoozed task steps aside");
        lock (gate) { State().Snoozed.Clear(); SaveState(); }

        // a smaller estimate drops the last block
        string said = TryLocal("schedtest essay will take 2 hours", s);
        Check(said != null && said.StartsWith("✓") && Get(essay).Est == 120, "\"the essay will take 2 hours\" sets the estimate (" + said + ")");
        var lastEssay = BlocksOf(essay).Last().Id;
        r = Rebalance(s, t0.AddMinutes(2), true);
        Check(BlocksOf(essay).Sum(Minutes) == 120 && BlocksOf(essay).All(b => b.Id != lastEssay) && r.Added.Count == 0, "a smaller estimate drops the last block, nothing else");

        // a real event lands on a block: that one moves, the others stay put
        var first = BlocksOf(essay)[0]; var second = BlocksOf(essay)[1];
        Planner.AddEvent("Schedtest dinner", first.Start.Value.AddMinutes(-5), first.Start.Value.AddMinutes(55), false);
        r = Rebalance(s, t0.AddMinutes(3), true);
        eb = BlocksOf(essay);
        Check(eb.All(b => b.Id != first.Id) && eb.Any(b => b.Id == second.Id) && eb.Sum(Minutes) == 120 && Clean(eb), "a new event moves only the block under it");
        Check(MovedOn(D).Any(b => b.BlockFor == essay.Id), "the move is recorded for the evening rollover");
        Check(BlocksOf(quiz).Select(b => b.Id).SequenceEqual(qb.Select(b => b.Id)), "other tasks' blocks don't churn");

        // a block that went by unworked is re-planned
        var q0 = BlocksOf(quiz)[0];
        r = Rebalance(s, q0.End.Value.AddMinutes(5), true);
        var qb2 = BlocksOf(quiz);
        Check(qb2.All(b => b.Id != q0.Id) && qb2.Sum(Minutes) >= 45 && qb2.All(b => b.Start > q0.End), "a skipped block is removed and re-planned later (" + string.Join(", ", qb2.Select(b => b.Start.Value.ToString("HH:mm"))) + ")");

        // a block that was worked stays
        var w = BlocksOf(essay).First(b => b.Start > q0.End);
        Sync(); Watch(s, w.Start.Value.AddMinutes(1));
        TimeLog.Add(w.Start.Value.AddMinutes(10), "code", essay.Id, 20 * 60);
        Planner.Edit(l => l.Find(i => i.Id == essay.Id).Spent = 20);
        r = Rebalance(s, w.End.Value.AddMinutes(2), true);
        Check(BlocksOf(essay).Any(b => b.Id == w.Id), "a worked block stays on the calendar");
        Check(BlocksOf(essay).Where(b => b.Start > w.End).Sum(Minutes) >= 100, "the rest of the essay is still booked");

        // done: its future blocks go, the past ones stay as a record
        var at = w.End.Value.AddMinutes(3);
        Planner.SetDone(essay.Id, true);
        Rebalance(s, at, true);
        Check(BlocksOf(essay).All(b => b.Start <= at) && BlocksOf(essay).Any(b => b.Id == w.Id), "a done task loses its future blocks");

        // voice
        Refresh(s, at);
        said = TryLocal("that's 30 minutes", s);
        Check(Get(quiz).Est == 30, "\"that's 30 minutes\" sets the Now pick's estimate (" + said + ")");
        said = TryLocal("clear my work blocks", s);
        Check(said != null && said.StartsWith("✓ Cleared") && !Blocks(Planner.Load()).Any(b => b.Start > DateTime.Now), "\"clear my blocks\" (" + said + ")");
        Check(Rebalance(s, DateTime.Now, false).Added.Count == 0, "no new blocks while cleared");
        said = TryLocal("plan my week", s);
        Check(said != null && said.Contains("Work blocks") && BlocksOf(quiz).Any(b => b.Start > DateTime.Now), "\"plan my week\" books them again");
        said = TryLocal("don't schedule schedtest quiz", s);
        Rebalance(s, DateTime.Now, true);
        Check(said != null && said.StartsWith("✓") && !BlocksOf(quiz).Any(b => b.Start > DateTime.Now), "\"don't schedule the quiz\" (" + said + ")");
        said = TryLocal("what should I do", s);
        Check(said != null && said.StartsWith("✓ Schedtest"), "\"what should I do now\" (" + said + ")");
        Check(TryLocal("start a pomodoro", s) == null && TryLocal("add task email Sam friday", s) == null && TryLocal("remind me in 10 minutes", s) == null, "other commands are left alone");

        Planner.Edit(l => l.RemoveAll(Mine));
        try { File.Delete(StatePath); } catch { }
        TimeLog.Reset();
        Console.WriteLine(fails == 0 ? "schedtest ok" : fails + " failed");
        return fails == 0 ? 0 : 1;
    }
}
