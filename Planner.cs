using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VowenLite;

// One task or calendar event. Lives in planner.json and mirrors a Google Task / Google Calendar event (GId) once synced.
sealed class Item
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "task";       // "task" | "event"
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime? Start { get; set; }             // task: due date (date only); event: start (date only when AllDay)
    public DateTime? End { get; set; }               // event end (exclusive when AllDay)
    public bool AllDay { get; set; }
    public bool Done { get; set; }
    public int Alert { get; set; } = 10;             // events: minutes before the start the PC rings (0 = at the start)
    public bool Fired { get; set; }                  // PC alert already rang (one-off events)
    public DateTime? FiredFor { get; set; }          // PC alert already rang for the occurrence starting at this time (repeating events)
    public string Repeat { get; set; }               // RRULE text ("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO") or null
    public List<DateTime> Skip { get; set; } = new();// repeating events: occurrences that were deleted / moved (EXDATE)
    public string Cat { get; set; } = "";            // the planner list this belongs to ("School"); "" = the default list
    public int Priority { get; set; }                // 0 none, 1 low, 2 medium, 3 high
    public DateTime? LastDone { get; set; }          // repeating tasks: when it was last ticked off
    public bool Deleted { get; set; }                // tombstone until the delete reaches Google
    public bool Dirty { get; set; }                  // local change not pushed yet
    public string GId { get; set; }
    public string GList { get; set; }                // Google Tasks list id this task lives in
    public DateTime Updated { get; set; } = DateTime.UtcNow;   // last change (remote items: Google's timestamp)
}

// One occurrence of an event (a repeating event has many).
sealed class Occ
{
    public Item Item;
    public DateTime Start, End;
    public bool AllDay, Series;
    public string Title => Item.Title;
}

// What a typed / spoken phrase turned into ("dentist every other friday at 3pm in my health list").
sealed class Parsed
{
    public string Kind = "task";                     // task | event
    public string Title = "", Cat = "", Notes = "";
    public DateTime? Start, End;
    public bool AllDay, HasTime, Reminder, Relative;
    public Rule Repeat;
    public int Prio, Alert = -1;                     // Alert -1 = default (10 min; 0 for reminders)

    public string Describe()
    {
        var bits = new List<string>();
        if (Start != null)
        {
            string day = Planner.DayWord(Start.Value);
            if (Kind == "event" && !AllDay) bits.Add(day + " " + Start.Value.ToString("h:mm tt") + (End != null && !Reminder ? "–" + End.Value.ToString("h:mm tt") : ""));
            else bits.Add(day);
        }
        if (Repeat != null) bits.Add(Repeat.Describe(Start ?? DateTime.Today));
        if (Cat.Length > 0) bits.Add(Cat);
        if (Prio > 0) bits.Add(new[] { "", "low", "medium", "high" }[Prio] + " priority");
        return string.Join(" · ", bits);
    }
}

