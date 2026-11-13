using System.Diagnostics;
using System.Text.RegularExpressions;

namespace VowenLite;

// Voice side of the planner: free local commands (Luna, the hold-to-talk keys, and the bare word "planner"),
// the PLAN: hook Codex can use, and opening the Plan window. Everything here runs with no model.
static partial class Planner
{
    static bool Is(string l, string pattern, out Match m) { m = Regex.Match(l, pattern, IC); return m.Success; }

    static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
        { "the", "a", "an", "my", "to", "task", "tasks", "event", "meeting", "appointment", "of", "for", "and", "on", "as", "done", "complete", "completed", "finished", "off", "with", "that", "it" };
    static List<string> Toks(string s) => Regex.Matches(s.ToLowerInvariant(), @"[a-z0-9]+").Select(x => x.Value).Where(w => !Stop.Contains(w)).ToList();

    // Best fuzzy title match (>= 60% of the spoken words found), preferring the shorter title on ties.
    static Item Best(IEnumerable<Item> pool, string q)
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

    static IEnumerable<Item> Plannable() => Live().Where(i => i.Kind == "event" || !i.Done);

    // ---------------- the Plan window

    public static string ViewFor(string w)
    {
        w = (w ?? "").ToLowerInvariant().Trim();
        if (w.Contains("week")) return "week";
        if (w.Contains("month") || w == "calendar") return "month";
        if (w.StartsWith("day")) return "day";
        if (w.Contains("agenda") || w.Contains("upcoming")) return "upcoming";
        if (w.Contains("task") || w.Contains("do")) return "tasks";
        if (w == "schedule") return "week";
        return "today";
    }

