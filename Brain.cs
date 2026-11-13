using System.Text.Json;
using System.Text.RegularExpressions;

namespace VowenLite;

// Decides how much model a request deserves when the user did not name one, and keeps count of what was spent.
// The ChatGPT plan is shared with the user's own Codex use, so the rule is: cheapest thing that will do the job well.
//   quick fact / short question      -> Luna, low effort        (the cheapest run there is)
//   ordinary task or longer question -> Luna, the configured effort
//   hard thinking, real writing, code in a project, driving the screen, ArtCraft work -> Terra (Settings.SmartModel), capped per day
// Saying a model ("Sol, ...", "swap to Terra") always wins over this. `--routetest "<request>"` prints the decision.
static class Brain
{
    public readonly record struct Pick(string Model, string Effort, string Name, string Why, bool Smart);

    static readonly Regex Hard = new(@"\b(think (?:hard|carefully|deeply|it through)|carefully|step by step|in depth|in detail|thorough(?:ly)?|deep dive|research|analy[sz]e|prove|derive|proof|optimi[sz]e|architecture|design (?:a|an|the) (?:system|schema|algorithm)|trade-?offs?|root cause|why (?:is|does|did) .* (?:fail|crash|break|slow))\b", RegexOptions.IgnoreCase);
    static readonly Regex Code = new(@"\b(refactor|implement|debug|fix (?:the|this|my|a) (?:bug|build|error|crash|test|code|function|merge)|write (?:a|the|some) (?:function|class|script|program|test|component|parser|regex)|add (?:a |the )?(?:feature|endpoint|test|command|option)|stack trace|exception|compile|unit tests?|pull request|code review|merge conflicts?)\b|\.(?:cs|js|ts|py|rs|cpp|java|sv|v)\b", RegexOptions.IgnoreCase);
    internal static readonly Regex Writing = new(@"\b(?:write|draft|compose|finish|complete|continue|extend|expand|rewrite|rephrase|polish|proofread|humani[sz]e|improve|shorten|lengthen)\b.{0,60}\b(?:essay|paragraphs?|doc(?:k|ument|s)?|report|assignment|paper|article|story|speech|cover letter|letter|email|message|reply|response|post|summary|conclusion|introduction|intro|thesis|abstract|caption|bio|section|answer)\b|\b(?:essay|report|paper|assignment)\b.{0,30}\b(?:for me|about|on)\b", RegexOptions.IgnoreCase);
    static readonly Regex Trivial = new(@"^(?:what(?:'s| is| are| was)?|who(?:'s| is| was)?|when|where|how (?:many|much|old|long|far|tall|big|do you (?:say|spell))|define|spell|translate|convert|is|are|does|did|capital of|meaning of)\b", RegexOptions.IgnoreCase);

    public static Pick Route(string request, bool question, bool widget, bool control, bool craft, Settings s)
    {
        string luna = s.LunaModel, eff = string.IsNullOrWhiteSpace(s.LunaEffort) ? "medium" : s.LunaEffort;
        if (!s.AutoRoute) return new(luna, eff, "Luna", "auto-routing off", false);
        int words = Regex.Split(request.Trim(), @"\s+").Length;

        bool design = craft && !Regex.IsMatch(request, @"\bpdfs?\b", RegexOptions.IgnoreCase);   // merging PDFs is clerical; making a video or a logo is not
        string why = control ? "screen control" : design ? "ArtCraft design work" : Hard.IsMatch(request) ? "needs careful thinking" : Writing.IsMatch(request) ? "real writing"
            : !question && Code.IsMatch(request) ? "code work" : null;
        if (why != null)
        {
            string smart = SmartModel(s);
            if (smart != null && SmartLeft(s) > 0)
                // Driving the screen is many small steps: low effort keeps each one cheap. Thinking and writing get medium.
                return new(smart, control ? "low" : "medium", Cap(s.SmartModel), why, true);
            return new(luna, eff == "low" ? "medium" : eff, "Luna", why + (smart == null ? "" : ", but today's smart-model budget is used up"), false);
        }
        if (widget) return new(luna, eff, "Luna", "widget", false);
        if (question && words <= 16 && Trivial.IsMatch(request.Trim())) return new(luna, "low", "Luna", "quick question", false);
        return new(luna, eff, "Luna", question ? "question" : "task", false);
    }

