using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VowenLite;

// What turns Luna into a personal assistant: things Codex can ask the daemon to do (its sandbox can't), plus memory.
//   REMIND / PIN / COPY / TYPE / SAY / REMEMBER / FORGET lines in a Codex reply run here, outside the sandbox, and are
//   stripped from what the user sees. Reminders survive restarts (reminders.json); remembered facts (memory.txt) are
//   added to every Luna prompt so it knows who you are.
static class Assistant
{
    // ---------------- prompt text

    public const string HookHelp =
        "Hooks: you can ask the host to do things your sandbox can't, by putting one line in your reply per action (uppercase keyword, then a colon). " +
        "The host runs them and hides those lines from the user.\n" +
        "REMIND: yyyy-MM-dd HH:mm | text   a reminder at that local time (work it out from \"Time now\"; or \"REMIND: in 30 minutes | text\")\n" +
        "PIN: text   pin a short note on the user's screen\n" +
        "COPY: text   put text on the clipboard (several lines: write \"COPY:\" alone, then the lines, then a line saying END)\n" +
        "TYPE: text   type text into the window the user is in (same multi-line form); only when they ask you to write or type something for them\n" +
        "SAY: text   speak out loud; only when they ask you to read or say something aloud\n" +
        "REMEMBER: fact   save a lasting fact or preference about the user (name, school, classes, how they like answers) for future chats; only things worth keeping\n" +
        "GIT: project folder name | pull   run git pull (fast-forward only), merge (pull + merge, may leave conflicts), fetch, status or log in one of the user's repos (use this instead of running those yourself: your sandbox has no GitHub login). It runs after you finish and you never see its output, so put it last, after your local work, and tell the user what it will do\n" +
        "PLAN: sentence   change the user's planner (tasks + calendar, synced to their phone through Google). Write the sentence the way the user would say it to the planner, one action per line, with real dates (yyyy-MM-dd) and 24 h times: " +
        "\"add event Dentist 2026-11-12 15:00 for 1 hour every 2 weeks\", \"add task Finish lab report 2026-11-14 in school list high priority\", \"remind me 2026-11-10 08:00 to call mom\", " +
        "\"add event Gym every monday wednesday friday at 18:00 until 2026-12-18\", \"mark lab report done\", \"move dentist to 2026-11-14 15:30\", \"delete event dentist\", \"skip standup tomorrow\", \"make piano repeat every week\". " +
        "Repeats: every day / every weekday / every week / every other week / every monday and thursday / every month / every year, optionally \"until <date>\" or \"for 6 weeks\". Use it instead of only describing what you would add\n" +
        "FORGET: words   delete remembered facts containing those words (FORGET: everything clears all of them)\n" +
        "After using a hook say nothing extra unless there is something useful to add.\n\n";

    // The hook list only goes into the prompt when the request could use one (saves tokens on plain questions).
    static readonly Regex HookWords = new(@"\b(remind\w*|alarm|tomorrow|tonight|later|schedule|type|write|complete|finish|fill|continue|answer|draft|reply|compose|copy|clipboard|say|read|aloud|speak|pin|note|remember|forget|git|pull|fetch|repo|repository|clone|call me|plan|planner|calendar|agenda|events?|meetings?|appointments?|tasks?|to-?dos?|due|deadline|weekly|biweekly|monthly|recurring|repeat\w*|every|my name|i'?m|i am|i like|i prefer|i have|my (?:class|school|teacher|favou?rite)\w*)\b", RegexOptions.IgnoreCase);
    public static string HookHelpFor(string request) => HookWords.IsMatch(request) ? HookHelp : "";

    public static string MemoryBlock()
    {
        var facts = Facts();
        if (facts.Count == 0) return "";
        return "What you remember about the user (from earlier chats; use it naturally, don't recite it):\n" + string.Join("\n", facts.TakeLast(25).Select(f => "- " + (f.Length > 160 ? f.Substring(0, 160) : f))) + "\n\n";
    }

    // ---------------- memory