// Tasks + calendar, shared by the daemon (voice, alerts, sync) and the Plan window through planner.json.
// Every write is load -> change -> atomic save under a named mutex, so both processes can edit safely.
static partial class Planner
{
    static string FilePath => Path.Combine(Paths.DataDir, "planner.json");
    static readonly Mutex mx = new(false, "Local\\VowenLite.Planner");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static List<Item> Load()
    {
        for (int i = 0; i < 4; i++)
        {
            try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Item>>(File.ReadAllText(FilePath)) ?? new() : new(); }
            catch (JsonException) { try { File.Copy(FilePath, FilePath + ".bad", true); } catch { } return new(); }
            catch (IOException) { Thread.Sleep(40); }
        }
        return new();
    }

    public static void Edit(Action<List<Item>> change)
    {
        bool got = false;
        try { got = mx.WaitOne(5000); } catch (AbandonedMutexException) { got = true; }
        try
        {
            var list = Load();
            change(list);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, Opts));
            File.Move(tmp, FilePath, true);
        }
        finally { if (got) try { mx.ReleaseMutex(); } catch { } }
    }

    public static DateTime LastWrite => File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;

    // ---------------- queries (always from disk: the other process may have changed things)

    public static List<Item> Live() => Load().Where(i => !i.Deleted).ToList();
    public static List<Item> OpenTasks() => Live().Where(i => i.Kind == "task" && !i.Done)
        .OrderBy(i => i.Start ?? DateTime.MaxValue).ThenByDescending(i => i.Priority).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
    public static List<Item> RecentDone(int n = 5) => Live().Where(i => i.Kind == "task" && i.Done).OrderByDescending(i => i.Updated).Take(n).ToList();

    // Every occurrence that overlaps [from, to): one-off events once, repeating events once per occurrence.
    public static List<Occ> Occurrences(DateTime from, DateTime to, List<Item> all = null)
    {
        var res = new List<Occ>();
        foreach (var i in (all ?? Load()).Where(i => i.Kind == "event" && !i.Deleted && i.Start != null))
        {
            var s0 = i.AllDay ? i.Start.Value.Date : i.Start.Value;
            var e0 = i.End ?? (i.AllDay ? s0.AddDays(1) : s0.AddHours(1));
            var dur = e0 - s0;
            if (dur <= TimeSpan.Zero) dur = i.AllDay ? TimeSpan.FromDays(1) : TimeSpan.FromMinutes(15);
            var rule = Rule.Parse(i.Repeat);
            if (rule == null)
            {
                if (s0 < to && s0 + dur > from) res.Add(new Occ { Item = i, Start = s0, End = s0 + dur, AllDay = i.AllDay });
                continue;
            }
            foreach (var st in rule.Expand(s0, to))
            {
                if (st + dur <= from || i.Skip.Contains(st)) continue;
                res.Add(new Occ { Item = i, Start = st, End = st + dur, AllDay = i.AllDay, Series = true });
            }
        }
        return res.OrderBy(o => o.Start).ThenBy(o => o.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Events that start inside [from, to). All-day events count on their date.
    public static List<Occ> Events(DateTime from, DateTime to) => Occurrences(from, to)
        .Where(o => o.AllDay ? o.Start.Date >= from.Date && o.Start.Date < to : o.Start >= from && o.Start < to).ToList();

    // ---------------- changes

    static void Touched(Item i) { i.Dirty = true; i.Updated = DateTime.UtcNow; }

    public static Item AddTask(string title, DateTime? due, string notes = "", Rule repeat = null, string cat = "", int prio = 0)
    {
        var it = new Item { Kind = "task", Title = When.Cap(title.Trim()), Start = due?.Date, Notes = notes ?? "", Repeat = due == null ? null : repeat?.ToRrule(), Cat = cat ?? "", Priority = prio, Dirty = true };
        Edit(l => l.Add(it));
        GoogleSync.Kick();
        return it;
    }

    public static Item AddEvent(string title, DateTime start, DateTime? end, bool allDay, int alert = 10, Rule repeat = null, string cat = "", int prio = 0, string notes = "")
    {
        var it = new Item { Kind = "event", Title = When.Cap(title.Trim()), Start = allDay ? start.Date : start, AllDay = allDay, Alert = alert, Dirty = true, Cat = cat ?? "", Priority = prio, Notes = notes ?? "" };
        it.End = allDay ? (end ?? start).Date.AddDays(1) : (end ?? start.AddHours(1));
        if (allDay && it.End <= it.Start) it.End = it.Start.Value.AddDays(1);
        it.Repeat = repeat?.ToRrule(allDay);
        Edit(l => l.Add(it));
        GoogleSync.Kick();
        return it;
    }

    public static Item Add(Parsed p)
    {
        if (p.Kind == "event" && p.Start != null)
            return AddEvent(p.Title.Length > 0 ? p.Title : "Event", p.Start.Value, p.End, p.AllDay, p.Alert >= 0 ? p.Alert : p.Reminder ? 0 : 10, p.Repeat, p.Cat, p.Prio, p.Notes);
        return AddTask(p.Title.Length > 0 ? p.Title : "Task", p.Start, p.Notes, p.Repeat, p.Cat, p.Prio);
    }

    // Changes one item under the lock (marks it for the next Google push).
    public static Item Update(string id, Action<Item> change)
    {
        Item res = null;
        Edit(l => { var x = l.Find(i => i.Id == id); if (x == null) return; change(x); Touched(x); res = x; });
        GoogleSync.Kick();
        return res;
    }

    // The next due date of a repeating task after it was ticked off; null when the series is over.
    static DateTime? NextDue(Item x, Rule rule)
    {
        var after = x.Start != null && x.Start.Value.Date > DateTime.Today ? x.Start.Value.Date : DateTime.Today;
        foreach (var d in rule.Expand(x.Start ?? DateTime.Today, after.AddYears(8)))
            if (d.Date > after) return d.Date;
        return null;
    }

    public static void SetDone(string id, bool done)
    {
        Edit(l =>
        {
            var x = l.Find(i => i.Id == id);
            if (x == null || x.Done == done) return;
            var rule = x.Kind == "task" && done ? Rule.Parse(x.Repeat) : null;
            if (rule != null && x.Start != null && rule.Count != 1)
            {
                var next = NextDue(x, rule);
                if (next != null)
                {
                    if (rule.Count > 1) { rule.Count--; x.Repeat = rule.ToRrule(); }
                    x.Start = next; x.LastDone = DateTime.Now; Touched(x);   // stays open: it comes round again
                    return;
                }
            }
            x.Done = done; x.LastDone = done ? DateTime.Now : null; Touched(x);
        });
        GoogleSync.Kick();
    }

    public static void Remove(string id)
    {
        Edit(l =>
        {
            var x = l.Find(i => i.Id == id);
            if (x == null) return;
            if (x.GId == null) l.Remove(x); else { x.Deleted = true; Touched(x); }
        });
        GoogleSync.Kick();
    }

    // Deletes just one occurrence of a repeating event.
    public static void SkipOccurrence(string id, DateTime occStart)
    {
        Update(id, x => { if (!x.Skip.Contains(occStart)) x.Skip.Add(occStart); });
    }

    public static int ClearDone()
    {
        int n = 0;
        Edit(l =>
        {
            foreach (var x in l.Where(i => i.Kind == "task" && i.Done && !i.Deleted).ToList())
            {
                n++;
                if (x.GId == null) l.Remove(x); else { x.Deleted = true; Touched(x); }
            }
        });
        if (n > 0) GoogleSync.Kick();
        return n;
    }

    // ---------------- alerts (daemon): ring when an event / timed reminder is about to start

    static System.Threading.Timer alertTick;

    public static void Start()
    {
        alertTick ??= new System.Threading.Timer(_ => CheckAlerts(), null, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(30));
        GoogleSync.Start();
    }

    static void CheckAlerts()
    {
        try
        {
            var now = DateTime.Now;
            var due = Occurrences(now.AddMinutes(-35), now.AddDays(1)).Where(o => !o.AllDay && !o.Item.Done
                && o.Start - TimeSpan.FromMinutes(o.Item.Alert) <= now && o.Item.FiredFor != o.Start && !(o.Item.Fired && !o.Series)).ToList();
            if (due.Count == 0) return;
            var ring = new List<Occ>();
            Edit(l =>
            {
                foreach (var d in due)
                {
                    var x = l.Find(i => i.Id == d.Item.Id);
                    if (x == null || x.FiredFor == d.Start || (x.Fired && x.Repeat == null)) continue;
                    x.Fired = x.Repeat == null; x.FiredFor = d.Start;
                    if (d.Start > now.AddMinutes(-30)) ring.Add(d);   // don't ring for things that ended long ago (PC was off)
                }
            });
            foreach (var x in ring)
            {
                var span = x.Start - now;
                string when = span.TotalMinutes > 1 ? $"in {(int)Math.Round(span.TotalMinutes)} min · {x.Start:h:mm tt}" : x.Start.ToString("h:mm tt");
                Cards.StartRinging(x.Item.Alert == 0 ? "Reminder" : "Event", x.Title + " · " + when);
            }
        }
        catch { }
    }

    // ---------------- formatting

    public static string DayWord(DateTime d)
    {
        var t = DateTime.Today;
        return d.Date == t ? "today" : d.Date == t.AddDays(1) ? "tomorrow" : d.Date == t.AddDays(-1) ? "yesterday"
            : d.Date < t.AddDays(7) && d.Date > t ? d.ToString("dddd") : d.ToString("MMM d");
    }

    public static string DueText(Item i)
    {
        if (i.Start == null) return "";
        if (i.Kind == "task") return i.Start.Value.Date < DateTime.Today && !i.Done ? "overdue · " + DayWord(i.Start.Value) : DayWord(i.Start.Value);
        return i.AllDay ? DayWord(i.Start.Value) + " · all day" : DayWord(i.Start.Value) + " " + i.Start.Value.ToString("h:mm tt");
    }

    static string RepeatNote(Item i) => Rule.Parse(i.Repeat) is { } r ? " · " + r.Describe(i.Start ?? DateTime.Today).ToLowerInvariant() : "";
    static string TaskLine(Item i) => "• " + i.Title + (i.Start != null ? " · " + DueText(i) : "") + RepeatNote(i) + (i.Priority == 3 ? " · high priority" : "");
    static string EventLine(Occ o, bool withDay) => "• " + (o.AllDay ? "all day" : o.Start.ToString("h:mm tt")) + " · " + o.Title + (withDay ? " · " + DayWord(o.Start) : "") + (o.Series ? " · " + Rule.Parse(o.Item.Repeat)?.Describe(o.Item.Start ?? o.Start).ToLowerInvariant() : "");

    // Lines for the morning briefing.
    public static string Briefing()
    {
        var sb = new System.Text.StringBuilder();
        var today = Events(DateTime.Today, DateTime.Today.AddDays(1)).Where(e => e.AllDay || e.Start > DateTime.Now.AddHours(-1)).ToList();
        if (today.Count > 0) { sb.Append("\nToday on your calendar\n"); foreach (var e in today.Take(6)) sb.Append(EventLine(e, false)).Append('\n'); }
        var tasks = OpenTasks();
        var dueNow = tasks.Where(t => t.Start != null && t.Start.Value.Date <= DateTime.Today).ToList();
        if (dueNow.Count > 0) { sb.Append("\nTasks due\n"); foreach (var t in dueNow.Take(6)) sb.Append(TaskLine(t)).Append('\n'); }
        int other = tasks.Count - dueNow.Count;
        if (other > 0) sb.Append($"\n{other} other open task{(other == 1 ? "" : "s")}\n");
        return sb.ToString();
    }

    // One day, in words: the calendar then what is due.
    public static string DayPlan(DateTime day)
    {
        var evs = Events(day, day.AddDays(1));
        var tasks = OpenTasks().Where(t => t.Start != null && (day.Date == DateTime.Today ? t.Start.Value.Date <= day.Date : t.Start.Value.Date == day.Date)).ToList();
        string name = DayWord(day);
        if (evs.Count == 0 && tasks.Count == 0) return $"✓ Nothing planned {name}";
        var sb = new System.Text.StringBuilder(char.ToUpperInvariant(name[0]) + name.Substring(1));
        if (evs.Count > 0) { sb.Append("\n"); foreach (var e in evs.Take(10)) sb.Append(EventLine(e, false)).Append('\n'); }
        if (tasks.Count > 0) { sb.Append(evs.Count > 0 ? "\nTasks due\n" : "\nTasks due\n"); foreach (var t in tasks.Take(10)) sb.Append(TaskLine(t)).Append('\n'); }
        return sb.ToString().TrimEnd();
    }

    // ---------------- parsing ("dentist every other friday at 3pm for an hour in my health list !high")

    static readonly HashSet<string> NotLists = new(StringComparer.OrdinalIgnoreCase) { "do", "todo", "to do", "to-do", "task", "tasks", "my", "the", "reminder", "reminders", "calendar" };
    static readonly Regex StrongEvent = new(@"\b(meeting|appointment|class|lecture|exam|interview|flight|party|concert|dentist|doctor|lunch|dinner|breakfast|practice|rehearsal|standup|stand-up|session|shift|call with)\b", IC);

    static string Cut(string s, Match m) => s.Remove(m.Index, m.Length).Insert(m.Index, " ");
    public static string Catify(string c)
    {
        c = Regex.Replace((c ?? "").Trim().Trim('#'), @"[\s_-]+", " ").Trim();
        return c.Length == 0 ? "" : string.Join(" ", c.Split(' ').Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));
    }

    // kind: "task" | "event" | "reminder" | null (guess: a time of day means an event)
    public static Parsed Parse(string text, DateTime now, string kind = null)
    {
        var p = new Parsed();
        string s = " " + (text ?? "").Trim() + " ";
        Match m;

        if ((m = Regex.Match(s, @"(?<=\s)#(?<c>[\w-]+)", IC)).Success) { p.Cat = Catify(m.Groups["c"].Value); s = Cut(s, m); }
        else if ((m = Regex.Match(s, @"\b(?:in|to|on|onto|into|under)\s+(?:my\s+|the\s+)?(?<c>[a-z][\w-]*(?:\s+[a-z][\w-]*)?)\s+list\b", IC)).Success)
        {
            string c = m.Groups["c"].Value.Trim();
            if (!NotLists.Contains(c) && !NotLists.Contains(c.Split(' ').Last())) p.Cat = Catify(c);
            s = Cut(s, m);
        }

        if ((m = Regex.Match(s, @"(?<=\s)(?<b>!{1,3})(?=\s)")).Success) { p.Prio = m.Groups["b"].Length; s = Cut(s, m); }
        else if ((m = Regex.Match(s, @"\b(?:(?<h>high|urgent|important|top)|(?<m>medium|normal)|(?<l>low))\s+priority\b|\bpriority\s+(?:(?<h>high)|(?<m>medium)|(?<l>low))\b", IC)).Success)
        { p.Prio = m.Groups["h"].Success ? 3 : m.Groups["m"].Success ? 2 : 1; s = Cut(s, m); }
        else if ((m = Regex.Match(s, @"\b(?:this is |it'?s )?(?:very )?(?:urgent|important)\b", IC)).Success && !Regex.IsMatch(s, @"\b(?:an?|the)\s+(?:urgent|important)\b", IC)) { p.Prio = 3; s = Cut(s, m); }

        if ((m = Regex.Match(s, @"\b(?:alert|remind(?: me)?|notify(?: me)?|warn me)\s+(?:me\s+)?(?<n>\d+|an?|one|five|ten|fifteen|thirty)\s*(?<u>min(?:ute)?s?|hours?|hrs?)\s+(?:before|ahead|early)\b", IC)).Success)
        {
            string n = m.Groups["n"].Value.ToLowerInvariant();
            int v = int.TryParse(n, out int nv) ? nv : n switch { "five" => 5, "ten" => 10, "fifteen" => 15, "thirty" => 30, _ => 1 };
            p.Alert = m.Groups["u"].Value.StartsWith("h", StringComparison.OrdinalIgnoreCase) ? v * 60 : v;
            s = Cut(s, m);
        }

        var rule = Rule.FromText(s, now, out s);
        var w = When.Parse(s, now, wantDuration: true);
        string title = Regex.Replace(w.Rest.Length > 0 ? w.Rest : Regex.Replace(s, @"\s+", " ").Trim(), @"^(?:to|about|that)\s+", "", IC).Trim();
        p.Title = When.Cap(title);

        DateTime? at = w.At;
        bool hasTime = w.HasTime;
        if (rule != null)
        {
            at = rule.FirstOn(at ?? now.Date);
            rule.ResolveSpan(at.Value.Date);
        }

        bool ev = kind switch
        {
            "event" or "reminder" => true,
            "task" => false,
            _ => hasTime || (at != null && StrongEvent.IsMatch(p.Title))
        };
        if (kind == "reminder") { p.Reminder = true; p.Alert = 0; if (!hasTime && at != null) { at = at.Value.Date.AddHours(9); hasTime = true; } }
        p.HasTime = hasTime; p.Relative = w.Relative;
        p.Repeat = rule;
        if (ev)
        {
            p.Kind = "event";
            p.Start = at;
            if (at != null)
            {
                p.AllDay = !hasTime;
                p.End = p.AllDay ? null : at.Value + (p.Reminder ? TimeSpan.FromMinutes(15) : w.Duration ?? TimeSpan.FromHours(1));
            }
        }
        else
        {
            p.Kind = "task";
            p.Start = at?.Date;
            if (hasTime && at != null && !w.Relative) p.Title += " (" + at.Value.ToString("h:mm tt") + ")";   // tasks are date-only (Google Tasks); keep the time in the title
        }
        return p;
    }

    // ---------------- the UI / Luna hand the planner a snapshot

    static readonly string[] Palette = { "#a9d8ff", "#8fe0cc", "#f3c98b", "#c2d68a", "#ffb4a8", "#c9b8ff", "#f0a6d6", "#9fb4ff" };
    static string ColorFile => Path.Combine(Paths.DataDir, "planner-colors.json");
    static Dictionary<string, int> colors;

    // Each list keeps its colour for good: a new list takes the first colour nobody uses yet (the default list is ice blue).
    public static string CatColor(string cat)
    {
        if (string.IsNullOrEmpty(cat)) return Palette[0];
        string key = cat.Trim().ToLowerInvariant();
        lock (Palette)
        {
            colors ??= ReadColors();
            if (!colors.TryGetValue(key, out int idx))
            {
                colors = ReadColors();
                if (!colors.TryGetValue(key, out idx))
                {
                    var used = colors.Values.ToHashSet();
                    idx = Enumerable.Range(1, Palette.Length - 1).FirstOrDefault(i => !used.Contains(i));
                    if (idx == 0) { int h = 17; foreach (char ch in key) h = h * 31 + ch; idx = 1 + (h & 0x7fffffff) % (Palette.Length - 1); }
                    colors[key] = idx;
                    try { File.WriteAllText(ColorFile, JsonSerializer.Serialize(colors)); } catch { }
                }
            }
            return Palette[Math.Clamp(idx, 0, Palette.Length - 1)];
        }
    }
    static Dictionary<string, int> ReadColors()
    {
        try { return File.Exists(ColorFile) ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(ColorFile)) ?? new() : new(); } catch { return new(); }
    }

    public static List<string> Cats()
    {
        var names = Live().Select(i => i.Cat ?? "").Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        return names;
    }

    static string Iso(DateTime? d) => d?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    // Everything the Plan window shows, for the visible date range.
    public static JsonObject Snapshot(DateTime from, DateTime to)
    {
        var all = Load().Where(i => !i.Deleted).ToList();
        var o = new JsonObject { ["type"] = "state", ["now"] = Iso(DateTime.Now), ["from"] = Iso(from), ["to"] = Iso(to) };
        var cats = new JsonArray();
        foreach (var c in all.Select(i => i.Cat ?? "").Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            cats.Add(new JsonObject { ["name"] = c, ["color"] = CatColor(c), ["open"] = all.Count(i => i.Kind == "task" && !i.Done && string.Equals(i.Cat ?? "", c, StringComparison.OrdinalIgnoreCase)) });
        o["cats"] = cats;

        var tasks = new JsonArray();
        var openTasks = all.Where(i => i.Kind == "task" && !i.Done).OrderBy(i => i.Start ?? DateTime.MaxValue).ThenByDescending(i => i.Priority).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase);
        var doneTasks = all.Where(i => i.Kind == "task" && i.Done).OrderByDescending(i => i.Updated).Take(40);
        foreach (var t in openTasks.Concat(doneTasks))
        {
            var rule = Rule.Parse(t.Repeat);
            tasks.Add(new JsonObject
            {
                ["id"] = t.Id, ["title"] = t.Title, ["notes"] = t.Notes ?? "", ["due"] = t.Start?.ToString("yyyy-MM-dd"), ["done"] = t.Done,
                ["cat"] = t.Cat ?? "", ["color"] = CatColor(t.Cat), ["prio"] = t.Priority, ["rrule"] = t.Repeat, ["repeat"] = rule?.Describe(t.Start ?? DateTime.Today), ["synced"] = t.GId != null && !t.Dirty
            });
        }
        o["tasks"] = tasks;

        var events = new JsonArray();
        foreach (var oc in Occurrences(from, to, all))
        {
            var rule = oc.Series ? Rule.Parse(oc.Item.Repeat) : null;
            events.Add(new JsonObject
            {
                ["id"] = oc.Item.Id, ["title"] = oc.Title, ["notes"] = oc.Item.Notes ?? "", ["start"] = Iso(oc.Start), ["end"] = Iso(oc.End), ["allDay"] = oc.AllDay,
                ["cat"] = oc.Item.Cat ?? "", ["color"] = CatColor(oc.Item.Cat), ["prio"] = oc.Item.Priority, ["alert"] = oc.Item.Alert, ["series"] = oc.Series,
                ["rrule"] = oc.Item.Repeat, ["repeat"] = rule?.Describe(oc.Item.Start ?? oc.Start), ["seriesStart"] = Iso(oc.Item.Start), ["seriesEnd"] = Iso(oc.Item.End), ["synced"] = oc.Item.GId != null && !oc.Item.Dirty
            });
        }
        o["events"] = events;
        return o;
    }

    // Plain-words planner state for Luna's prompt (only added when the request is about plans, tasks or the calendar).
    static readonly Regex PlanWords = new(@"\b(planner|plan|plans|schedule|scheduled|calendar|agenda|tasks?|to-?dos?|events?|meetings?|appointments?|due|deadline|homework|assignments?|busy|free|available|weekly|biweekly|daily|monthly|recurring|repeat\w*|every|reschedule|postpone|move|push|cancel|remind\w*)\b", IC);
    public static string PromptBlock(string request)
    {
        if (!PlanWords.IsMatch(request ?? "")) return "";
        try
        {
            var evs = Events(DateTime.Today, DateTime.Today.AddDays(14)).Take(30).ToList();
            var tasks = OpenTasks().Take(30).ToList();
            if (evs.Count == 0 && tasks.Count == 0) return "";
            var sb = new System.Text.StringBuilder("The user's planner (tasks + calendar; change it with PLAN: hook lines). Next 14 days:\n");
            foreach (var e in evs)
                sb.Append("- event ").Append(e.Start.ToString("ddd MMM d")).Append(e.AllDay ? " all day " : " " + e.Start.ToString("h:mm tt") + "-" + e.End.ToString("h:mm tt") + " ").Append(e.Title)
                  .Append(e.Series ? " (" + Rule.Parse(e.Item.Repeat)?.Describe(e.Item.Start ?? e.Start).ToLowerInvariant() + ")" : "").Append(e.Item.Cat.Length > 0 ? " [" + e.Item.Cat + "]" : "").Append('\n');
            foreach (var t in tasks)
                sb.Append("- task ").Append(t.Title).Append(t.Start != null ? " due " + t.Start.Value.ToString("ddd MMM d") : "").Append(t.Cat.Length > 0 ? " [" + t.Cat + "]" : "")
                  .Append(t.Priority == 3 ? " (high priority)" : "").Append(RepeatNote(t)).Append('\n');
            return sb.Append('\n').ToString();
        }
        catch { return ""; }
    }
}