    static string Cap(string f) => string.IsNullOrWhiteSpace(f) ? "Terra" : char.ToUpperInvariant(f.Trim()[0]) + f.Trim().Substring(1).ToLowerInvariant();

    static string SmartModel(Settings s)
    {
        string f = (s.SmartModel ?? "").Trim().ToLowerInvariant();
        if (f is "" or "luna" or "off" or "none") return null;
        return f is "sol" or "terra" ? Luna.ModelSlug(f, s) : s.SmartModel.Trim();   // a family name, or an exact model id
    }

    // ---------------- usage (usage.json): today's runs and tokens, so "how much have I used" is free and the smart budget holds

    sealed class Day { public string Date { get; set; } = ""; public int Runs { get; set; } public int Smart { get; set; } public long In { get; set; } public long Cached { get; set; } public long Out { get; set; } }
    sealed class Store { public Day Today { get; set; } = new(); public List<Day> History { get; set; } = new(); }
    static readonly object gate = new();
    static string FilePath => Path.Combine(Paths.DataDir, "usage.json");

    static Store Load()
    {
        Store st = null;
        try { if (File.Exists(FilePath)) st = JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath)); } catch { }
        st ??= new Store();
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        if (st.Today.Date != today)
        {
            if (st.Today.Date.Length > 0 && st.Today.Runs > 0) st.History.Add(st.Today);
            while (st.History.Count > 30) st.History.RemoveAt(0);
            st.Today = new Day { Date = today };
        }
        return st;
    }

    static void Save(Store st) { try { File.WriteAllText(FilePath, JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true })); } catch { } }

    static int SmartLeft(Settings s) { lock (gate) return Math.Max(0, s.SmartRunsPerDay - Load().Today.Smart); }

    public static void NoteRun(bool smart) { lock (gate) { var st = Load(); st.Today.Runs++; if (smart) st.Today.Smart++; Save(st); } }

    // A `turn.completed` event from `codex exec --json`: {"usage":{"input_tokens":..,"cached_input_tokens":..,"output_tokens":..}}
    public static void NoteUsage(JsonElement usage)
    {
        long N(string k) => usage.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        lock (gate) { var st = Load(); st.Today.In += N("input_tokens"); st.Today.Cached += N("cached_input_tokens"); st.Today.Out += N("output_tokens"); Save(st); }
    }

    static string K(long n) => n >= 1_000_000 ? (n / 1_000_000.0).ToString("0.0") + "M" : n >= 1000 ? (n / 1000.0).ToString("0") + "k" : n.ToString();

    public static string UsageText(Settings s)
    {
        Store st; lock (gate) st = Load();
        var t = st.Today;
        var sb = new System.Text.StringBuilder();
        sb.Append($"Today: {t.Runs} model run{(t.Runs == 1 ? "" : "s")} ({t.Smart} on {Cap(s.SmartModel)}, limit {s.SmartRunsPerDay}/day)\n");
        sb.Append($"• {K(t.In)} tokens in ({K(t.Cached)} of them cached), {K(t.Out)} out\n");
        var week = st.History.TakeLast(6).ToList();
        if (week.Count > 0) sb.Append($"• Last {week.Count} day{(week.Count == 1 ? "" : "s")} before today: {week.Sum(d => d.Runs)} runs, {K(week.Sum(d => d.In))} in, {K(week.Sum(d => d.Out))} out\n");
        sb.Append("Local commands (timers, planner, apps, volume, clipboard…) cost nothing and are not counted.");
        return sb.ToString();
    }

    // ---------------- free local commands that belong to the "brain": usage, voice on/off, humanize the clipboard

    public static string TryLocal(string l, Settings s)
    {
        if (Regex.IsMatch(l, @"^(?:show (?:me )?)?(?:my |the )?(?:(?:codex|chat ?gpt|token|model|ai|luna) )?usage(?: today| so far| stats)?$|^how much (?:(?:codex|chat ?gpt|usage|tokens?|ai) )?(?:have (?:i|you|we) used|did (?:i|you|we) use)(?: today)?$|^how many tokens (?:have (?:i|you|we) used|did (?:i|you|we) use)(?: today)?$", RegexOptions.IgnoreCase))
            return UsageText(s);

        var m = Regex.Match(l, @"^(?:turn |switch )?(?:your |the )?(?:voice|speech|speaking|talking)(?: replies| answers)? (on|off)$|^(?:turn |switch )(on|off) (?:your |the )?(?:voice|speech|speaking|talking)(?: replies| answers)?$|^(be quiet|stop talking|go silent|silent mode|shut up|mute yourself)$|^(talk to me|speak to me|speak your answers|voice mode|talk back)$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            bool on = m.Groups[1].Value.Equals("on", StringComparison.OrdinalIgnoreCase) || m.Groups[2].Value.Equals("on", StringComparison.OrdinalIgnoreCase) || m.Groups[4].Success;
            if (Luna.DryRun) return "DRY RUN: spoken replies " + (on ? "on" : "off");
            Assistant.StopSpeaking();
            s.SpeakReplies = on ? "short" : "off"; s.Save();
            return on ? "✓ I'll say short answers out loud" : "✓ Silent: answers on screen only";
        }

        if (Regex.IsMatch(l, @"^humani[sz]e (?:the |my |this |that )?(?:clipboard|copied text|text|it|this|that)$", RegexOptions.IgnoreCase))
        {
            string clip = Luna.ClipboardText();
            if (clip.Trim().Length < 20) return "Copy the text first, then say “humanize the clipboard”.";
            string outText = Humanize.Clean(clip, out int changes);
            if (Luna.DryRun) return $"DRY RUN: humanize the clipboard ({changes} changes)";
            if (changes == 0) return "✓ Nothing robotic found in it";
            Assistant.SetClipboard(outText);
            return $"✓ Tidied {changes} thing{(changes == 1 ? "" : "s")} · paste it back";
        }
        // "plan my day": fit the tasks that matter into the free time around today's calendar (free, local). "block it in" then books them.
        if (Regex.IsMatch(l, @"^(?:plan|organi[sz]e|schedule|sort out|time ?block)(?: out)? (?:my |the )?(?:rest of (?:my |the )?)?(?:day|today|afternoon|evening|tasks|to-?dos?)(?: for me)?$", RegexOptions.IgnoreCase))
            return DayPlan();
        if (Regex.IsMatch(l, @"^(?:block|book|put|lock) (?:it|them|that|those|the plan) in(?: my calendar)?$|^(?:add|put) (?:it|them|that|the plan) (?:to|on|in) my calendar$", RegexOptions.IgnoreCase))
        {
            if (proposal.Count == 0 || DateTime.Now - proposedAt > TimeSpan.FromMinutes(30)) return "Say “plan my day” first, then “block it in”.";
            if (Luna.DryRun) return $"DRY RUN: add {proposal.Count} focus blocks to the calendar";
            foreach (var b in proposal) Planner.AddEvent(b.Title, b.Start, b.End, false, 5, null, "Focus");
            int n = proposal.Count; proposal.Clear();
            return $"✓ Blocked in {n} focus session{(n == 1 ? "" : "s")}";
        }
        return null;
    }

    static readonly List<(string Title, DateTime Start, DateTime End)> proposal = new();
    static DateTime proposedAt;

    static string DayPlan()
    {
        var now = DateTime.Now;
        var tasks = Planner.OpenTasks()
            .Where(t => (t.Start != null && t.Start.Value.Date <= now.Date.AddDays(1)) || t.Priority >= 2)
            .OrderByDescending(t => t.Start != null && t.Start.Value.Date < now.Date)      // overdue first
            .ThenByDescending(t => t.Priority).ThenBy(t => t.Start ?? DateTime.MaxValue).Take(6).ToList();
        if (tasks.Count == 0) return "✓ Nothing due today or tomorrow, and no high-priority tasks";

        // free time from now (rounded up to a quarter hour) until 22:00, minus today's events
        var start = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddMinutes((now.Minute / 15 + 1) * 15);
        if (start.Hour < 8) start = now.Date.AddHours(8);
        var end = now.Date.AddHours(22);
        var busy = Planner.Events(now.Date, now.Date.AddDays(1)).Where(e => !e.AllDay && e.End > start).OrderBy(e => e.Start).Select(e => (e.Start, e.End, e.Title)).ToList();
        proposal.Clear();
        var rows = new List<(DateTime At, DateTime To, string What, bool Task)>();
        foreach (var e in busy) rows.Add((e.Start, e.End, e.Title, false));
        var cursor = start;
        var left = new List<string>();
        foreach (var t in tasks)
        {
            int mins = t.Priority == 3 ? 60 : 45;
            while (true)
            {
                var clash = busy.FirstOrDefault(b => b.Start < cursor.AddMinutes(mins) && b.End > cursor);
                if (clash.Title == null) break;
                cursor = clash.End.AddMinutes(5);
            }
            if (cursor.AddMinutes(mins) > end) { left.Add(t.Title); continue; }
            proposal.Add((t.Title, cursor, cursor.AddMinutes(mins)));
            rows.Add((cursor, cursor.AddMinutes(mins), t.Title + (t.Priority == 3 ? " (high priority)" : "") + (t.Start != null && t.Start.Value.Date < now.Date ? " (overdue)" : ""), true));
            cursor = cursor.AddMinutes(mins + 10);
        }
        proposedAt = now;
        var sb = new System.Text.StringBuilder();
        sb.Append(proposal.Count == 0 ? "No free time left today for the tasks that are due.\n\n" : $"Here is a plan for the rest of today: {proposal.Count} focus block{(proposal.Count == 1 ? "" : "s")} around your calendar.\n\n");
        sb.Append("| Time | What |\n|---|---|\n");
        foreach (var r in rows.OrderBy(r => r.At)) sb.Append("| ").Append(r.At.ToString("h:mm tt")).Append(" – ").Append(r.To.ToString("h:mm tt")).Append(" | ").Append(r.Task ? "**" + r.What + "**" : r.What + " (calendar)").Append(" |\n");
        if (left.Count > 0) sb.Append("\nNo room today for: ").Append(string.Join(", ", left)).Append(".\n");
        if (proposal.Count > 0) sb.Append("\nSay **“block it in”** to put the focus blocks on your calendar (they sync to your phone).");
        return sb.ToString();
    }
}