    static string MemFile => Path.Combine(Paths.DataDir, "memory.txt");
    static readonly object memLock = new();

    public static List<string> Facts()
    {
        lock (memLock)
        {
            try { return File.Exists(MemFile) ? File.ReadAllLines(MemFile).Select(x => x.Trim()).Where(x => x.Length > 0).ToList() : new(); }
            catch { return new(); }
        }
    }

    static void SaveFacts(List<string> f) { lock (memLock) { try { File.WriteAllLines(MemFile, f); } catch { } } }

    static bool Remember(string fact)
    {
        fact = Regex.Replace(fact.Trim().TrimEnd('.'), @"\s+", " ");
        if (fact.Length < 3) return false;
        if (fact.Length > 200) fact = fact.Substring(0, 200);
        var f = Facts();
        if (f.Any(x => x.Equals(fact, StringComparison.OrdinalIgnoreCase))) return true;
        f.Add(fact);
        while (f.Count > 40) f.RemoveAt(0);
        SaveFacts(f);
        return true;
    }

    static int Forget(string words)
    {
        var f = Facts();
        int before = f.Count;
        if (Regex.IsMatch(words.Trim(), @"^(everything|all|it all)$", RegexOptions.IgnoreCase)) f.Clear();
        else
        {
            var keys = Regex.Split(words.ToLowerInvariant(), @"\W+").Where(w => w.Length > 2).ToList();
            if (keys.Count == 0) return 0;
            f.RemoveAll(x => keys.All(k => x.ToLowerInvariant().Contains(k)));
        }
        SaveFacts(f);
        return before - f.Count;
    }

    // ---------------- free spoken commands (no model, no tokens)

    const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static bool Is(string l, string pattern, out Match m) { m = Regex.Match(l, pattern, IC); return m.Success; }