    public static string OpenWindow(string view = "today")
    {
        if (Luna.DryRun) return "DRY RUN: open the Plan window (" + view + ")";
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath, "--plan " + view) { UseShellExecute = false }); return ""; }
        catch (Exception ex) { return "I couldn't open it: " + ex.Message; }
    }

    // ---------------- the bare word "planner" (no Luna): "Planner, add dentist friday at 3", "Planner" alone opens the window

    static readonly Regex DirectRx = new(@"^\W*(?:(?:hey|hi|hello|ok|okay|yo)\W+)*(?:(?:the|my)\s+)?(?<w>[A-Za-z]+)\b(?<p>\s*[,:.\-–—!?]*\s*)(?<rest>.*)$", IC | RegexOptions.Singleline);
    // Whisper hears "planner" many ways. Strict = near-certain mis-hearings; the rest are matched by edit distance
    // but only when a planner command word follows, so "Plant, water the..." / "Planet, ..." are still typed.
    static readonly HashSet<string> PlannerStrict = new(StringComparer.OrdinalIgnoreCase)
        { "planners", "planer", "planter", "planar", "plana", "planor", "plannar", "planna", "planners", "plainer", "blanner", "blaner", "plannerr" };
    static readonly HashSet<string> NotPlanner = new(StringComparer.OrdinalIgnoreCase)
        { "plane", "planet", "plant", "plans", "plank", "plain", "plaid", "blame", "blank", "blind", "bland", "blend", "black", "plan", "planes", "planets", "plants", "player", "players", "blender", "blanket", "planted", "planned", "planning", "planner's" };
    // PW = what a spoken "planner" may look like inside a regex (for "open planner").
    const string PW = @"planner|planners|planer|planter|planar|plana|planor|plannar|plain her|plan her|plan er|blanner";
    static bool FuzzyPlanner(string w)
    {
        w = w.ToLowerInvariant();
        return w.Length is >= 5 and <= 9 && (w.StartsWith("pl") || w.StartsWith("bl")) && !NotPlanner.Contains(w) && Luna.Edit(w, "planner") <= 3;
    }
    static readonly HashSet<string> Lead = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "show", "add", "new", "create", "schedule", "remind", "reminder", "what", "whats", "what's", "when", "whens", "when's", "mark", "complete", "finish", "done", "delete", "remove", "cancel",
        "move", "push", "postpone", "reschedule", "rename", "sync", "today", "tomorrow", "tonight", "week", "month", "day", "calendar", "tasks", "task", "agenda", "events", "event", "meeting", "skip",
        "clear", "am", "do", "how", "list", "read", "set", "make", "put", "plan", "book", "upcoming", "next", "this", "free", "busy", "undo", "repeat", "refresh"
    };

    // True when the dictated text is addressed to the planner: the word at the start, followed by a pause (comma)
    // or a planner-ish word, so ordinary sentences that start with "planner" are still typed normally.
    public static bool TryDirect(string text, out string rest)
    {
        rest = null;
        // "plan her" / "plain her" / "plan er" are one spoken word
        text = Regex.Replace(text ?? "", @"^(\W*(?:(?:hey|hi|hello|ok|okay|yo)\W+)*(?:(?:the|my)\s+)?)(?:plan|plain)\s+(?:her|er|or|a)\b", "$1planner", IC);
        var m = DirectRx.Match(text);
        if (!m.Success) return false;
        string w = m.Groups["w"].Value;
        bool exact = w.Equals("planner", StringComparison.OrdinalIgnoreCase);
        bool strict = exact || PlannerStrict.Contains(w);
        if (!strict && !FuzzyPlanner(w)) return false;
        string r = m.Groups["rest"].Value.Trim();
        bool punct = m.Groups["p"].Value.IndexOfAny(new[] { ',', ':', '.', '-', '–', '—', '!', '?' }) >= 0;
        string next = Regex.Match(r, @"^[A-Za-z']+").Value;
        if (r.Length == 0) { if (!exact) return false; }
        else if (exact) { if (!(punct || Lead.Contains(next))) return false; }
        else if (strict) { if (!((punct && r.Length > 2) || Lead.Contains(next))) return false; }
        else if (!Lead.Contains(next)) return false;
        rest = r;
        return true;
    }

    // Runs what was said to the planner and returns text for the overlay ("✓ ..." = a short confirmation, "" = nothing to say).
    public static string Direct(string rest)
    {
        rest = (rest ?? "").Trim().TrimEnd('.', ',', ';', '!');
        if (rest.Length == 0) return OpenWindow("today");
        var done = TryLocal(rest);
        if (done != null) return done;
        string w = rest.TrimEnd('?').ToLowerInvariant();
        if (Regex.IsMatch(w, @"^(?:the |my )?(?:today|week|this week|month|this month|day|agenda|upcoming|calendar|schedule|tasks?|to-?do list|to-?dos|list)$", IC)) return OpenWindow(ViewFor(w));
        if (w is "tomorrow" or "tonight") return DayPlan(w == "tomorrow" ? DateTime.Today.AddDays(1) : DateTime.Today);
        if (Regex.IsMatch(rest, @"^(?:what|when|where|who|why|how|which|is|are|do|does|did|can|could|should|am)\b", IC))
            return "I didn't catch that. Try “planner, add dentist friday at 3” or “planner, what's on tomorrow”.";
        // anything else is something to add: a time of day makes it an event, otherwise a task
        var p = Parse(rest, DateTime.Now);
        if (p.Title.Length == 0 && p.Start == null) return null;
        if (Luna.DryRun) return $"DRY RUN: {p.Kind} \"{p.Title}\" {p.Describe()}";
        Add(p);
        return Added(p);
    }

    public static string AddedText(Parsed p) => Added(p).TrimStart('✓', ' ');

    static string Added(Parsed p)
    {
        string what = p.Reminder ? "Reminder set" : p.Kind == "event" ? "Event added" : "Task added";
        string d = p.Describe();
        return "✓ " + what + " · " + (p.Title.Length > 0 ? p.Title : p.Kind == "event" ? "Event" : "Task") + (d.Length > 0 ? " · " + d : "");
    }

    // ---------------- the PLAN: hook (Codex writes a planner sentence, the host runs it)

    public static string Hook(string line, out string problem)
    {
        problem = null;
        string r = TryLocal(line.Trim());
        if (r == null) { problem = "I couldn't do that in the planner: " + (line.Length > 80 ? line.Substring(0, 79) + "…" : line); return null; }
        r = r.TrimStart('✓', ' ');
        return r.Length == 0 ? null : r.Split('\n')[0];
    }

    // ---------------- local voice commands

    static DateTime? RangeEnd(string what, out DateTime from, out string label)
    {
        from = DateTime.Today; label = "";
        what = (what ?? "").Trim().ToLowerInvariant();
        if (what.Length == 0 || what is "today" or "for today") { label = "today"; return DateTime.Today.AddDays(1); }
        if (what is "tomorrow" or "for tomorrow") { from = DateTime.Today.AddDays(1); label = "tomorrow"; return from.AddDays(1); }
        if (what is "this week" or "for this week" or "the week" or "this week's" or "week") { label = "this week"; return DateTime.Today.AddDays(7); }   // the next 7 days (a calendar week made "this week" empty on a Sunday)
        if (what is "next week" or "for next week") { from = DateTime.Today.AddDays(7 - ((int)DateTime.Today.DayOfWeek + 6) % 7); label = "next week"; return from.AddDays(7); }
        if (what is "this month") { label = "the next 30 days"; return DateTime.Today.AddDays(30); }
        var r = When.Parse(what, DateTime.Now);
        if (r.At != null && !r.Relative) { from = r.At.Value.Date; label = DayWord(from); return from.AddDays(1); }
        return null;
    }

    static string TaskWhen(Item t) => t.Start != null ? " · " + DayWord(t.Start.Value) : "";

    public static string TryLocal(string l)
    {
        Match m;
        if (string.IsNullOrWhiteSpace(l)) return null;
        var now = DateTime.Now;

        // ---- open the Plan window
        if (Is(l, @"^(?:open|show|pull up|launch|bring up|go to|switch to|take me to|start)(?: me)?(?: up)?(?: my| the)? (?<w>" + PW + @"|plan|calendar|schedule|agenda|upcoming|task list|to-?do list|todo list|my tasks|tasks|week|month|day|today)(?: (?<v>week|month|day|today|agenda|tasks|calendar))?(?: view| window| app| page)?$", out m)
            || Is(l, @"^(?:my |the )?(?<w>" + PW + @"|calendar|agenda|week view|month view|day view|week|month)$", out m))
        {
            string view = ViewFor(m.Groups["v"].Success ? m.Groups["v"].Value : m.Groups["w"].Value);
            return OpenWindow(view);
        }

        // ---- sync now
        if (Is(l, @"^(?:sync|resync|refresh)(?: my| the| all)?(?: google)?(?: (?:calendar|tasks?|to-?dos?|planner|schedule|agenda)(?: and (?:calendar|tasks?))?)?(?: now)?$", out m) && l.Trim().Length > 4)
        {
            if (Luna.DryRun) return "DRY RUN: sync with Google";
            if (!GoogleSync.Connected) return "Google isn't connected yet. Open the Plan window and press Connect Google (see PLANNER.md).";
            _ = GoogleSync.SyncNowAsync();
            return "✓ Syncing with Google";
        }

        // ---- clear finished tasks
        if (Is(l, @"^(?:clear|remove|delete|get rid of|clean up)(?: all| my)?(?: the)?(?: completed| done| finished| checked(?: off)?) tasks?$", out _))
        {
            if (Luna.DryRun) return "DRY RUN: clear done tasks";
            int n = ClearDone();
            return n == 0 ? "✓ No finished tasks to clear" : $"✓ Cleared {n} finished task{(n == 1 ? "" : "s")}";
        }

        // ---- timed reminder: "remind me tomorrow at 5 to call mom", "remind me every monday at 9 to take out the trash"
        // (a countdown like "in 10 minutes" or "every 20 minutes" stays with the timers)
        if (Is(l, @"^(?:set (?:a )?reminder|remind me|reminder)(?: to| about| that| for)? (.+)$", out m) && !Is(l, @"^remind me every\s+(?:\d+|few|couple)?\s*(?:seconds?|secs?|minutes?|mins?|hours?|hrs?)\b", out _))
        {
            var p = Parse(m.Groups[1].Value, now, "reminder");
            if (p.Relative || p.Start == null) return null;
            if (p.Title.Length == 0) return "What should I remind you about?";
            if (Luna.DryRun) return $"DRY RUN: reminder \"{p.Title}\" {p.Describe()}";
            Add(p);
            return Added(p);
        }

        // ---- add a task
        if (Is(l, @"^(?:add|create|make|put|new|set)(?: me)?(?: a| an| another)?(?: new)?(?: (?<pri>high|medium|low|urgent|important)(?: priority)?)? (?:task|to-?do|todo|to do)(?: item)?(?: (?:called|named|titled|to|for me to|that says|saying))?[:,]?\s+(?<t>.+)$", out m)
            || Is(l, @"^(?<pri>)(?:task|to-?do|todo)[:,]?\s+(?<t>.+)$", out m)
            || Is(l, @"^(?<pri>)(?:add|put) (?<t>.+?) (?:to|on|onto|in) (?:my |the )?(?:tasks?|to-?do|to do|todo)(?: list)?$", out m))
        {
            string text = m.Groups["t"].Value + (m.Groups["pri"].Success && m.Groups["pri"].Length > 0 ? " " + m.Groups["pri"].Value + " priority" : "");
            var p = Parse(text, now, "task");
            if (p.Title.Length == 0) return "What's the task?";
            if (Luna.DryRun) return $"DRY RUN: task \"{p.Title}\" {p.Describe()}";
            Add(p);
            return Added(p);
        }

        // ---- add an event
        if (Is(l, @"^(?:add|create|schedule|set up|book|make|put|plan)(?: me)?(?: a| an)?(?: new)?(?: (?<adj>recurring|repeating|weekly|daily|monthly|biweekly|bi-weekly|yearly))? (?:calendar )?(?:event|meeting|appointment)(?: (?:called|named|for|about|with|titled|to))? (?<t>.+)$", out m)
            || Is(l, @"^(?:add|put|schedule) (?<t>.+?) (?:to|on|onto|in|into) (?:my |the )?(?:google )?calendar(?: (?<t2>.+))?$", out m)
            || (Is(l, @"^schedule (?<t>.+)$", out m) && Parse(m.Groups["t"].Value, now, "event").Start != null))
        {
            string text = (m.Groups["adj"].Success ? m.Groups["adj"].Value + " " : "") + m.Groups["t"].Value + (m.Groups["t2"].Success ? " " + m.Groups["t2"].Value : "");
            var p = Parse(text, now, "event");
            if (p.Start == null) return "When is it? Try \"add event dentist tomorrow at 3pm\".";
            if (p.Title.Length == 0) p.Title = "Event";
            if (Luna.DryRun) return $"DRY RUN: event \"{p.Title}\" {p.Describe()}";
            Add(p);
            return Added(p);
        }

        // ---- add to a named list: "add milk to my shopping list"
        if (Is(l, @"^(?:add|put) (?<t>.+?) (?:to|on|onto|in|into) (?:my |the )?(?<c>[a-z][\w-]*(?: [a-z][\w-]*)?) list$", out m) && !NotLists.Contains(m.Groups["c"].Value.Trim()))
        {
            var p = Parse(m.Groups["t"].Value + " in " + m.Groups["c"].Value + " list", now, "task");
            if (p.Title.Length > 0)
            {
                if (Luna.DryRun) return $"DRY RUN: task \"{p.Title}\" {p.Describe()}";
                Add(p);
                return Added(p);
            }
        }

        // ---- a day in one go: "what's my day", "what's on tomorrow", "what do I have today"
        if (Is(l, @"^(?:what(?:'s| is)|show|give me|tell me|read)(?: me)?(?: on)?(?: my| the)? (?:day|agenda|plan|schedule|planner)(?: look(?:s)? like)?(?: (?<d>today|tomorrow|tonight))?$", out m)
            || Is(l, @"^what(?:'s| is)? (?:on )?(?<d>today|tomorrow|tonight)$", out m)
            || Is(l, @"^what do i have (?<d>today|tomorrow|tonight)$", out m)
            || Is(l, @"^(?:do i have|have i got) anything (?:on |for )?(?<d>today|tomorrow|tonight)$", out m))
        {
            if (Luna.DryRun) return "DRY RUN: day plan";
            string d = m.Groups["d"].Success ? m.Groups["d"].Value.ToLowerInvariant() : "today";
            return DayPlan(d == "tomorrow" ? DateTime.Today.AddDays(1) : DateTime.Today);
        }

        // ---- list tasks
        if (Is(l, @"^(?:what(?:'s| is| are)|show|list|read|tell me|give me)(?: me)?(?: all)?(?: of)?(?: my| the)?(?: open| pending| upcoming| current)? (?:tasks|to-?dos?|to do list|todo list|to-?do list)(?: (?:left|due|i have))?(?: (today|tomorrow|this week|overdue))?$", out m)
            || Is(l, @"^what do i (?:still )?(?:have|need) to do(?: (today|tomorrow|this week))?$", out m)
            || Is(l, @"^what(?:'s| is) (?:on )?my (?:to-?do|task)(?: list)?(?: (today|tomorrow|this week))?$", out m))
        {
            var all = OpenTasks();
            string f = m.Groups[1].Success ? m.Groups[1].Value.ToLowerInvariant() : "";
            var list = f switch
            {
                "today" => all.Where(t => t.Start != null && t.Start.Value.Date <= DateTime.Today),
                "tomorrow" => all.Where(t => t.Start != null && t.Start.Value.Date == DateTime.Today.AddDays(1)),
                "this week" => all.Where(t => t.Start != null && t.Start.Value.Date < DateTime.Today.AddDays(7)),
                "overdue" => all.Where(t => t.Start != null && t.Start.Value.Date < DateTime.Today),
                _ => all
            };
            var shown = list.ToList();
            if (shown.Count == 0) return f.Length > 0 ? $"✓ Nothing due {f}" : "✓ No open tasks";
            return $"{shown.Count} task{(shown.Count == 1 ? "" : "s")}{(f.Length > 0 ? " " + f : "")}\n" + string.Join("\n", shown.Take(12).Select(TaskLine)) + (shown.Count > 12 ? $"\n…and {shown.Count - 12} more" : "");
        }

        // ---- one named list: "what's on my school list", "show my work tasks"
        if (Is(l, @"^(?:what(?:'s| is| are)|show|list|read|tell me|give me)(?: me)?(?: on| in)?(?: my| the)? (?<c>[a-z][\w-]*(?: [a-z][\w-]*)?) (?:list|tasks)$", out m)
            && !NotLists.Contains(m.Groups["c"].Value) && !Regex.IsMatch(m.Groups["c"].Value, @"^(?:open|pending|upcoming|current|all|my|the)$", IC))
        {
            string c = Cats().FirstOrDefault(x => x.Equals(m.Groups["c"].Value.Trim(), StringComparison.OrdinalIgnoreCase));
            if (c == null) return null;
            var shown = OpenTasks().Where(t => string.Equals(t.Cat, c, StringComparison.OrdinalIgnoreCase)).ToList();
            return shown.Count == 0 ? $"✓ Nothing left on {c}" : $"{c} · {shown.Count} task{(shown.Count == 1 ? "" : "s")}\n" + string.Join("\n", shown.Take(12).Select(TaskLine));
        }

        // ---- calendar questions
        if (Is(l, @"^what(?:'s| is)(?: my)? next(?: event| meeting| appointment| thing)?(?: on my calendar)?$|^(?:what(?:'s| is)|when(?:'s| is)) my next (?:event|meeting|appointment)$|^what(?:'s| is) coming up$", out _))
        {
            var next = Events(DateTime.Now.AddMinutes(-5), DateTime.Today.AddDays(60)).FirstOrDefault(e => !e.AllDay) ?? Events(DateTime.Today, DateTime.Today.AddDays(60)).FirstOrDefault();
            return next == null ? "✓ Nothing coming up on your calendar" : EventLine(next, true).Substring(2);
        }
        if (Is(l, @"^(?:what(?:'s| is| are| do i have on)|show|read|list|tell me|give me|do i have anything on)(?: me)?(?: on)?(?: my| the)? (?:calendar|schedule|agenda|events|meetings|appointments)(?: (?:for |on |this |next )?(.+))?$", out m)
            || Is(l, @"^what(?:'s| is) (?:on )?(?:my )?(?:calendar|schedule|agenda)(?: (?:for |on )?(.+))?$", out m)
            || Is(l, @"^what do i have (?:on )?(tomorrow|this week|next week|monday|tuesday|wednesday|thursday|friday|saturday|sunday)$", out m))
        {
            string what = m.Groups[1].Success ? m.Groups[1].Value : "";
            if (Is(l, @"\b(this|next) (week|month)\b", out var tm) && what.Length > 0 && !what.StartsWith("this") && !what.StartsWith("next")) what = tm.Value;
            var end = RangeEnd(what, out var from, out string label);
            if (end == null) return null;
            var evs = Events(from, end.Value);
            if (evs.Count == 0) return $"✓ Nothing on your calendar {label}";
            if (end.Value - from <= TimeSpan.FromDays(1)) return $"{label}\n" + string.Join("\n", evs.Take(12).Select(e => EventLine(e, false)));
            return string.Join("\n", evs.Take(14).Select(e => EventLine(e, true)));
        }

        // ---- when is my dentist appointment / when is the lab report due
        if (Is(l, @"^when(?:'s| is| are)(?: my| the)? (?:next )?(?<t>.+?)(?: due| scheduled| happening)?$", out m) && !Is(l, @"^when(?:'s| is) (?:it|that|this)\b", out _))
        {
            string q = m.Groups["t"].Value;
            var item = Best(Plannable(), q);
            if (item == null) return null;
            if (item.Kind == "task") return item.Start == null ? $"✓ {item.Title} has no due date" : $"{item.Title} · due {DueText(item)}";
            var occ = Occurrences(DateTime.Now, DateTime.Today.AddDays(365), Load()).FirstOrDefault(o => o.Item.Id == item.Id);
            return occ == null ? $"✓ I can't find a coming {item.Title}" : EventLine(occ, true).Substring(2);
        }

        // ---- am I free?
        if (Is(l, @"^(?:am i|are we) (?:free|busy|available)(?: (?:on|at|for))? (?<w>.+)$", out m)
            || Is(l, @"^do i have (?:anything|something|any plans|plans|a conflict)(?: (?:on|at|for))? (?<w>.+)$", out m))
        {
            var w = When.Parse(m.Groups["w"].Value, now, wantDuration: true);
            if (w.At == null) return null;
            DateTime a = w.HasTime ? w.At.Value : w.At.Value.Date, b = w.HasTime ? a + (w.Duration ?? TimeSpan.FromHours(1)) : a.AddDays(1);
            if (!w.HasTime) return DayPlan(a);
            var busy = Occurrences(a, b).ToList();
            string when = DayWord(a) + " at " + a.ToString("h:mm tt");
            return busy.Count == 0 ? $"✓ You're free {when}" : $"Busy {when}\n" + string.Join("\n", busy.Take(5).Select(e => EventLine(e, false)));
        }

        // ---- complete a task
        if (Is(l, @"^(?:(?:please )?(?:complete|finish|check off|tick off|cross off|knock out)(?: the)?(?: task)?|mark(?: the)?(?: task)?|i(?:'ve| have)? (?:just )?(?:finished|completed|done)|i(?:'m| am) done with|done with|finished)\s+(.+?)(?: (?:as )?(?:done|complete|completed|finished))?$", out m))
        {
            var t = Best(OpenTasks(), m.Groups[1].Value);
            if (t == null) return Is(l, @"\btask\b", out _) ? $"I couldn't find an open task like \"{m.Groups[1].Value}\"." : null;
            if (Luna.DryRun) return "DRY RUN: complete " + t.Title;
            SetDone(t.Id, true);
            var after = Live().FirstOrDefault(i => i.Id == t.Id);
            return "✓ Done · " + t.Title + (after != null && !after.Done && after.Start != null ? " · next " + DayWord(after.Start.Value) : "");
        }

        // ---- reschedule: "move dentist to friday at 4", "push the lab report to tomorrow", "postpone the meeting by an hour"
        if (Is(l, @"^(?:move|push|postpone|reschedule|bump|shift|delay|put off)(?: the| my)? (?<t>.+?)(?: (?:back|forward|out))? (?:(?<by>by)|to|until|till|for|onto|on|at) (?<w>.+)$", out m))
        {
            var item = Best(Plannable(), m.Groups["t"].Value);
            if (item == null) goto afterMove;
            string wtxt = m.Groups["w"].Value.Trim();
            DateTime? ns = null;
            var by = Regex.Match(wtxt, @"^(?<n>\d+|an?|one|two|three|four|five|six|seven)\s*(?<u>minutes?|mins?|hours?|hrs?|days?|weeks?)$", IC);
            if (m.Groups["by"].Success || by.Success)
            {
                if (!by.Success) goto afterMove;
                string nn = by.Groups["n"].Value.ToLowerInvariant();
                int n = int.TryParse(nn, out int nv) ? nv : nn switch { "two" => 2, "three" => 3, "four" => 4, "five" => 5, "six" => 6, "seven" => 7, _ => 1 };
                char u = char.ToLowerInvariant(by.Groups["u"].Value[0]);
                var cur = item.Start ?? DateTime.Today;
                if (item.Kind == "task" && u is 'm' or 'h') goto afterMove;
                ns = u == 'w' ? cur.AddDays(7 * n) : u == 'd' ? cur.AddDays(n) : u == 'h' ? cur.AddHours(n) : cur.AddMinutes(n);
            }
            else
            {
                var w = When.Parse(wtxt, now);
                if (w.At == null || w.Relative) goto afterMove;
                var old = item.Start ?? DateTime.Today;
                ns = w.HasTime ? w.At : (item.Kind == "event" && !item.AllDay ? w.At.Value.Date + old.TimeOfDay : w.At.Value.Date);
            }
            if (Luna.DryRun) return $"DRY RUN: move {item.Title} to {ns:g}";
            var target = ns.Value;
            Update(item.Id, x =>
            {
                var dur = x.Kind == "event" ? (x.End ?? x.Start.Value.AddHours(1)) - x.Start.Value : TimeSpan.Zero;
                x.Start = x.Kind == "task" || x.AllDay ? target.Date : target;
                if (x.Kind == "event") x.End = x.AllDay ? target.Date + dur : target + dur;
                x.Fired = false; x.FiredFor = null;
                var rule = Rule.Parse(x.Repeat);
                if (rule is { Freq: "WEEKLY" } && rule.Days.Count <= 1) { rule.Days = new() { target.DayOfWeek }; x.Repeat = rule.ToRrule(x.AllDay); x.Skip.Clear(); }
            });
            return "✓ Moved · " + item.Title + " · " + (item.Kind == "event" && !item.AllDay ? DayWord(target) + " " + target.ToString("h:mm tt") : DayWord(target));
        }
        afterMove:

        // ---- rename
        if (Is(l, @"^(?:rename|retitle)(?: the| my)?(?: task| event)? (?<t>.+?) to (?<n>.+)$", out m))
        {
            var item = Best(Plannable(), m.Groups["t"].Value);
            if (item == null) return null;
            string nn = When.Cap(m.Groups["n"].Value.Trim());
            if (Luna.DryRun) return $"DRY RUN: rename {item.Title} to {nn}";
            Update(item.Id, x => x.Title = nn);
            return "✓ Renamed · " + nn;
        }

        // ---- priority
        if (Is(l, @"^(?:make|set|mark|flag)(?: the| my)? (?<t>.+?) (?:as |to |a )?(?:(?<p>high|medium|low|normal) priority|(?<p>urgent|important))$", out m)
            || Is(l, @"^(?:make|set|mark|flag)(?: the| my)? (?<t>.+?) (?:as |to |a )?(?<p>urgent|important)$", out m))
        {
            var item = Best(Plannable(), m.Groups["t"].Value);
            if (item == null) return null;
            string pv = m.Groups["p"].Value.ToLowerInvariant();
            int pr = pv is "high" or "urgent" or "important" ? 3 : pv == "medium" ? 2 : pv == "low" ? 1 : 0;
            if (Luna.DryRun) return $"DRY RUN: {item.Title} priority {pr}";
            Update(item.Id, x => x.Priority = pr);
            return $"✓ {item.Title} · {(pr == 0 ? "normal" : pv is "urgent" or "important" ? "high" : pv)} priority";
        }

        // ---- repeat: "make the gym session repeat every monday and thursday", "stop repeating the standup"
        if (Is(l, @"^(?:make|set)(?: the| my)? (?<t>.+?) (?:repeat|recur|repeating)\s+(?<r>.+)$", out m)
            || Is(l, @"^repeat(?: the| my)? (?<t>.+?) (?<r>(?:every|daily|weekly|monthly|yearly|annually|bi-?weekly|fortnightly).*)$", out m))
        {
            var item = Best(Plannable(), m.Groups["t"].Value);
            if (item == null) return null;
            var rule = Rule.FromText(m.Groups["r"].Value, now, out _);
            if (rule == null) return $"I didn't catch how often. Try “make {item.Title.ToLowerInvariant()} repeat every week”.";
            if (Luna.DryRun) return $"DRY RUN: {item.Title} {rule.Describe(item.Start ?? DateTime.Today)}";
            Update(item.Id, x =>
            {
                var s0 = x.Start ?? DateTime.Today;
                var first = rule.FirstOn(s0);
                if (first.Date != s0.Date) { var span = x.End - x.Start; x.Start = x.AllDay || x.Kind == "task" ? first.Date : first; if (x.Kind == "event") x.End = x.Start + (span ?? TimeSpan.FromHours(1)); }
                rule.ResolveSpan(x.Start.Value.Date);
                x.Repeat = rule.ToRrule(x.AllDay || x.Kind == "task"); x.Skip.Clear();
            });
            return "✓ " + item.Title + " · " + rule.Describe(item.Start ?? DateTime.Today).ToLowerInvariant();
        }
        if (Is(l, @"^(?:stop|cancel|end|turn off)(?: the)? (?:repeating|repeat|recurring|recurrence)(?: of| for| on)?(?: the| my)? (?<t>.+)$", out m)
            || Is(l, @"^(?:make|set)(?: the| my)? (?<t>.+?) (?:a )?(?:one[- ]?time|one[- ]?off|non-?repeating|not repeating)$", out m))
        {
            var item = Best(Plannable().Where(i => i.Repeat != null), m.Groups["t"].Value);
            if (item == null) return null;
            if (Luna.DryRun) return "DRY RUN: stop repeating " + item.Title;
            Update(item.Id, x => { x.Repeat = null; x.Skip.Clear(); });
            return "✓ " + item.Title + " no longer repeats";
        }

        // ---- move into a list: "put the lab report in my school list"
        if (Is(l, @"^(?:move|put|file|add)(?: the| my)? (?<t>.+?) (?:to|into|in|under) (?:my |the )?(?<c>[a-z][\w-]*(?: [a-z][\w-]*)?) list$", out m) && !NotLists.Contains(m.Groups["c"].Value.Trim()))
        {
            var item = Best(Plannable(), m.Groups["t"].Value);
            if (item != null)
            {
                string c = Catify(m.Groups["c"].Value);
                if (Luna.DryRun) return $"DRY RUN: {item.Title} -> {c}";
                Update(item.Id, x => x.Cat = c);
                return $"✓ {item.Title} · {c}";
            }
        }

        // ---- skip the next occurrence of a repeating event
        if (Is(l, @"^skip(?: the| my)?(?: next)? (?<t>.+?)(?: (?<d>today|tomorrow|this week))?$", out m))
        {
            var item = Best(Live().Where(i => i.Kind == "event" && i.Repeat != null), m.Groups["t"].Value);
            if (item == null) return null;
            DateTime from = m.Groups["d"].Value.Equals("tomorrow", StringComparison.OrdinalIgnoreCase) ? DateTime.Today.AddDays(1) : DateTime.Now;
            var occ = Occurrences(from, from.AddDays(60)).FirstOrDefault(o => o.Item.Id == item.Id);
            if (occ == null) return $"✓ Nothing coming up for {item.Title}";
            if (Luna.DryRun) return $"DRY RUN: skip {item.Title} {occ.Start:g}";
            SkipOccurrence(item.Id, occ.Start);
            return $"✓ Skipped {item.Title} · {DayWord(occ.Start)}";
        }

        // ---- delete a task / event
        if (Is(l, @"^(?:delete|remove|cancel|scratch|get rid of)(?: the| my)? (task|event|meeting|appointment) (?:called |named |about |with )?(.+)$", out m))
        {
            bool task = m.Groups[1].Value.Equals("task", StringComparison.OrdinalIgnoreCase);
            var pool = Live().Where(i => i.Kind == (task ? "task" : "event"));
            var t = Best(pool, m.Groups[2].Value);
            if (t == null) return $"I couldn't find a {m.Groups[1].Value} like \"{m.Groups[2].Value}\".";
            if (Luna.DryRun) return "DRY RUN: delete " + t.Title;
            Remove(t.Id);
            return "✓ Removed · " + t.Title + (t.Repeat != null ? " (all of them)" : "");
        }

        return null;
    }
}