// Makes text read like a person wrote it. Two parts: a prompt block that tells the model how to write in the user's voice, and a free
// rule pass that removes the tics models fall back on anyway (stock openers, inflated words, em dashes, curly quotes, markdown in prose).
static class Humanize
{
    public const string StyleBlock =
        "WRITING STYLE: this text goes out under the user's name, so write the way a real person at their level writes, and match what is already in the document: same voice (first or third person), tense, vocabulary level, paragraph length, spelling (US/UK) and formatting. " +
        "Read their existing text first and continue it; do not restart, summarise or repeat what is there. Be specific and concrete: real details, examples and plain claims instead of filler. Vary sentence length; some short. Use contractions where they would. " +
        "Do not use: em dashes or semicolons where a full stop works, stock openers (Moreover, Furthermore, Additionally, In conclusion, Overall, It is important to note), inflated words (delve, tapestry, testament, pivotal, crucial, foster, leverage, utilize, landscape, realm, multifaceted, underscore, showcase), " +
        "\"not only X but also Y\", rule-of-three lists, rhetorical questions, a neat moral at the end of every paragraph, bullet points or markdown in prose, or anything addressed to the reader about the writing itself. " +
        "Do not invent facts, quotes, citations or personal experiences; if the piece needs something only the user knows, leave a short [bracketed note] there. If the task or its instructions on screen say it must be the user's own unaided work, say so in your reply instead of writing it.\n\n";