    public static string TryLocal(string l)
    {
        Match m;

        // ---- git on your own repos, free: "git pull my-app", "pull the my-app repo", "fetch latenta clone", "git status of my-app"
        if (Is(l, @"^(?:please\s+)?(?:do\s+a\s+)?(?:git\s+)?(pull|fetch)(?:\s+down)?(?:\s+(?:the\s+)?latest(?:\s+changes)?)?\s+(?:(?:in|for|on|from|of|into)\s+)?(?:the\s+|my\s+)?(.+?)(?:\s+(?:repo|repository|project|folder))?$", out var gm)
            || Is(l, @"^(?:(?:check|show)\s+(?:the\s+)?)?git\s+(status|log)(?:\s+(?:of|for|in|on))?\s+(?:the\s+|my\s+)?(.+?)(?:\s+(?:repo|repository|project|folder))?$", out gm))
        {
            string gres = Git(gm.Groups[2].Value, gm.Groups[1].Value.ToLowerInvariant(), out string gproblem, quietIfUnknown: true);
            if (gproblem != null) return gproblem == "" ? null : gproblem;   // "" = not one of your repos: let the rest of the router try
            return (gres.Contains('\n') ? gres : "✓ " + gres);
        }

        string pj = Projects.TryLocal(l);
        if (pj != null) return pj;

        // ---- memory
        if (Is(l, @"^(?:what|tell me what)(?: do you| you)? (?:remember|know)(?: about me)?$|^show (?:me )?(?:my )?(?:memory|memories)$", out _))
        {
            var f = Facts();
            return f.Count == 0 ? "✓ I don't remember anything yet" : string.Join("\n", f.Select(x => "• " + x));
        }
        if (Is(l, @"^forget (?:everything|all|it all)(?: you (?:know|remember))?(?: about me)?$|^forget (?:about |that )?(.+)$", out m) && !Is(l, @"\b(timer|alarm|reminder|note)s?\b", out _))
        {
            string what = m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : "everything";
            if (Luna.DryRun) return "DRY RUN: forget " + what;
            int n = Forget(what);
            return n > 0 ? $"✓ Forgot {n} thing{(n == 1 ? "" : "s")}" : "✓ Nothing to forget";
        }

        // ---- saved reminders
        if (Is(l, @"^(?:what|which)(?: are| do i have)?(?: my)?(?: upcoming| saved)? reminders(?: do i have| are there)?$|^(?:list|show)(?: me)?(?: my| the| all)?(?: upcoming| saved)? reminders$|^do i have (?:any )?reminders$", out _))
        {
            var list = LoadRem().OrderBy(x => x.Due).ToList();
            return list.Count == 0 ? "✓ No reminders saved" : string.Join("\n", list.Take(8).Select(r => $"• {Pretty(r.Due)} · {r.Text}"));
        }
        if (Is(l, @"^(?:cancel|clear|delete|remove)(?: all| my| the)?(?: saved| upcoming)? reminders$", out _))
        {
            if (Luna.DryRun) return "DRY RUN: clear reminders";
            int n = ClearReminders();
            return n > 0 ? $"✓ Cleared {n} reminder{(n == 1 ? "" : "s")}" : "✓ No reminders saved";
        }

        // ---- briefing
        if (Is(l, @"^(?:good (?:morning|afternoon|evening)|(?:give me |what(?:'s| is) )?(?:my )?(?:daily )?(?:briefing|brief)|brief me|what(?:'s| is) (?:my day|today) like|what do i have (?:today|on today|going on|coming up)|what(?:'s| is) (?:going on|on) today)$", out _))
            return Briefing();

        // ---- clipboard
        if (Is(l, @"^read(?: my| the)? clipboard (?:out loud|aloud)$", out _)) { string t = Luna.ClipboardText(); if (t.Length == 0) return "✓ Clipboard is empty"; if (!Luna.DryRun) Speak(t); return ""; }
        if (Is(l, @"^(?:read|show|what(?:'s| is) (?:on|in))(?: me)?(?: my| the)? clipboard$", out _)) { string t = Luna.ClipboardText(); return t.Length == 0 ? "✓ Clipboard is empty" : t; }
        if (Is(l, @"^clear(?: my| the)? clipboard$", out _)) { if (!Luna.DryRun) SetClipboard(""); return "✓ Clipboard cleared"; }
        if (Is(l, @"^paste(?: it| that)? (?:as )?(?:plain(?: text)?|text only|without (?:the )?formatting)$", out _))
        {
            string t = Luna.ClipboardText();
            if (t.Length == 0) return "✓ Nothing to paste";
            if (Luna.DryRun) return "DRY RUN: paste plain text";
            SetClipboard(t);
            Task.Run(async () => { await Task.Delay(120); Native.Combo(0x11, 0x56); });
            return "";
        }

        // ---- last answer: copy it, read it out loud, stop reading
        if (Is(l, @"^copy(?: that| the| your| last)?(?: last)? (?:answer|response|reply)$", out _))
        {
            if (string.IsNullOrEmpty(Luna.LastShown)) return "✓ No answer to copy yet";
            if (!Luna.DryRun) SetClipboard(Luna.LastShown);
            return "✓ Answer copied";
        }
        if (Is(l, @"^read(?: me)?(?: that| the| your| last)?(?: last)? (?:answer|response|reply)(?: out loud| aloud| to me)?$|^read (?:that|it) (?:out loud|aloud)$|^(?:say|read) (?:that|it) again$", out _))
        {
            if (string.IsNullOrEmpty(Luna.LastShown)) return "✓ Nothing to read yet";
            if (!Luna.DryRun) Speak(Luna.LastShown);
            return "";
        }
        if (Is(l, @"^(?:stop|quit) (?:talking|reading|speaking)$|^(?:be quiet|shut up)$", out _)) { StopSpeaking(); return ""; }

        // ---- unit conversion
        string conv = Convert(Commands.WordsToDigits(l));
        if (conv != null) return conv;
        return null;
    }

    static string Pretty(DateTime due) =>
        due.Date == DateTime.Today ? due.ToString("h:mm tt") : due.Date == DateTime.Today.AddDays(1) ? "tomorrow " + due.ToString("h:mm tt")
        : due < DateTime.Today.AddDays(6) ? due.ToString("ddd h:mm tt") : due.ToString("MMM d h:mm tt");