    static readonly Regex Openers = new(@"((?:^|(?<=[.!?])[ \t]+|\n[ \t]*))(?:(?:Moreover|Furthermore|Additionally|In addition|Notably|Importantly|Indeed|Overall|Ultimately|In conclusion|In summary|To summari[sz]e|In essence|That being said|With that said),|It(?:'s| is) (?:important|worth|crucial|essential) (?:to note|noting|to mention|mentioning|to remember) that)[ \t]+(\w)");

    static readonly (Regex Rx, string To)[] Rules =
    {
        (new(@"\bin order to\b", RegexOptions.IgnoreCase), "to"),
        (new(@"\bdue to the fact that\b", RegexOptions.IgnoreCase), "because"),
        (new(@"\bat this point in time\b", RegexOptions.IgnoreCase), "now"),
        (new(@"\ba (?:wide|broad|diverse) (?:range|variety|array) of\b", RegexOptions.IgnoreCase), "many"),
        (new(@"\bplays? a (?:crucial|pivotal|vital|key|significant) role in\b", RegexOptions.IgnoreCase), "matters for"),
        (new(@"\bdelv(e|es|ed|ing) into\b", RegexOptions.IgnoreCase), "look$1 at"),
        (new(@"\butili[sz](e|es|ed)\b", RegexOptions.IgnoreCase), "us$1"),
        (new(@"\butili[sz]ing\b", RegexOptions.IgnoreCase), "using"),
        (new(@"\bleverag(e|es|ed)\b", RegexOptions.IgnoreCase), "us$1"),
        (new(@"\bleveraging\b", RegexOptions.IgnoreCase), "using"),
        (new(@"\bfacilitat(e|es|ed)\b", RegexOptions.IgnoreCase), "help$1"),
        (new(@"\bcommenc(e|es|ed)\b", RegexOptions.IgnoreCase), "start$1"),
        (new(@"\bnumerous\b", RegexOptions.IgnoreCase), "many"),
        (new(@"\bpivotal\b", RegexOptions.IgnoreCase), "important"),
        (new(@"\bmultifaceted\b", RegexOptions.IgnoreCase), "complex"),
        (new(@"\s*[—–]\s*(?=\w)", RegexOptions.None), ", "),          // em/en dash between words -> comma
        (new(@"[“”]", RegexOptions.None), "\""),
        (new(@"[‘’]", RegexOptions.None), "'"),
        (new(@"…", RegexOptions.None), "..."),
        (new(@"\*\*(.+?)\*\*", RegexOptions.None), "$1"),             // markdown bold in prose
        (new(@"(?m)^#{1,6}\s+", RegexOptions.None), ""),
        (new(@"[ \t]{2,}", RegexOptions.None), " "),
        (new(@",\s*,", RegexOptions.None), ","),
    };

    // "looke at" / "usees" from the suffix rules above.
    static readonly (Regex Rx, string To)[] Fixups =
    {
        (new(@"\blooke at\b"), "look at"), (new(@"\blookes at\b"), "looks at"), (new(@"\blooking at\b"), "looking at"),
        (new(@"\buse(es|ed)\b"), "us$1"), (new(@"\bUse(es|ed)\b"), "Us$1"), (new(@"\bhelpe\b"), "help"), (new(@"\bhelpes\b"), "helps"), (new(@"\bstarte\b"), "start"), (new(@"\bstartes\b"), "starts"),
        (new(@"\blooking into\b"), "looking into"),
    };

    public static string Clean(string text, out int changes)
    {
        changes = 0;
        if (string.IsNullOrEmpty(text)) return text;
        string t = text;
        // Stock openers go, and the word that now starts the sentence gets the capital.
        int cut = 0;
        for (int pass = 0; pass < 3; pass++)   // "Moreover, it is important to note that students..." loses both
        {
            int before = cut;
            t = Openers.Replace(t, m => { cut++; return m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant(); });
            if (cut == before) break;
        }
        changes += cut;
        foreach (var (rx, to) in Rules)
        {
            int n = rx.Matches(t).Count;
            if (n == 0) continue;
            // whitespace and quote tidy-ups are not worth counting
            if (to != " " && to != "\"" && to != "'" && to != ",") changes += n;
            t = rx.Replace(t, to);
        }
        t = Regex.Replace(t, @"\blookeing at\b", "looking at");
        t = Regex.Replace(t, @"\blook(ing) at\b", "look$1 at");
        foreach (var (rx, to) in Fixups) t = rx.Replace(t, to);
        return t;
    }
}