    static string Briefing()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(DateTime.Now.ToString("dddd, MMMM d")).Append(" · ").Append(DateTime.Now.ToString("h:mm tt")).Append('\n');
        var rem = LoadRem().OrderBy(x => x.Due).ToList();
        var soon = rem.Where(r => r.Due < DateTime.Now.AddHours(24)).Take(4).ToList();
        if (soon.Count > 0) { sb.Append("\nNext 24 hours\n"); foreach (var r in soon) sb.Append("• ").Append(Pretty(r.Due)).Append(" · ").Append(r.Text).Append('\n'); }
        else sb.Append("\nNo reminders in the next 24 hours").Append(rem.Count > 0 ? $" ({rem.Count} later)" : "").Append('\n');
        sb.Append(Planner.Briefing());
        int timers = Commands.Running().Count;
        if (timers > 0) sb.Append($"\n{timers} timer{(timers == 1 ? "" : "s")} running\n");
        string notes = Cards.Try("what are my notes") ?? "";
        if (notes.Length > 0 && !notes.StartsWith("✓")) sb.Append("\nPinned notes\n").Append(notes).Append('\n');
        string bat = Commands.Battery();
        if (!bat.Contains("No battery")) sb.Append('\n').Append(bat.TrimStart('✓', ' ')).Append('\n');
        var facts = Facts();
        if (facts.Count > 0) sb.Append($"\nI remember {facts.Count} thing{(facts.Count == 1 ? "" : "s")} about you.");
        return sb.ToString().Trim();
    }

    // ---- unit conversion ("convert 5 miles to km", "72 f in c", "how many cups in a litre")

    static readonly (string Cat, double F, string Names)[] UnitDefs =
    {
        ("len", 0.001, "mm millimeter millimeters millimetre millimetres"), ("len", 0.01, "cm centimeter centimeters centimetre centimetres"),
        ("len", 1, "m meter meters metre metres"), ("len", 1000, "km kilometer kilometers kilometre kilometres"), ("len", 0.0254, "in inch inches"),
        ("len", 0.3048, "ft foot feet"), ("len", 0.9144, "yd yard yards"), ("len", 1609.344, "mi mile miles"),
        ("mass", 1e-6, "mg milligram milligrams"), ("mass", 0.001, "g gram grams"), ("mass", 1, "kg kilogram kilograms kilo kilos"),
        ("mass", 0.028349523, "oz ounce ounces"), ("mass", 0.45359237, "lb lbs pound pounds"), ("mass", 6.35029318, "stone stones"),
        ("vol", 0.001, "ml milliliter milliliters millilitre millilitres"), ("vol", 1, "l liter liters litre litres"), ("vol", 3.785411784, "gal gallon gallons"),
        ("vol", 0.946352946, "qt quart quarts"), ("vol", 0.473176473, "pint pints"), ("vol", 0.2365882365, "cup cups"),
        ("vol", 0.0147867648, "tbsp tablespoon tablespoons"), ("vol", 0.00492892159, "tsp teaspoon teaspoons"),
        ("spd", 0.44704, "mph"), ("spd", 0.277777778, "kph kmh kmph"), ("spd", 1, "mps"), ("spd", 0.514444, "knot knots"),
        ("data", 1, "byte bytes"), ("data", 1e3, "kb kilobyte kilobytes"), ("data", 1e6, "mb megabyte megabytes"), ("data", 1e9, "gb gigabyte gigabytes"), ("data", 1e12, "tb terabyte terabytes"),
        ("time", 1, "sec secs second seconds"), ("time", 60, "min mins minute minutes"), ("time", 3600, "hr hrs hour hours"), ("time", 86400, "day days"), ("time", 604800, "week weeks"),
        ("temp", 0, "c celsius centigrade"), ("temp", 0, "f fahrenheit"), ("temp", 0, "kelvin"),
    };
    static Dictionary<string, (string Cat, double F, string Canon)> unitMap;

    static (string Cat, double F, string Canon)? Unit(string s)
    {
        if (unitMap == null)
        {
            unitMap = new();
            foreach (var d in UnitDefs) { var names = d.Names.Split(' '); foreach (var n in names) unitMap[n] = (d.Cat, d.F, names[0]); }
        }
        s = s.Trim().ToLowerInvariant().Replace("°", "").Replace("km/h", "kmh").Replace("m/s", "mps");
        s = Regex.Replace(s, @"^(?:degrees?|an?|the|one)\s+|\s+degrees?$", "");
        return unitMap.TryGetValue(s, out var u) ? u : null;
    }

    static string Convert(string l)
    {
        const string num = @"(-?\d+(?:\.\d+)?)";
        const string word = @"([a-z°/]+(?: [a-z]+)?)";
        var m = Regex.Match(l, @"^(?:what(?:'s| is) |convert )?" + num + @"\s*" + word + @"\s+(?:to|in|into|as)\s+" + word + "$", IC);
        string a, b; double v;
        if (m.Success) { v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); a = m.Groups[2].Value; b = m.Groups[3].Value; }
        else
        {
            m = Regex.Match(l, @"^how many " + word + @" (?:are |is )?(?:in|there in) (?:an? |one )?" + @"(?:" + num + @"\s*)?" + word + "$", IC);
            if (!m.Success) return null;
            v = m.Groups[2].Success ? double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
            b = m.Groups[1].Value; a = m.Groups[3].Value;
        }
        var from = Unit(a); var to = Unit(b);
        if (from == null || to == null || from.Value.Cat != to.Value.Cat) return null;
        double r;
        if (from.Value.Cat == "temp")
        {
            double c = from.Value.Canon switch { "f" => (v - 32) * 5 / 9, "kelvin" => v - 273.15, _ => v };
            r = to.Value.Canon switch { "f" => c * 9 / 5 + 32, "kelvin" => c + 273.15, _ => c };
        }
        else r = v * from.Value.F / to.Value.F;
        string Fmt(double x) => Math.Abs(x) >= 1e7 || (x != 0 && Math.Abs(x) < 1e-3) ? x.ToString("G4", CultureInfo.InvariantCulture) : Math.Round(x, 4).ToString("0.####", CultureInfo.InvariantCulture);
        return $"✓ {Fmt(v)} {from.Value.Canon} = {Fmt(r)} {to.Value.Canon}";
    }

    static int ClearReminders()
    {
        List<Saved> list;
        lock (remLock) { list = LoadRem(); SaveRem(new()); }
        string[] ids; lock (armed) ids = armed.ToArray();
        foreach (var id in ids) Commands.CancelTimer(id);
        lock (armed) armed.Clear();
        return list.Count;
    }

    // A reminder card was dismissed (x / right-click): don't bring it back after a restart.
    public static void Cancelled(string id)
    {
        bool mine; lock (armed) mine = armed.Remove(id);
        if (!mine) return;
        lock (remLock) { var l = LoadRem(); l.RemoveAll(x => x.Id == id); SaveRem(l); }
    }

    // ---------------- reminders that survive restarts

    sealed class Saved { public string Id { get; set; } public DateTime Due { get; set; } public string Text { get; set; } }
    static string RemFile => Path.Combine(Paths.DataDir, "reminders.json");
    static readonly object remLock = new();

    static List<Saved> LoadRem()
    {
        try { return File.Exists(RemFile) ? JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(RemFile)) ?? new() : new(); }
        catch { return new(); }
    }
    static void SaveRem(List<Saved> l) { try { File.WriteAllText(RemFile, JsonSerializer.Serialize(l)); } catch { } }

    static readonly HashSet<string> armed = new();
    static System.Threading.Timer refresh;   // field: an unreferenced Timer can be collected

    static void Arm(Saved r)
    {
        var span = r.Due - DateTime.Now;
        if (span > TimeSpan.FromDays(20)) return;   // System.Threading.Timer tops out at ~49 days; Restore() runs again every 12 h and picks it up
        lock (armed) if (!armed.Add(r.Id)) return;
        if (span < TimeSpan.FromSeconds(3)) span = TimeSpan.FromSeconds(3);
        string label = r.Text.Length > 30 ? r.Text.Substring(0, 29) + "…" : r.Text;
        Commands.Schedule(span, label, "Reminder", r.Text, id: r.Id, then: () =>
        {
            lock (armed) armed.Remove(r.Id);
            lock (remLock) { var l = LoadRem(); l.RemoveAll(x => x.Id == r.Id); SaveRem(l); }
        });
    }

    // Daemon start: put back the reminders that were waiting (overdue ones ring a few seconds after login).
    public static void Restore()
    {
        try { lock (remLock) foreach (var r in LoadRem()) Arm(r); } catch { }
        refresh ??= new System.Threading.Timer(_ => Restore(), null, TimeSpan.FromHours(12), TimeSpan.FromHours(12));
    }

    static string AddReminder(string when, string text, out string problem)
    {
        problem = null;
        DateTime due;
        var rel = Regex.Match(when.Trim(), @"^(?:in|\+)\s*(\d+(?:\.\d+)?)\s*(s|sec\w*|m|min\w*|h|hr\w*|hours?|d|days?)$", RegexOptions.IgnoreCase);
        if (rel.Success)
        {
            double n = double.Parse(rel.Groups[1].Value, CultureInfo.InvariantCulture);
            char u = char.ToLowerInvariant(rel.Groups[2].Value[0]);
            due = DateTime.Now + (u == 's' ? TimeSpan.FromSeconds(n) : u == 'h' ? TimeSpan.FromHours(n) : u == 'd' ? TimeSpan.FromDays(n) : TimeSpan.FromMinutes(n));
        }
        else if (!DateTime.TryParse(when.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out due))
        {
            problem = $"I couldn't read the reminder time “{when}”.";
            return null;
        }
        if (due < DateTime.Now.AddSeconds(-30)) { problem = "That reminder time has already passed."; return null; }
        text = text.Trim().TrimEnd('.');
        if (text.Length == 0) text = "Reminder";
        var r = new Saved { Id = Guid.NewGuid().ToString("N"), Due = due, Text = text };
        if (!Luna.DryRun) { lock (remLock) { var l = LoadRem(); l.Add(r); SaveRem(l); } Arm(r); }
        return Pretty(due);
    }

    // ---------------- the hooks

    static readonly Regex Hook = new(@"^\s*[-*>`]*\s*(REMIND|PIN|COPY|TYPE|SAY|REMEMBER|FORGET|PROJECT|GIT|PLAN)\s*:\s*(.*?)\s*`*\s*$");   // case-sensitive on purpose: prose like "Note:" must not match

    // Runs every hook line in a Codex reply; returns the rest of the reply and short confirmations ("Reminder set · 3:00 PM").
    public static string Process(string reply, out List<string> done)
    {
        done = new();
        var lines = reply.Replace("\r", "").Split('\n');
        var rest = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            var m = Hook.Match(lines[i]);
            if (!m.Success) { rest.Add(lines[i]); continue; }
            string kind = m.Groups[1].Value, arg = m.Groups[2].Value;
            if (arg.Length == 0 && (kind == "COPY" || kind == "TYPE" || kind == "SAY"))
            {
                // block form: lines until END
                var block = new List<string>();
                int j = i + 1;
                for (; j < lines.Length && lines[j].Trim().Trim('`') != "END"; j++) if (lines[j].Trim() != "```") block.Add(lines[j]);
                arg = string.Join("\n", block).Trim();
                i = j;
            }
            string note = Run(kind, arg, out string problem);
            if (problem != null) rest.Add(problem);
            else if (note != null) done.Add(note);
        }
        return string.Join("\n", rest).Trim();
    }

    static string Run(string kind, string arg, out string problem)
    {
        problem = null;
        string shown = arg.Length > 40 ? arg.Substring(0, 39) + "…" : arg;
        if (Luna.DryRun && kind != "REMIND") return "DRY RUN: " + kind + " " + arg.Replace("\n", " ⏎ ");
        try
        {
            switch (kind)
            {
                case "REMIND":
                {
                    int bar = arg.IndexOf('|');
                    if (bar < 0) { problem = "I couldn't read that reminder."; return null; }
                    string at = AddReminder(arg.Substring(0, bar), arg.Substring(bar + 1), out problem);
                    return at == null ? null : (Luna.DryRun ? "DRY RUN: " : "") + "Reminder set · " + at;
                }
                case "PLAN":
                {
                    string r = Planner.Hook(arg, out problem);
                    return problem != null ? null : r;
                }
                case "PIN": Cards.Try("pin a note " + arg); return "Pinned · " + shown;
                case "COPY": return SetClipboard(arg) ? "Copied to clipboard" : null;
                case "TYPE":
                    // let the pill/card settle and focus stay where the user was, then type
                    if (arg.Length > 160 && Settings.Load().Humanize) arg = Humanize.Clean(arg, out _);   // free tidy-up of the usual model tics
                    Task.Run(async () => { await Task.Delay(300); Native.TypeLines(arg); });
                    return "Typed it";
                case "SAY": Speak(arg); return null;
                case "PROJECT":
                {
                    int bar = arg.IndexOf('|');
                    if (bar > 0) Projects.AddNote(arg.Substring(0, bar), arg.Substring(bar + 1), Settings.Load());
                    return null;   // quiet: a saved project note needs no pill
                }
                case "GIT":
                {
                    int bar = arg.IndexOf('|');
                    string op = bar > 0 ? arg.Substring(bar + 1).Trim().ToLowerInvariant() : "pull";
                    string repo = bar > 0 ? arg.Substring(0, bar) : arg;
                    string res = Git(repo, op, out problem);
                    return problem != null ? null : res;
                }
                case "REMEMBER": return Remember(arg) ? "Remembered · " + shown : null;
                case "FORGET": { int n = Forget(arg); return n > 0 ? $"Forgot {n} thing{(n == 1 ? "" : "s")}" : null; }
            }
        }
        catch (Exception ex) { problem = $"{kind} didn't work: {ex.Message}"; }
        return null;
    }

    // ---------------- git, run here (outside Codex's sandbox, which has no GitHub credentials) on the user's own repos

    // op: pull (fast-forward only), fetch, status, log. Returns a one-line (or short multi-line) result; problem = message to show, "" = no such repo.
    static string Git(string repoPhrase, string op, out string problem, bool quietIfUnknown = false)
    {
        problem = null;
        var s = Settings.Load();
        string dir = Projects.ResolveDir(repoPhrase, s) ?? Folders.Resolve(repoPhrase, s, out _);
        if (dir == null || !Directory.Exists(Path.Combine(dir, ".git")))
        {
            problem = quietIfUnknown ? "" : $"I couldn't find a git repo called \"{repoPhrase}\".";
            return null;
        }
        string name = Path.GetFileName(dir.TrimEnd('\\'));
        string[] args = op switch
        {
            "pull" => new[] { "pull", "--ff-only" },
            "fetch" => new[] { "fetch", "--prune" },
            "status" => new[] { "status", "-sb" },
            "log" => new[] { "log", "--oneline", "-5" },
            "merge" => new[] { "pull", "--no-rebase", "--no-edit" },   // fetch + merge the upstream branch; conflicts stay in the working tree
            _ => null,
        };
        if (args == null) { problem = $"I only run git pull, merge, fetch, status or log (not \"{op}\")."; return null; }
        if (Luna.DryRun) return $"DRY RUN: git {string.Join(' ', args)} in {dir}";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var p = System.Diagnostics.Process.Start(psi);
            var outT = p.StandardOutput.ReadToEndAsync(); var errT = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(90_000)) { try { p.Kill(true); } catch { } problem = $"git {args[0]} in {name} took too long, so I stopped it."; return null; }
            string o = outT.Result.Trim(), e = errT.Result.Trim();
            if (op == "merge" && p.ExitCode != 0 && o.Contains("CONFLICT"))
            {
                var files = Regex.Matches(o, @"CONFLICT[^\n]*?in (\S+)").Select(m => m.Groups[1].Value).Distinct().ToList();
                string conflict = name + ": merge stopped on conflicts in " + (files.Count > 0 ? string.Join(", ", files) : "some files") + ". Say \"resolve the conflicts\" and I'll fix them.";
                Projects.NoteGit(dir, op, conflict);
                return conflict;
            }
            if (p.ExitCode != 0) { problem = $"git {args[0]} in {name} failed:\n{(e.Length > 0 ? e : o)}"; return null; }
            string gitResult = op switch
            {
                "merge" => name + ": " + (o.Contains("Already up to date") ? "already up to date" : "merged · " + (Regex.Match(o, @"\d+ files? changed[^\n]*").Success ? Regex.Match(o, @"\d+ files? changed[^\n]*").Value : "done")),
                "pull" => name + ": " + (o.Contains("Already up to date") ? "already up to date" : Regex.Match(o, @"\d+ files? changed[^\n]*").Success ? "pulled · " + Regex.Match(o, @"\d+ files? changed[^\n]*").Value : "pulled"),
                "fetch" => name + ": " + (e.Length + o.Length == 0 ? "nothing new" : "fetched new commits"),
                "status" => name + "\n" + o,
                _ => name + "\n" + o,
            };
            Projects.NoteGit(dir, op, gitResult);   // remembered as a project fact ("last git: merged · 3 files changed")
            return gitResult;
        }
        catch (Exception ex) { problem = "git didn't run: " + ex.Message; return null; }
    }

    // ---------------- clipboard + speech (Win32 / SAPI, no WinForms in the daemon)

    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr mem);

    internal static bool SetClipboard(string text)
    {
        for (int tries = 0; tries < 10; tries++)
        {
            if (!OpenClipboard(IntPtr.Zero)) { Thread.Sleep(40); continue; }
            try
            {
                EmptyClipboard();
                byte[] bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
                IntPtr mem = GlobalAlloc(0x0002 /* GMEM_MOVEABLE */, (UIntPtr)bytes.Length);
                IntPtr p = GlobalLock(mem);
                Marshal.Copy(bytes, 0, p, bytes.Length);
                GlobalUnlock(mem);
                return SetClipboardData(13 /* CF_UNICODETEXT */, mem) != IntPtr.Zero;
            }
            finally { CloseClipboard(); }
        }
        return false;
    }

    // Speech runs in a short-lived child (`VowenLite.exe --say <file>`): SAPI + the dynamic binder cost ~40 MB
    // of working set and never unload, so the always-on daemon must not host them.
    static System.Diagnostics.Process sayProc;
    public static int SayPid { get { try { return sayProc?.Id ?? -1; } catch { return -1; } } }
    public static void StopSpeaking()
    {
        var p = sayProc; sayProc = null;
        try { if (p != null && !p.HasExited) p.Kill(); } catch { }
        try { p?.Dispose(); } catch { }
    }

    public static void Speak(string text)
    {
        if (text.Length > 1500) text = text.Substring(0, 1500);
        StopSpeaking();
        try
        {
            string file = Path.Combine(Path.GetTempPath(), "vowenlite-say-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(file, text);
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath, "--say \"" + file + "\"") { UseShellExecute = false, CreateNoWindow = true });
            if (p == null) return;
            Jobs.Add(p);
            sayProc = p;
        }
        catch { }
    }

    // The --say child: speaks the text in the file, deletes it, exits.
    public static int SayMain(string file)
    {
        string text = "";
        try { text = File.ReadAllText(file); File.Delete(file); } catch { }
        if (text.Length == 0) return 0;
        var t = new Thread(() =>
        {
            try
            {
                var type = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (type == null) return;
                object voice = Activator.CreateInstance(type);
                type.InvokeMember("Speak", System.Reflection.BindingFlags.InvokeMethod, null, voice, new object[] { text, 0 });   // 0 = synchronous
            }
            catch { }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return 0;
    }
}
