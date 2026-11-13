using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace VowenLite;

// "Luna, <request>" spoken while holding Right Shift: the request goes to a small Codex model
// (gpt-6-luna, low reasoning, your ChatGPT login) instead of being typed. Nothing listens in the
// background; this only ever runs on text Right Shift dictation already produced. Runs inside the
// daemon with no WinForms; an answer, if there is one, opens in a separate short-lived window
// (`VowenLite.exe --answer <file>`) so the daemon stays small.
static class Luna
{
    static readonly Regex NeedsContext = new(@"\b(this|that|these|those|selected|selection|clipboard|copied|paste|above|here|screen)\b", RegexOptions.IgnoreCase);
    static int running;

    // "Luna, open spotify" -> "Open spotify". Speech-to-text hears the name many ways, so the first word may be
    // any near-spelling of "luna". Looser spellings only count with a comma/colon after them or a command word next
    // ("Lunar, open X", "Loona set a timer"), so ordinary sentences starting with "Lunar eclipse..." still type normally.
    static readonly HashSet<string> Variants = new(StringComparer.OrdinalIgnoreCase)
        { "luna", "lunar", "lunah", "lunna", "lunaa", "lunnah", "loona", "looner", "luner", "lona", "lune", "lunas", "luma", "lunes", "lunah", "leuna", "luna's", "lunar's", "looney", "luny", "loonah", "lunu", "lunna", "lunnar" };
    static readonly HashSet<string> CommandWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "launch", "start", "run", "set", "turn", "what", "whats", "what's", "who", "when", "where", "how", "why", "search", "google", "look", "play", "pause", "resume",
        "mute", "unmute", "volume", "take", "lock", "close", "show", "switch", "go", "remind", "timer", "cancel", "snap", "maximize", "minimize", "toggle", "dark", "light", "next",
        "skip", "previous", "copy", "paste", "undo", "redo", "scroll", "refresh", "reload", "new", "find", "tell", "can", "could", "please", "make", "give", "bring", "pull", "fire",
        "stop", "save", "select", "cd", "type", "write", "check", "list", "read", "is", "are", "do", "does", "calculate", "convert", "translate", "define", "explain", "summarize",
    };

    // Model picking by voice: "Sol, explain X" / "Terra, fix the build" / "Luna, use sol to ..." / (Luna key held) "use terra to ...".
    // Luna is the default; Sol and Terra are the bigger models. The name must be followed by punctuation (or come after use/ask/with)
    // so ordinary words (sole, terra firma) are not taken as a model switch. Newest installed version of the family is used.
    static readonly Regex ModelPick = new(@"^\W*(?:(?:hey|hi|hello|ok|okay|yo)\W+)*(?:(?<lead>use|ask|using|with|try|switch(?:ing)?\s+to|swap(?:ping)?\s+to|change\s+to|go\s+with|go\s+to)\s+)?(?<n>[a-z]+)\b(?<p>[\s]*[,:.!?–—\-]*\s*)(?:(?:to|for|and|please)\s+)?(?<rest>.+)$", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    // "swap to Terra" / "switch to sol" / "back to Luna" with nothing after = change the default model until switched again.
    static readonly Regex ModelSwitch = new(@"^\W*(?:(?:hey|hi|hello|ok|okay|yo|luna)\W+)*(?:please\s+)?(?:(?:swap|switch|change|go)(?:ping|ing)?(?:\s+(?:over|back))?\s+to|back\s+to|stay\s+on|stick\s+(?:with|to))\s+(?:the\s+)?(?<n>[a-z]+)(?:\s+model)?\W*$", RegexOptions.IgnoreCase);
    internal static string activeModel;   // set per request by Start/RunBlocking; null = Settings.LunaModel
    internal static string activeModelName = "Luna";

    // Spoken name -> model family ("luna" | "sol" | "terra" | null). Right Shift dictation only accepts the strict spellings so a
    // name like "Cara, can you..." still types; with the Luna key held or "switch to ..." in front, whisper's usual mis-hearings count too.
    static string Family(string n, bool loose)
    {
        n = n.ToLowerInvariant();
        switch (n)
        {
            case "luna": return "luna";
            case "sol": case "soul": case "sole": return "sol";
            case "terra": case "tera": case "terrah": return "terra";
        }
        if (!loose) return null;
        switch (n)
        {
            case "tara": case "cara": case "kara": case "tira": case "tierra": case "terror": case "tiara": case "tarra": case "terri": case "terrace": case "tear": return "terra";
            case "sal": case "sul": case "sull": case "soll": case "sold": case "ssoul": return "sol";
        }
        return Variants.Contains(n) ? "luna" : null;
    }

    static bool ParseModel(string text, bool held, out string family, out string rest, out bool lead)
    {
        family = null; rest = ""; lead = false;
        var m = ModelPick.Match(text);
        if (!m.Success) return false;
        lead = m.Groups["lead"].Success;
        bool punct = m.Groups["p"].Value.IndexOfAny(new[] { ',', ':', '.', '!', '?', '–', '—', '-' }) >= 0;
        family = Family(m.Groups["n"].Value, held || lead);
        rest = m.Groups["rest"].Value.Trim();
        return family != null && rest.Length >= 2 && (lead || punct);
    }

    static bool IsModelLead(string text) => ParseModel(text, false, out _, out _, out _);

    internal static string SwitchTarget(string text)
    {
        var m = ModelSwitch.Match(text);
        return m.Success ? Family(m.Groups["n"].Value, true) : null;
    }

    // Strips a leading model name from the request and returns the model slug to use (or null = default).
    internal static string PickModel(ref string request, Settings s, out string name, bool held = false)
    {
        name = "Luna";
        if (!ParseModel(request, held, out string family, out string rest, out _)) return null;
        request = char.ToUpperInvariant(rest[0]) + rest.Substring(1);
        name = char.ToUpperInvariant(family[0]) + family.Substring(1);
        return ModelFor(family, s);
    }

    // No name said: the sticky model from "swap to Terra" (Settings.StickyModel), else null = Luna.
    static string StickyModel(Settings s, out string name)
    {
        name = "Luna";
        string f = (s.StickyModel ?? "").Trim().ToLowerInvariant();
        if (f != "sol" && f != "terra") return null;
        name = char.ToUpperInvariant(f[0]) + f.Substring(1);
        return ModelFor(f, s);
    }

    internal static string ModelSlug(string family, Settings s) => ModelFor(family, s);

    static string ModelFor(string family, Settings s)
    {
        if (family == "luna") return s.LunaModel;
        string pinned = family == "sol" ? s.SolModel : s.TerraModel;
        if (!string.IsNullOrWhiteSpace(pinned)) return pinned.Trim();
        // newest installed version of the family from the Codex model list (gpt-6.1-sol beats gpt-6-sol)
        try
        {
            string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "models_cache.json");
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cache));
            Version best = null; string slug = null;
            foreach (var mo in doc.RootElement.GetProperty("models").EnumerateArray())
            {
                string sl = mo.GetProperty("slug").GetString() ?? "";
                var vm = Regex.Match(sl, @"^gpt-(\d+(?:\.\d+)?)-" + family + "$");
                if (!vm.Success) continue;
                var v = Version.Parse(vm.Groups[1].Value.Contains('.') ? vm.Groups[1].Value : vm.Groups[1].Value + ".0");
                if (best == null || v > best) { best = v; slug = sl; }
            }
            if (slug != null) return slug;
        }
        catch (Exception ex) { Log("model list read failed: " + ex.Message); }
        return family == "sol" ? "gpt-6-sol" : "gpt-6.1-terra";
    }

    public static bool TryParse(string text, out string request)
    {
        request = null;
        if (SwitchTarget(text) != null) { request = text.Trim(); return true; }   // "swap to Terra" works without the Luna word
        if (ParseModel(text, false, out string fam, out _, out bool hadLead) && !hadLead && fam != "luna") { request = text.Trim(); return true; }   // "Sol, ..." / "Terra, ..." work without the Luna key too
        var m = Regex.Match(text, @"^\W*(?:(?:hey|hi|hello|ok|okay|yo)\W+)*(?<w>[A-Za-z']+)(?<p>[\s,:.\-–—!?]*)(?<rest>.*)$", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        string w = m.Groups["w"].Value, rest = m.Groups["rest"].Value.Trim();
        bool hadHey = Regex.IsMatch(text, @"^\W*(?:hey|hi|hello|ok|okay|yo)\W", RegexOptions.IgnoreCase);
        bool punct = m.Groups["p"].Value.IndexOfAny(new[] { ',', ':', '.', '-', '–', '—', '!', '?' }) >= 0;
        string next = Regex.Match(rest, @"^[A-Za-z']+").Value;
        bool exact = w.Equals("luna", StringComparison.OrdinalIgnoreCase) || w.Equals("jarvis", StringComparison.OrdinalIgnoreCase);
        bool close = Variants.Contains(w) || (w.Length is >= 3 and <= 7 && Edit(w.ToLowerInvariant(), "luna") <= 1);
        if (!(exact || (close && (punct || hadHey || CommandWords.Contains(next))))) return false;
        if (rest.Length < 2) return false;
        request = char.ToUpperInvariant(rest[0]) + rest.Substring(1);
        return true;
    }

    internal static int Edit(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    // Starts the request in the background; `done` runs when it ends (to hide the overlay).
    public static void Start(string request, Settings s, Action done, bool held = false, string thread = null)
    {
        // Which conversation this belongs to: an explicit reply (thread id; "new" = start fresh), else the newest thread if it is recent (voice follow-ups).
        bool forced = !string.IsNullOrEmpty(thread) && thread != "new" && Threads.Get(thread) != null;
        string tid = thread == "new" ? Threads.NewId() : forced ? thread : (!string.IsNullOrEmpty(thread) ? thread : Threads.Newest(FollowWindow)?.Id ?? Threads.NewId());
        int doneFlag = 0;
        void DoneOnce() { if (Interlocked.Exchange(ref doneFlag, 1) == 0) done(); }
        string sw0 = SwitchTarget(request);
        if (sw0 != null)
        {
            // "swap to Terra": every request after this uses Terra until "swap to Luna".
            s.StickyModel = sw0 == "luna" ? "" : sw0;
            s.Save();
            string nm = char.ToUpperInvariant(sw0[0]) + sw0.Substring(1);
            Log($"model: default is now {nm} ({ModelFor(sw0, s)})");
            DoneOnce();
            Overlay.Notice("✓ Now using " + nm);
            return;
        }
        string pickedModel = PickModel(ref request, s, out string pickedName, held);   // "Sol, ..." / "Terra, ..." / "use sol to ..."
        if (pickedModel == null) pickedModel = StickyModel(s, out pickedName);
        if (pickedModel != null) Log($"model: {pickedName} ({pickedModel})");
        Task.Run(async () =>
        {
            // 1) Local commands never wait on Codex (a slow Codex run used to block "stop timer" and everything after it).
            string local = null;
            try { local = await TryLocal(request, s); }
            catch (Exception ex) { Log("router error: " + ex.Message); }
            if (local != null)
            {
                DoneOnce();
                if (string.IsNullOrWhiteSpace(local)) return;
                if (local.StartsWith("✓")) Overlay.Notice(local); else Show(request, local);
                return;
            }

            // 2) A single leftover word is nearly always a mis-hearing ("stocktimer"): don't spend a Codex run on it.
            if (!IsWidgetRequest(request) && Regex.Split(request.Trim(), @"\s+").Length <= 1)
            {
                DoneOnce();
                Overlay.Notice("Didn't catch “" + request.Trim().TrimEnd('.', ',', '?', '!') + "”", 3000, error: true);
                return;
            }

            // 3) Codex. The newest request wins: a run that is still going is cancelled.
            if (Interlocked.Exchange(ref running, 1) == 1)
            {
                // A long job that is really doing things (a git merge, an install) is not thrown away by the next thing you say:
                // the new request waits its turn (click the working card to cancel the job instead). Quick answers still replace each other.
                bool midJob = lastRunWasTask && Environment.TickCount64 - runStart > 20_000;
                if (midJob) { Overlay.Notice("Queued — Luna is still on the last job", 3000); Log("queued behind the running job"); }
                else CancelRunning();
                for (int i = 0; i < (midJob ? 18_000 : 40) && Volatile.Read(ref running) == 1; i++) await Task.Delay(100);
                Interlocked.Exchange(ref running, 1);
            }
            runStart = Environment.TickCount64;
            string answer = null;
            bool failed = false, cancelled = false;
            lastWidgetTitle = null;
            activeModel = pickedModel; activeModelName = pickedName;
            activeThread = tid; activeForced = forced;
            int work = Cards.BeginWork(pickedName != "Luna" ? pickedName + " · " + request : request);
            progressWork = work;                                    // a card with elapsed time appears if this takes > 0.7 s (click it to cancel)
            // The "Thinking" pill gives instant feedback; after a moment the card takes over.
            using var early = new System.Threading.Timer(_ => DoneOnce(), null, 1300, Timeout.Infinite);
            var sw = Stopwatch.StartNew();
            try { answer = await Execute(request, s, skipLocal: true); }
            catch (OperationCanceledException) { cancelled = true; Log("cancelled"); }
            catch (Exception ex) { Log("failed: " + ex.Message); answer = "Luna couldn't run that: " + ex.Message; failed = true; }
            finally { Interlocked.Exchange(ref running, 0); }
            Log($"ran {sw.Elapsed.TotalSeconds:0.0}s, answer {answer?.Length ?? 0} chars");
            DoneOnce();

            if (cancelled)
            {
                Cards.EndWork(work, null);
                Overlay.Notice("Cancelled", 1500, error: true);
                return;
            }
            if ((sw.Elapsed.TotalSeconds > 1.5 || forced) && answer != null && !answer.StartsWith("✓") && !failed) Remember(request, answer, lastRunWasTask, tid);
            else if (failed && lastFailureNote != null) Remember(request, lastFailureNote, true, tid);   // so "continue" / "try again" resumes with what was already done
            Projects.AfterExchange(request, failed ? null : answer, lastRunWasTask);

            bool confirm = answer != null && answer.StartsWith("✓");
            if (!string.IsNullOrWhiteSpace(answer))
            {
                string one = Regex.Replace(answer.Trim(), @"\s+", " ");
                Log("answer: " + (one.Length > 500 ? one.Substring(0, 500) + "…" : one));   // so a result you missed can be read in daemon.log
            }
            // The card keeps the answer (hover to read it, click to reopen), so a result that flashed past is never lost.
            bool hasText = !string.IsNullOrWhiteSpace(answer) && lastWidgetTitle == null;
            // A real answer opens in the answer box (bottom right), so its card just goes away; failures and action-only results keep a card.
            Cards.EndWork(work,
                failed ? "Couldn't finish that"
                : lastWidgetTitle != null ? "Opened " + lastWidgetTitle
                : confirm || string.IsNullOrWhiteSpace(answer) ? (sw.Elapsed.TotalSeconds > 1.5 ? "Done" : null)
                : null, failed, failed && hasText ? answer : null, tid);

            if (string.IsNullOrWhiteSpace(answer)) return;
            SpeakShort(answer, s, failed);
            // "✓ ..." = a short confirmation: shown in the pill (instant, nothing launched). Anything longer opens the answer window.
            if (confirm) Overlay.Notice(answer);
            else Show(request, answer, Threads.Get(tid) != null ? tid : null);
        });
    }

    // Jarvis talks back: short answers (and the first sentence of long ones) are said out loud unless Settings.SpeakReplies is "off".
    static void SpeakShort(string answer, Settings s, bool failed)
    {
        try
        {
            string mode = (Settings.Load().SpeakReplies ?? "off").Trim().ToLowerInvariant();
            if (mode is "off" or "" || failed) return;
            string plain = Md.Plain(answer.TrimStart('✓', ' '));
            if (plain.Length == 0) return;
            if (mode != "all" && plain.Length > 220)
            {
                var first = Regex.Match(plain, @"^.{20,200}?[.!?](?=\s|$)");
                if (!first.Success) return;
                plain = first.Value;
            }
            Assistant.Speak(plain);
        }
        catch { }
    }

    internal static string RouteTest(string request)
    {
        var s = Settings.Load();
        bool craft = Craft.Ask(request, s), widget = !craft && WidgetAsk(request);
        bool control = IsControl(request, s, widget);
        bool web = !widget && WebBridge.Ask(request, s);
        bool question = !control && !craft && !web && (widget || IsQuestion(request));
        var p = Brain.Route(request, question, widget, control, craft, s);
        return $"{(widget ? "widget" : control ? "screen control" : craft ? "ArtCraft" : question ? "question" : "task")}{(web ? " + web browser" : "")} -> {p.Name} ({p.Model}, {p.Effort} effort): {p.Why}" + (Brain.Writing.IsMatch(request) && s.Humanize ? " + writing style" : "");
    }

    // "click the blue button", "scroll down", "finish this doc", "fill in this form": Luna needs eyes and hands, not just one screenshot.
    static readonly Regex ControlAsk = new(@"\b(?:click|double[- ]click|right[- ]click|scroll (?:up|down|to)|drag|take (?:over|control)|control (?:my|the) (?:screen|pc|computer|mouse|keyboard)|use (?:my|the) (?:mouse|keyboard|screen)|do (?:it|this|that) (?:for me )?on (?:my |the )?screen|navigate to|go through (?:this|the) (?:page|form|site|settings)|fill (?:in|out)|press (?:the )?(?:\w+ )?(?:button|key|tab)|select (?:all|the|this)|in (?:this|the) (?:window|app|tab|browser|page)\b.{0,40}\b(?:click|open|change|set|type|go))\b", RegexOptions.IgnoreCase);
    static bool IsControl(string request, Settings s, bool widget) =>
        s.ScreenControl && !widget && (ControlAsk.IsMatch(request) || (Brain.Writing.IsMatch(request) && ScreenAsk.IsMatch(request)));

    public static Task<string> TestLocal(string request) { DryRun = true; return TryLocal(request, Settings.Load()); }

    // Blocking version for `--luna "<request>"` (testing from a terminal).
    public static void RunBlocking(string request, Settings s)
    {
        activeModel = PickModel(ref request, s, out activeModelName);
        string answer = Execute(request, s).GetAwaiter().GetResult();
        Console.WriteLine(string.IsNullOrWhiteSpace(answer) ? "(no answer; nothing to say)" : answer);
        if (!string.IsNullOrWhiteSpace(answer)) Show(request, answer);
    }

    internal static string Dry(string what) => "DRY RUN: " + what;
    public static bool DryRun;   // `--opentest`: say what would happen instead of doing it

    // Anything that can be done without a model is done here, free. Returns null when nothing matched
    // (then it goes to Codex), "" when handled with nothing to say, else text to show.
    static async Task<string> TryLocal(string request, Settings s)
    {
        string r = null;
        try { r = await Router.Local(request, s); }
        catch (Exception ex) { Log("router error: " + ex.Message); }
        Log((r != null ? "local   " : "-> codex ") + "\"" + (request.Length > 140 ? request.Substring(0, 140) : request) + "\"");
        return r;
    }

    static volatile int progressWork;   // the card whose hover text Execute feeds

    // One `codex exec --json` line -> a "what is it doing" step on the working card. False = not an event (keep it for error messages).
    static bool Progress(int card, string line)
    {
        if (!line.StartsWith("{")) return false;
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(line);
            var root = d.RootElement;
            string type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
            if (type == "error" || type == "turn.failed") return false;
            if (type == "turn.completed" && root.TryGetProperty("usage", out var usage)) { Brain.NoteUsage(usage); return true; }
            if (!root.TryGetProperty("item", out var it)) return true;
            string kind = it.TryGetProperty("type", out var k) ? k.GetString() : "";
            string Str(string name) => it.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            string step = kind switch
            {
                "reasoning" when type == "item.completed" => Thought(Str("text")),
                "agent_message" when type == "item.completed" => "Writing the answer",
                "command_execution" when type == "item.started" => Command(Str("command")),
                "command_execution" when type == "item.completed" => CommandResult(it),
                "web_search" => Str("query") is { Length: > 0 } q ? "Searching the web for “" + q + "”" : "Searching the web",
                "mcp_tool_call" when type == "item.started" => "Using " + (Str("tool") ?? Str("server") ?? "a tool"),
                "file_change" when type == "item.started" || type == "item.completed" => FileChange(it, type == "item.completed"),
                _ => null,
            };
            if (kind == "command_execution") commandRunning = type == "item.started";
            if (step != null && kind != "agent_message" && kind != "reasoning" && !step.StartsWith("↳ ok")) lock (runSteps) { runSteps.Add(step.Length > 100 ? step.Substring(0, 99) + "…" : step); while (runSteps.Count > 60) runSteps.RemoveAt(0); }
            Cards.Progress(card, step);
        }
        catch { }
        return true;
    }

    // A reasoning summary: first sentence, markdown stripped.
    static string Thought(string t)
    {
        if (string.IsNullOrWhiteSpace(t)) return null;
        t = Regex.Replace(t, @"[*_`#]+", "").Trim();
        int dot = t.IndexOfAny(new[] { '.', '\n' });
        // the heading of the summary plus its first full sentence: "Checking the merge state: the pull left two conflicted files"
        int nl = t.IndexOf('\n');
        if (nl > 0 && nl < 60 && t.Length > nl + 20) { string rest = t.Substring(nl + 1).Trim(); int d2 = rest.IndexOfAny(new[] { '.', '\n' }); t = t.Substring(0, nl).Trim().TrimEnd(':') + ": " + (d2 > 20 ? rest.Substring(0, d2) : rest); }
        else if (dot > 20) t = t.Substring(0, dot);
        t = Regex.Replace(t, @"\s+", " ");
        return t.Length > 260 ? t.Substring(0, 259).TrimEnd() + "…" : t;
    }

    // "↳ ok" / "↳ failed (exit 1): fatal: not a git repository" after each command, so the hover list shows what actually happened.
    static string CommandResult(System.Text.Json.JsonElement it)
    {
        int? code = it.TryGetProperty("exit_code", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Number ? c.GetInt32() : null;
        string outp = it.TryGetProperty("aggregated_output", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.String ? o.GetString() : "";
        string first = outp.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        if (first.Length > 110) first = first.Substring(0, 109) + "…";
        if (code is null or 0) return first.Length > 0 && !first.StartsWith("{") ? "↳ ok · " + first : "↳ ok";
        return "↳ failed (exit " + code + ")" + (first.Length > 0 ? ": " + first : "");
    }

    static string FileChange(System.Text.Json.JsonElement it, bool done)
    {
        var names = new List<string>();
        if (it.TryGetProperty("changes", out var ch) && ch.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var x in ch.EnumerateArray())
                if (x.TryGetProperty("path", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String) names.Add(Path.GetFileName(p.GetString()));
        string list = names.Count == 0 ? "files" : names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.Take(3)) + " +" + (names.Count - 3) + " more";
        return (done ? "Edited " : "Editing ") + list;
    }

    // `git -C D:\repo merge origin/main` -> "Merging origin/main in repo". Null = not a git command.
    static string GitStep(string c)
    {
        var m = Regex.Match(c, @"\bgit(?:\.exe)?\b(?:\s+-C\s+(?:""(?<d>[^""]+)""|'(?<d>[^']+)'|(?<d>\S+)))?(?:\s+-c\s+\S+)*\s+(?<sub>[a-z][a-z-]*)(?<rest>[^|;&\r\n]*)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        string sub = m.Groups["sub"].Value.ToLowerInvariant();
        string rest = m.Groups["rest"].Value.Trim().Trim('"', '\'');
        string where = m.Groups["d"].Success ? " in " + Path.GetFileName(m.Groups["d"].Value.TrimEnd('\\', '/')) : "";
        string arg = Regex.Match(rest, @"(?:^|\s)(?!-)([^\s""']+)").Groups[1].Value;
        string msg = Regex.Match(rest, @"-m\s+[""']([^""']{1,60})").Groups[1].Value;
        return sub switch
        {
            "status" => "Checking git status" + where,
            "diff" or "show" => "Comparing changes" + where,
            "log" or "reflog" or "shortlog" => "Reading git history" + where,
            "merge" => "Merging " + (arg.Length > 0 ? arg : "branches") + where,
            "commit" => "Committing" + (msg.Length > 0 ? " “" + msg + "”" : " changes") + where,
            "checkout" or "switch" => "Switching to " + (arg.Length > 0 ? arg : "a branch") + where,
            "branch" => "Looking at branches" + where,
            "tag" => "Tagging " + (arg.Length > 0 ? arg : "a release") + where,
            "add" or "rm" or "mv" => "Staging files" + where,
            "stash" => "Stashing changes" + where,
            "rebase" or "cherry-pick" or "revert" => "Replaying commits" + where,
            "fetch" or "pull" or "push" or "remote" => "Talking to the remote" + where,
            "rev-parse" or "ls-files" or "config" or "describe" or "rev-list" or "blame" or "ls-remote" => "Inspecting the repo" + where,
            _ => "Running git " + sub + where,
        };
    }

    // `node vpc.js click 400 300` -> "Clicking at 400, 300"; `node vpc.js craft pdf combine ...` -> "PdfCraft: combine a.pdf b.pdf"; vgit.js -> the git step.
    static string BridgeStep(string c)
    {
        var g = Regex.Match(c, @"\bvgit\.js[""']?\s+(?<repo>""[^""]+""|'[^']+'|\S+)\s+(?<rest>[^|;&\r\n]+)", RegexOptions.IgnoreCase);
        if (g.Success) return (GitStep("git " + g.Groups["rest"].Value) ?? "Running git " + g.Groups["rest"].Value.Trim()) + " (" + Path.GetFileName(g.Groups["repo"].Value.Trim('"', '\'').TrimEnd('\\', '/')) + ", with your login)";
        var m = Regex.Match(c, @"\bvpc\.js[""']?\s+(?<cmd>[a-z]+)\s*(?<rest>[^|;&\r\n]*)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        string cmd = m.Groups["cmd"].Value.ToLowerInvariant(), rest = m.Groups["rest"].Value.Trim().Trim('"', '\'');
        string shortRest = rest.Length > 70 ? rest.Substring(0, 69) + "…" : rest;
        var xy = Regex.Match(rest, @"^(\d+)\s+(\d+)");
        switch (cmd)
        {
            case "look": return rest.StartsWith("s", StringComparison.OrdinalIgnoreCase) ? "Looking at the whole screen" : "Looking at the window you're in";
            case "click": return (rest.Contains("double") ? "Double-clicking" : rest.Contains("right") ? "Right-clicking" : "Clicking") + (xy.Success ? $" at {xy.Groups[1].Value}, {xy.Groups[2].Value}" : "");
            case "move": return "Moving the mouse";
            case "drag": return "Dragging with the mouse";
            case "scroll": return rest.StartsWith("-") ? "Scrolling down" : "Scrolling up";
            case "key": return "Pressing " + shortRest;
            case "type": return rest.StartsWith("--file") ? "Typing the text it wrote into your window" : "Typing “" + shortRest + "”";
            case "paste": return "Pasting the text it wrote into your window";
            case "doc": return "Reading the whole document you have open";
            case "selection": return "Reading the selected text";
            case "windows": return "Checking which windows are open";
            case "focus": return "Switching to the “" + shortRest + "” window";
            case "wait": return "Waiting for the screen to catch up";
            case "craft":
            {
                var a = Regex.Match(rest, @"^(\w+)\s*(.*)$");
                string app = a.Groups[1].Value.ToLowerInvariant() switch { "effect" => "EffectCraft", "photo" => "PhotoCraft", "vector" => "VectorCraft", "fig" => "FigCraft", "pdf" => "PdfCraft", _ => "ArtCraft" };
                string what = a.Groups[2].Value.Trim();
                string sub = Regex.Match(what, @"^[\w-]+").Value.ToLowerInvariant();
                string say = sub switch
                {
                    "" or "--help" or "help" or "commands" or "tools" => "reading what it can do",
                    "info" or "props" or "get" => "inspecting the project",
                    "render-frame" => "rendering a check frame to look at",
                    "render" => "rendering the final file (this is the slow part)",
                    "script" => "running a script to build the scene",
                    "run" or "exec" or "set" or "edit" => "applying changes: " + (what.Length > 60 ? what.Substring(0, 59) + "…" : what),
                    _ => what.Length > 70 ? what.Substring(0, 69) + "…" : what,
                };
                return app + ": " + say;
            }
        }
        return null;
    }

    // Common build/package/script tools -> a step; null = not one.
    static string ToolStep(string c)
    {
        var m = Regex.Match(c, @"\b(?<t>npm|npx|pnpm|yarn|node|dotnet|python|py|pip|cargo|go|gh|tsc|msbuild|make|cmake|powershell|winget)(?:\.exe|\.cmd)?\s+(?<rest>[^|;&\r\n]*)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        string t = m.Groups["t"].Value.ToLowerInvariant(), rest = m.Groups["rest"].Value.Trim();
        string first = Regex.Match(rest, @"^(?:run\s+)?([\w:.\-]+)").Groups[1].Value;
        string tail = rest.Length > 70 ? rest.Substring(0, 69) + "…" : rest;
        return t switch
        {
            "npm" or "pnpm" or "yarn" or "npx" => first is "test" or "run" or "build" or "install" or "ci" or "version" ? "Running " + t + " " + first : "Running " + t + " " + tail,
            "dotnet" => first is "build" or "test" or "publish" or "run" ? "Running dotnet " + first : "Running dotnet " + tail,
            "gh" => "Using GitHub CLI: " + tail,
            "node" or "python" or "py" => "Running a " + (t == "node" ? "Node" : "Python") + " script: " + tail,
            "pip" or "winget" or "cargo" or "go" => "Running " + t + " " + tail,
            _ => null,
        };
    }

    // A shell command -> plain words ("Get-Content 'C:\x\a.txt'" -> "Reading a.txt"). Unknown commands become a generic step.
    static readonly (Regex Rx, string Say)[] Verbs =
    {
        (new(@"\bspicetify\s+(?:backup\s+)?apply\b", RegexOptions.IgnoreCase), "Applying Spicetify changes"),
        (new(@"\bspicetify\b", RegexOptions.IgnoreCase), "Setting up Spicetify"),
        (new(@"Expand-Archive|\bunzip\b|\btar\s+-x|\b7z\s+x\b", RegexOptions.IgnoreCase), "Unpacking @"),
        (new(@"Copy-Item|\bcopy\b|\bcp\b|Move-Item|\bmove\b", RegexOptions.IgnoreCase), "Copying @"),
        (new(@"Set-Content|Add-Content|Out-File|WriteAll|\bsed\b|-replace\b", RegexOptions.IgnoreCase), "Editing @"),
        (new(@"Invoke-WebRequest|Invoke-RestMethod|\bcurl\b|\bwget\b|\biwr\b", RegexOptions.IgnoreCase), "Downloading"),
        (new(@"Get-Content|\bcat\b|\btype\b|Select-String|\brg\b|\bfindstr\b", RegexOptions.IgnoreCase), "Reading @"),
        (new(@"Get-ChildItem|\bls\b|\bdir\b|Test-Path|Get-Item\b|Resolve-Path|\bfind\b", RegexOptions.IgnoreCase), "Looking through files"),
        (new(@"Get-Process|Get-Service|Get-CimInstance|Get-WmiObject|\btasklist\b", RegexOptions.IgnoreCase), "Checking what's running"),
        (new(@"Remove-Item|\bdel\b|\brm\b", RegexOptions.IgnoreCase), "Cleaning up"),
        (new(@"New-Item|\bmkdir\b", RegexOptions.IgnoreCase), "Creating @"),
        (new(@"Start-Process|\bstart\b", RegexOptions.IgnoreCase), "Launching something"),
    };

    internal static string Command(string c)
    {
        if (string.IsNullOrWhiteSpace(c)) return null;
        // drop the shell wrapper ("C:\...\powershell.exe" -Command) so it isn't mistaken for a file name
        c = Regex.Replace(c, @"^\s*""?[^""]*?(?:powershell|pwsh|cmd)(?:\.exe)?""?\s+(?:-\w+\s+)*", "", RegexOptions.IgnoreCase);
        string special = BridgeStep(c) ?? GitStep(c) ?? ToolStep(c);
        if (special != null) return special;
        foreach (var (rx, say) in Verbs)
        {
            if (!rx.IsMatch(c)) continue;
            if (!say.Contains('@')) return say;
            var f = Regex.Match(c, @"([\w\-]+(?:[ ][\w\-]+)*\.(?:txt|zip|js|ini|json|md|css|html|exe|ps1|png|jpg|pdf|docx|xlsx|csv|py|cs))", RegexOptions.IgnoreCase);
            string name = f.Success ? f.Groups[1].Value.Trim() : null;
            string fallback = say.StartsWith("Unpacking") ? "an archive" : say.StartsWith("Creating") ? "a folder" : "files";
            return say.Replace("@", name ?? fallback);
        }
        // Unknown command: show it (trimmed) rather than a vague "Working on it".
        c = Regex.Replace(c.Trim(), @"\s+", " ");
        return "Running " + (c.Length > 100 ? c.Substring(0, 99) + "…" : c);
    }

    static Process currentCodex;
    static volatile bool cancelRequested;

    // Click the working card (or start a new request) to stop a Codex run that is taking too long.
    public static void CancelRunning()
    {
        cancelRequested = true;
        try { currentCodex?.Kill(true); } catch { }
    }

    // ---- Recovery. A stalled or failed Codex run is not the end: it is retried once, told what the first attempt already did
    // (its changes are still on disk), so a long job continues instead of starting over; if that fails too, the card says what was done
    // and the next "continue" / "try again" resumes from that note (see Remember in Start).
    static readonly List<string> runSteps = new();     // plain-words steps of the current request (what the card shows)
    static string resumeNote = "";                     // set for the retry attempt
    static string lastFailureNote;                     // set when both attempts failed: remembered so a follow-up can pick up
    static long lastEvent;                             // tick of the last output from Codex (any event counts as "alive")
    static volatile bool commandRunning;               // a shell command is in flight: silence is normal (installs, builds, git over the network)

    static string StepsDone()
    {
        lock (runSteps) return runSteps.Count == 0 ? "nothing yet" : string.Join("; ", runSteps.TakeLast(25));
    }

    static async Task<string> Execute(string request, Settings s, bool skipLocal = false)
    {
        if (!skipLocal)
        {
            string local = await TryLocal(request, s);   // simple open/search commands cost no tokens
            if (local != null) return local;
        }
        lock (runSteps) runSteps.Clear();
        resumeNote = ""; lastFailureNote = null;
        try { return await RunCodex(request, s); }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not FileNotFoundException && !DryRun)
        {
            string why = ex is TimeoutException ? "it stalled (" + ex.Message + ")" : "it failed (" + Regex.Replace(ex.Message, @"\s+", " ").Trim() + ")";
            if (why.Length > 300) why = why.Substring(0, 300) + "…)";
            string done = StepsDone();
            Log("attempt 1 " + why + "; retrying. Done so far: " + done);
            Cards.Progress(progressWork, "Hit a snag — picking up from where it stopped");
            resumeNote = "RESUMING: an earlier attempt at this same request was cut off because " + why + ". Before it stopped it had done (in order): " + done
                + ". Those changes are still on disk. Look at the current state first (git status, the files involved) instead of redoing finished steps, then finish the job. "
                + "If something it ran was slow or hung, do it differently (smaller pieces, a different command, or in the background and check on it) rather than repeating it. "
                + "If a step truly can't be done from here, finish everything around it and say precisely what is left.\n\n";
            try { return await RunCodex(request, s); }
            catch (Exception ex2) when (ex2 is not OperationCanceledException)
            {
                lastFailureNote = "FAILED twice (" + (ex2 is TimeoutException ? "stalled: " + ex2.Message : Regex.Replace(ex2.Message, @"\s+", " ").Trim()) + "). Already done: " + StepsDone()
                    + ". The user can say continue / try again to resume; check the current state first.";
                throw new Exception(ex2.Message + " (I tried twice. Done so far: " + StepsDone() + ". Say “continue” and I'll pick it up from there.)");
            }
        }
        finally { resumeNote = ""; }
    }

    static async Task<string> RunCodex(string request, Settings s)
    {
        try { return await RunCodexInner(request, s); }
        finally { PcBridge.End(); WebBridge.End(); }   // Luna's hands come off the PC (and its browser) the moment the run ends, however it ends
    }

    static async Task<string> RunCodexInner(string request, Settings s)
    {
        string codex = Summariser.FindCodex(s) ?? throw new FileNotFoundException("Codex CLI not found. Is the Codex app installed?");
        string work = Directory.CreateDirectory(Path.Combine(Paths.DataDir, "luna")).FullName;
        string tmp = Path.Combine(work, "answer-" + Guid.NewGuid().ToString("N") + ".tmp");

        var psi = new ProcessStartInfo(codex)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = work,
        };
        // Hidden chat (--ephemeral), small model at low effort. Plain questions run read-only with the shell tools
        // switched off (answers in ~4 s, and it can't wander off searching "the workspace"); PC tasks may write
        // only inside the scratch folder.
        bool craft = Craft.Ask(request, s);                       // an ArtCraft job (video, image, vector, design, PDF) wins over "make a ... animation" = widget
        bool widget = !craft && WidgetAsk(request);
        bool control = IsControl(request, s, widget);             // needs to see and operate the screen (click, scroll, finish the doc that is open)
        bool web = !widget && WebBridge.Ask(request, s);          // a website is involved: Luna's own background browser (vweb.js), shell needed
        bool question = !control && !craft && !web && (widget || IsQuestion(request));
        lastRunWasTask = !question;
        PcBridge.Begin(control, craft);                           // vpc.js works only for this run, and only for what was asked
        WebBridge.Begin(web);
        if (!question)
        {
            GitBridge.Ensure();   // vgit.js: git commands that need the user's GitHub login run on the host and come back with their real output
            GitBridge.PushOk = Regex.IsMatch(request, @"\bpush(?:ed|ing|es)?\b", RegexOptions.IgnoreCase)
                || (FollowUpStart.IsMatch(request.Trim()) && LastTurn() is Turn pt && Regex.IsMatch(pt.A, @"\bpush\b", RegexOptions.IgnoreCase));
        }
        projectBlock = widget ? "" : Projects.Context(request, s);   // only when a project is named, or was the last topic (see Projects.cs)
        string shot = null;
        if (!widget && (control || ScreenAsk.IsMatch(request))) shot = TakeShot(work, control);
        screenAttached = shot != null;
        screenNote = !screenAttached ? "" : control
            ? "A screenshot of the window the user is in is attached. " + (File.Exists(shot + ".txt") ? PcBridge.NoteShot(shot + ".txt") + " " : "") + "\"this\", \"that\" and \"it\" refer to what is in it.\n\n"
            : ScreenNote;
        try { File.Delete(shot + ".txt"); } catch { }
        extraBlock = (control ? PcBridge.Help : "") + (web ? WebBridge.Help : "") + (craft ? Craft.Help(s) : "") + (s.Humanize && Brain.Writing.IsMatch(request) ? Humanize.StyleBlock : "");

        // Which model: the one named out loud ("Sol, ...", "swap to Terra"), otherwise the cheapest that suits the request (Brain.cs).
        string model = activeModel, effort = s.LunaEffort;
        bool smart = false;
        if (model == null)
        {
            var pick = Brain.Route(request, question, widget, control, craft, s);
            model = pick.Model; effort = pick.Effort; smart = pick.Smart;
            Log($"route: {pick.Name} ({pick.Model}, {pick.Effort}) because {pick.Why}");
            if (pick.Smart) Cards.Progress(progressWork, $"Using {pick.Name} for this ({pick.Why})");
        }
        if (!DryRun) Brain.NoteRun(smart);
        var args = new List<string> { "exec", "--ephemeral", "--skip-git-repo-check", "-s", question ? "read-only" : "workspace-write", "-m", model ?? s.LunaModel,
                                      "-c", $"model_reasoning_effort=\"{effort}\"", "-C", work };
        if (shot != null) { args.Add("-i"); args.Add(shot); }   // a picture of the window the user is looking at
        args.AddRange(new[] { "-o", tmp, "--json" });
        // Light mode: skip the Codex app's own config (its MCP servers, 12 plugins and rules). Each of those starts extra processes and adds
        // ~5k prompt tokens; without them a request is ~40% faster. The Windows sandbox setting lives in that config, so it is passed explicitly.
        if (s.LunaInternet)
        {
            args.AddRange(new[] { "-c", "web_search=\"live\"" });                                       // the built-in web search tool (no extra prompt tokens until it is used)
            if (!question) args.AddRange(new[] { "-c", "sandbox_workspace_write.network_access=true" });   // curl / downloads / public git from PC tasks
        }
        if (s.LunaLight) args.AddRange(new[] { "--ignore-user-config", "--ignore-rules", "-c", "windows.sandbox=\"elevated\"" });
        if (question) args.AddRange(new[] { "--disable", "shell_tool", "--disable", "unified_exec" });
        else foreach (var d in s.LunaWritableDirs ?? new()) if (Directory.Exists(d)) { args.Add("--add-dir"); args.Add(d); }   // write access beyond the scratch folder
        args.Add("-");
        foreach (var a in args) psi.ArgumentList.Add(a);

        // The sandbox user has no Windows TLS credentials (schannel fails), so git inside it uses OpenSSL, and must never wait on a login prompt.
        psi.Environment["GIT_CONFIG_COUNT"] = "1"; psi.Environment["GIT_CONFIG_KEY_0"] = "http.sslBackend"; psi.Environment["GIT_CONFIG_VALUE_0"] = "openssl";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0"; psi.Environment["GCM_INTERACTIVE"] = "never";
        using var p = new Process { StartInfo = psi };
        var log = new StringBuilder();
        DataReceivedEventHandler keep = (_, e) => { if (e.Data != null) lock (log) { log.AppendLine(e.Data); if (log.Length > 4000) log.Remove(0, 2000); } };
        int card = progressWork;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) { lastEvent = Environment.TickCount64; if (!Progress(card, e.Data)) keep(null, e); } };   // --json: events on stdout
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lastEvent = Environment.TickCount64; keep(null, e); };
        cancelRequested = false;
        lastEvent = Environment.TickCount64; commandRunning = false;
        p.Start();
        currentCodex = p;
        Jobs.Add(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { await p.StandardInput.WriteAsync(Prompt(request, question, widget)); p.StandardInput.Close(); } catch { }
        // No fixed deadline for the whole job (long multi-step work used to be killed at 2.5 min): it runs as long as Codex keeps producing events.
        // Only silence is a problem: 4 min of nothing while it thinks, 15 min while one shell command is running, with an overall cap.
        int idleSec = widget ? 240 : question ? 75 : 240, capMin = widget ? 6 : question ? 4 : 30;
        if (int.TryParse(Environment.GetEnvironmentVariable("VOWENLITE_IDLE"), out int testIdle)) idleSec = testIdle;   // tests only: forces a stall
        var clock = Stopwatch.StartNew();
        string stalled = null;
        while (!await Task.Run(() => p.WaitForExit(500)))
        {
            if (cancelRequested) break;
            long idle = (Environment.TickCount64 - lastEvent) / 1000;
            if (idle > (commandRunning ? 900 : idleSec)) { stalled = $"no activity for {idle} s" + (commandRunning ? " while a command was running" : ""); break; }
            if (clock.Elapsed.TotalMinutes > capMin) { stalled = $"over the {capMin} min limit"; break; }
        }
        currentCodex = null;
        if (cancelRequested) { try { p.Kill(true); } catch { } throw new OperationCanceledException(); }
        if (stalled != null) { try { p.Kill(true); } catch { } throw new TimeoutException(stalled); }

        try
        {
            if (p.ExitCode != 0)
            {
                string tail = log.ToString().Trim();
                if (tail.Length > 600) tail = "..." + tail.Substring(tail.Length - 600);
                throw new Exception($"Codex exit {p.ExitCode}: {tail}");
            }
            return await Dispatch(File.Exists(tmp) ? File.ReadAllText(tmp).Trim() : "", s);
        }
        finally { try { File.Delete(tmp); } catch { } if (shot != null) try { File.Delete(shot); } catch { } }
    }

    static volatile bool lastRunWasTask;
    static long runStart;
    static string projectBlock = "";
    static bool screenAttached;
    static string screenNote = "", extraBlock = "";   // per run: what the attached screenshot is; screen-control / ArtCraft / writing-style instructions

    // "what's this error on my screen", "what am I looking at", "read my screen": attach a picture of the active window.
    static readonly Regex ScreenAsk = new(@"\b(?:on|in|from)\s+(?:my\s+|the\s+)?screen\b|\b(?:this|that|the|my)\s+(?:doc(?:k|s)?|documents?|box|field|form|section|paragraph|essay|assignment|worksheet|slide|sheet)\b|\b(?:complete|finish|fill(?:\s+(?:in|out))?|continue|answer)\s+(?:this|that|it|the|my)\b|\b(?:my|the)\s+screen\b|\bwhat(?:'s| is| am i)\s+(?:am i\s+)?(?:looking at|seeing)\b|\blook\s+at\s+(?:this|my|the)\b|\bthis\s+(?:error|page|window|code|problem|question|screen)\b|\b(?:read|explain|summari[sz]e)\s+(?:this|that|it)\b", RegexOptions.IgnoreCase);

    static string TakeShot(string dir, bool forControl = false)
    {
        try
        {
            string path = Path.Combine(dir, "screen-" + Guid.NewGuid().ToString("N") + ".png");
            // for screen control the picture also records where it sits on the screen, so click coordinates can be mapped back
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath, "--shot \"" + path + "\"" + (forControl ? " --meta --max 1400" : "")) { UseShellExecute = false, CreateNoWindow = true });
            if (p == null || !p.WaitForExit(6000)) { try { p?.Kill(); } catch { } return null; }
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex) { Log("screenshot failed: " + ex.Message); return null; }
    }

    // ---- Opening things. Codex's sandbox can't launch Store apps/URLs ("Windows denied the launch request"),
    // so Luna asks for them with `OPEN: <app name | https URL | file or folder path>` lines and this
    // (unsandboxed) side opens them. Apps are matched against the Start menu list; executables by path are refused.
    static readonly Regex OpenLine = new(@"^\s*[-*>`]*\s*OPEN:\s*(.+?)\s*`*\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    static readonly string[] Executable = { ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".msi", ".scr", ".com", ".lnk", ".reg", ".hta", ".jar" };

    // Runs every OPEN line and returns the rest of the reply (plus a note for anything that failed).
    // Hook lines (REMIND/PIN/COPY/TYPE/SAY/REMEMBER/FORGET, see Assistant.cs) run first; the rest goes on to widgets / OPEN.
    static async Task<string> Dispatch(string reply, Settings s)
    {
        string afterHooks = Assistant.Process(reply, out var did);
        string rest = await DispatchOpen(afterHooks, s);
        if (did.Count == 0) return rest;
        if (string.IsNullOrWhiteSpace(rest)) return "✓ " + string.Join(" · ", did.Take(2));
        return rest;
    }

    internal static string TestHooks(string reply, bool dry = true) { DryRun = dry; return Dispatch(reply, Settings.Load()).GetAwaiter().GetResult(); }

    static async Task<string> DispatchOpen(string reply, Settings s)
    {
        string afterWidget = TryWidget(reply);      // an HTML page in the reply opens as a widget window
        if (afterWidget != null) { if (afterWidget.Length == 0 || afterWidget.StartsWith("DRY RUN")) return afterWidget; reply = afterWidget; }
        var targets = OpenLine.Matches(reply).Select(m => m.Groups[1].Value.Trim('"', '\'', ' ')).ToList();
        if (targets.Count == 0) return reply;
        var rest = new StringBuilder(OpenLine.Replace(reply, "").Trim());
        foreach (var t in targets.Take(5))
        {
            string err = await Open(t);
            if (err != null) rest.Append(rest.Length > 0 ? "\n\n" : "").Append(err);
        }
        return rest.ToString().Trim();
    }

    internal static async Task<string> Open(string target)
    {
        if (DryRun && !Regex.IsMatch(target, @"^https?://", RegexOptions.IgnoreCase) && !Path.IsPathRooted(target))
        {
            var found = await FindApp(target);
            return found == null ? $"I couldn't find an app called \"{target}\"." : Dry($"open app {found.Value.Name} ({found.Value.Id})");
        }
        if (DryRun) return Dry("open " + target);
        try
        {
            if (Regex.IsMatch(target, @"^https?://", RegexOptions.IgnoreCase))
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                return null;
            }
            if (Path.IsPathRooted(target))
            {
                if (Executable.Contains(Path.GetExtension(target).ToLowerInvariant())) return $"I won't launch the program file {target} by path; ask for it by name instead.";
                if (!File.Exists(target) && !Directory.Exists(target)) return $"I couldn't find {target}.";
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                return null;
            }
            var app = await FindApp(target);
            if (app == null) return $"I couldn't find an app called \"{target}\".";
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + app.Value.Id) { UseShellExecute = false });
            return null;
        }
        catch (Exception ex) { Log("open failed: " + ex.Message); return $"I couldn't open {target}: {ex.Message}"; }
    }

    internal static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]", "");

    internal static async Task<(string Name, string Id)?> FindApp(string query)
    {
        string q = Norm(Regex.Replace(query, @"\.(exe|app)$", "", RegexOptions.IgnoreCase));
        if (q.Length == 0) return null;
        foreach (bool fresh in new[] { false, true })   // second pass re-reads the Start menu (new install)
        {
            var apps = await StartApps(fresh);
            var hit = apps.Where(a => Norm(a.Name) == q).Concat(apps.Where(a => Norm(a.Name).StartsWith(q) || Norm(a.Name).Contains(q)))
                          .Concat(apps.Where(a => Norm(a.Name).Length >= 4 && q.Contains(Norm(a.Name))))
                          .Concat(apps.Where(a => q.Length >= 4 && !a.Id.Contains('\\') && !a.Id.Contains('/') && Norm(a.Id).Contains(q)))   // e.g. "Codex" is listed as "ChatGPT" but its ID is OpenAI.Codex_...
                          .Cast<(string Name, string Id)?>().FirstOrDefault();
            if (hit != null) return hit;
        }
        return null;
    }

    static async Task<List<(string Name, string Id)>> StartApps(bool refresh)
    {
        string cache = Path.Combine(Paths.DataDir, "apps.txt");
        if (!refresh && File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromDays(1))
            return Parse(File.ReadAllText(cache));
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-StartApps | ForEach-Object { $_.Name + '|' + $_.AppID }\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi);
        Jobs.Add(p);
        string text = await p.StandardOutput.ReadToEndAsync();
        p.WaitForExit(20000);
        if (text.Length > 0) { try { File.WriteAllText(cache, text); } catch { } }
        return Parse(text);
    }

    static List<(string Name, string Id)> Parse(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.Trim()).Select(l => (i: l.LastIndexOf('|'), l)).Where(x => x.i > 0)
        .Select(x => (Name: x.l.Substring(0, x.i), Id: x.l.Substring(x.i + 1))).ToList();

    // ---- Interactive widgets: Luna may answer with a self-contained HTML page, shown in its own window (Widget.cs).
    const string WidgetRules =
        "INTERACTIVE WIDGETS: when the answer is best as something interactive or visual (calculator, converter, chart or graph, simulation, game, quiz or " +
        "flashcards, planner or tracker, form, visualizer, mini-app) reply with ONE complete self-contained HTML document inside a ```html code block, preceded by a " +
        "line `TITLE: <short title>`. Rules: inline CSS and JavaScript only; no external files, fonts, CDNs or network (all network access is blocked); it must work " +
        "offline, fit any window size, have large clear controls, and real working logic (not a mock-up). Style: dark theme, body background #000000, cards #15171b, " +
        "borders #24272e, text #f3f4f6, muted text #9a9fa9, one accent #a9d8ff, font-family 'Segoe UI', rounded corners, no alert(). Keep it focused and polished. " +
        "For a plain fact or short explanation, do NOT use a widget: reply in plain text.\n";

    static readonly Regex WidgetAskRx = new(
        @"\b(?:make|build|create|generate|design|code|give|show|draw|plot|visuali[sz]e|simulate|set up|whip up)\b.*\b(?:widget|interactive|dashboard|simulat\w+|visuali[sz]\w+|chart|graph|plot|calculator|quiz|flashcards?|game|tracker|planner|mini ?app|app|tool|converter|generator|picker|timeline|diagram|slider|animation)\b|^(?:plot|graph|chart|visuali[sz]e|simulate|animate)\b|\b(?:interactive|widget)\b",
        RegexOptions.IgnoreCase);
    static bool WidgetAsk(string r) => WidgetAskRx.IsMatch(r);
    internal static bool IsWidgetRequest(string r) => WidgetAsk(r);
    static string lastWidgetTitle;

    internal static string WidgetsDir => Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Paths.MeetingsDir), "Widgets")).FullName;

    internal static void OpenWidget(string file, string title)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false };
            psi.ArgumentList.Add("--widget"); psi.ArgumentList.Add(file); psi.ArgumentList.Add(title);
            Process.Start(psi);
        }
        catch (Exception ex) { Log("couldn't open the widget: " + ex.Message); }
    }

    // If the reply contains an HTML page, saves it, opens it, and returns the reply without it (null if there was none).
    static string TryWidget(string reply)
    {
        var m = Regex.Match(reply, @"```html\s*(?<h>.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        string html = m.Success ? m.Groups["h"].Value.Trim()
            : Regex.IsMatch(reply.TrimStart(), @"^<(?:!doctype|html)", RegexOptions.IgnoreCase) ? reply.Trim() : null;
        if (html == null || html.Length < 80) return null;
        string title = Regex.Match(reply, @"(?im)^\s*TITLE:\s*(.+?)\s*$").Groups[1].Value.Trim('*', '`', ' ');
        if (title.Length == 0) title = Regex.Match(html, @"<title>\s*(.*?)\s*</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value.Trim();
        if (title.Length == 0) title = "Luna";
        string safe = Regex.Replace(title, @"[^A-Za-z0-9 _-]", "").Trim().Replace(' ', '-');
        string file = Path.Combine(WidgetsDir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + (safe.Length > 0 ? safe : "widget") + ".html");
        File.WriteAllText(file, html, new UTF8Encoding(false));
        if (DryRun) return "DRY RUN: widget \"" + title + "\"";
        lastWidgetTitle = title;
        OpenWidget(file, title);
        string rest = m.Success ? reply.Remove(m.Index, m.Length) : "";
        rest = Regex.Replace(rest, @"(?im)^\s*TITLE:.*$", "").Trim();
        return rest;
    }

    static readonly Regex QuestionStart = new(@"^(?:what|what's|whats|who|who's|whom|whose|when|where|why|how|which|is|are|was|were|do|does|did|can|could|should|would|will|explain|define|tell me|describe|compare|translate|calculate|solve|summari[sz]e|list|name|spell|recommend|suggest|give me|write me|write a|write an|help me understand|teach me|rewrite|rephrase|fix this|proofread|correct)\b", RegexOptions.IgnoreCase);
    static readonly Regex AboutThisPc = new(@"\b(my (?:pc|computer|laptop|files?|folders?|desktop|downloads|screen|battery|disk|drive)|on this (?:pc|computer|laptop)|installed|running (?:processes|apps|programs)|this (?:folder|file|directory|window)|task manager|my network)\b", RegexOptions.IgnoreCase);
    // Things that need hands (shell, files, git), not just knowledge: "Do a git merge", "Can you just fix the merge", "can you update my-app".
    // "do/can/could/would/will/should" used to mark these as questions, which ran them read-only with the shell off ("I can't run git from this voice session").
    static readonly Regex DoSomething = new(@"\b(?:git|github|merge|rebase|commit|branch|checkout|push|pull|fetch|clone|repo|repository|version|release|deploy|build|compile|install|uninstall|update|upgrade|fix|debug|patch|set\s?up|configure|create|make|add|change|edit|modify|rename|move|copy|delete|remove|download|run|restart|apply|launch|open|send|sync|hook|script|spicetify|spotify|ritme|figure\s+(?:it|this|that)\s+out|sort\s+(?:it|this|that)\s+out|handle\s+(?:it|this|that))\b|\.(?:cs|js|py|ps1|json|md)\b", RegexOptions.IgnoreCase);
    static bool IsQuestion(string r)
    {
        r = r.Trim();
        // A short reply typed under an answer keeps that exchange's mode: after a task it stays a task, after a question it stays a question unless it names an action.
        if (activeForced && LastTurn() is Turn prior && r.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 8)
            return !prior.Task && !DoSomething.IsMatch(r) && !AboutThisPc.IsMatch(r);
        if (!QuestionStart.IsMatch(r) || AboutThisPc.IsMatch(r)) return false;
        // "write/rewrite/summarise/translate..." are knowledge tasks even though they mention "version" or "update"; only the auxiliary starters can hide an action.
        bool auxiliary = Regex.IsMatch(r, @"^(?:do|does|did|can|could|would|will|should)\b", RegexOptions.IgnoreCase);
        if (Regex.IsMatch(r, @"^do\s+(?!you\b|i\b|we\b|they\b|people\b)", RegexOptions.IgnoreCase)) return false;   // "Do a git merge", "Do the thing": an instruction, not "do you..."
        if (auxiliary && DoSomething.IsMatch(r)) return false;
        var last = LastTurn();
        if (FollowUpStart.IsMatch(r) && last != null && last.Task) return false;
        return true;
    }

    internal static string Mode(string request) => (WidgetAsk(request) ? "widget" : IsQuestion(request) ? "question (read-only, no shell)" : "task (shell + git)");

    // "and now push it", "yes do it", "go ahead", "also bump the version": continues the previous exchange, in the same mode.
    static readonly Regex FollowUpStart = new(@"^(?:and|also|now|then|next|ok(?:ay)?|yes|yeah|yep|sure|go ahead|do it|continue|keep going|try again|again|retry|why (?:not|did)|what (?:happened|went wrong|about)|how about|so)\b", RegexOptions.IgnoreCase);
    static readonly TimeSpan FollowWindow = TimeSpan.FromMinutes(10);

    // The last two exchanges, so a follow-up ("and in kilometres?", "now push it") makes sense for a few minutes, in questions and tasks alike.
    // Exchanges live in conversation threads (Threads.cs, persisted); the run in progress belongs to activeThread.
    // A reply to a shown answer (activeForced) uses its thread whatever its age; a voice follow-up only within FollowWindow.
    internal static string activeThread;
    internal static bool activeForced;
    static void Remember(string q, string a, bool task = false, string tid = null)
    {
        if (string.IsNullOrWhiteSpace(a) || a.StartsWith("DRY RUN")) return;
        Threads.Append(tid ?? activeThread, q, a, task);
    }

    // The thread's last exchange if it still counts as "just now" for this request.
    static Turn LastTurn()
    {
        var t = activeThread != null ? Threads.Get(activeThread) : Threads.Newest(FollowWindow);
        var last = t?.Turns.LastOrDefault();
        return last != null && (activeForced || DateTime.Now - last.At < FollowWindow) ? last : null;
    }

    // The "earlier in this conversation" block, empty when nothing recent.
    static string History()
    {
        if (LastTurn() == null) return "";
        return Threads.History(activeThread ?? Threads.Newest(FollowWindow)?.Id);
    }

    // Answers open in a box at the corner of the screen that renders markdown and SVG, read while the user keeps working.
    const string AnswerStyle =
        "ANSWER FORMAT: your reply is shown in a small answer box that renders markdown. First line: the answer itself in one plain sentence (it may also be read aloud). " +
        "A simple fact stops there. Anything that deserves it then gets a complete, well-organised explanation rather than a clipped one: short `##` headings, bullets, **bold** key terms, " +
        "a table for comparisons, code blocks for code or commands, worked steps for maths (plain text or Unicode, no LaTeX). " +
        "When a picture explains it better (a process, a structure, a timeline, a comparison, geometry, a circuit, how parts connect) include a DIAGRAM as one ```svg block: " +
        "a complete <svg> with a viewBox about 640 wide, no background rectangle, lines and shapes stroke #a9d8ff or #9a9fa9 (stroke-width 1.5-2, fills none or #15171b), text fill #f3f4f6 (labels) or #9a9fa9 (notes), " +
        "font-family Segoe UI, font-size 13-15, nothing overlapping, every element labelled; no scripts, images or external references. Do not describe the diagram in words as well. " +
        "No preamble, no restating the question, no closing offer of more help.\n\n";

    const string ScreenNote ="A screenshot of the window the user is looking at is attached; \"this\", \"that\" and \"it\" probably refer to what is in it. Use it only if the request needs it. If they want something completed, written or answered in what is on screen (a document, form or box), read it from the screenshot and put the text in a TYPE: hook so it is typed where their cursor is; do not try to open the document through Drive or other connectors, and do not ask them to paste it.\n\n";

    static string Prompt(string request, bool question, bool widget)
    {
        var sb = new StringBuilder();
        if (widget)
        {
            sb.Append("You are Luna, a voice assistant that builds small interactive tools. The user wants an interactive widget: reply with the TITLE line and ONE ")
              .Append("complete HTML document in a ```html block, and nothing else. Do not run commands or look at files. The request was dictated by voice, so ")
              .Append("expect small speech-to-text slips and build what was most likely meant.\n\n").Append(WidgetRules);
            sb.Append("\nRequest: ").Append(request).Append('\n');
            AppendContext(sb, request);
            return sb.ToString();
        }
        if (question)
        {
            sb.Append("You are Luna, the user's personal assistant (think Jarvis: capable, direct, a little dry, never gushing). Answer the question from your own knowledge")
              .Append(" (use web search when it is about something current or you are not sure). Do not run commands, do not look at files, and never ")
              .Append("mention workspaces, folders or files: they are irrelevant. The question was dictated by voice, so expect small speech-to-text slips ")
              .Append("(numbers and symbols especially: \"w 3 3\" could be \"3 + 3\") and answer what was most likely meant instead of saying you can't find something.\n\n")
              .Append(AnswerStyle)
              .Append(WidgetRules).Append(Assistant.HookHelpFor(request)).Append(Planner.PromptBlock(request)).Append(Assistant.MemoryBlock()).Append(screenNote).Append(extraBlock).Append(projectBlock).Append(resumeNote).Append("Time now: ").Append(DateTime.Now.ToString("dddd d MMMM yyyy, HH:mm")).Append('\n');
            sb.Append(History());
            sb.Append("\nQuestion: ").Append(request).Append('\n');
            AppendContext(sb, request);
            return sb.ToString();
        }
        sb.Append("You are Luna, the user's personal assistant on their Windows 11 PC (think Jarvis: capable, direct, gets things done). The request was dictated by voice, so expect small ")
          .Append("speech-to-text slips and infer what was meant. Do what it asks. If it is really just a question, answer it from your own knowledge: ")
          .Append("the working folder you start in is an empty scratch folder and means nothing, so never search it or say something isn't in \"the workspace\". ")
          .Append(WidgetRules).Append(AnswerStyle).Append(Assistant.HookHelpFor(request)).Append(Planner.PromptBlock(request)).Append(Assistant.MemoryBlock()).Append(screenNote).Append(extraBlock).Append(projectBlock)
          .Append("You have internet access: a web search tool, and node can make HTTPS requests (node -e or a script using fetch; curl.exe and Invoke-WebRequest FAIL in your sandbox with a TLS credentials error, so do not use them). Your sandbox has no GitHub login, so git commands that talk to GitHub (fetch, pull, push, ls-remote) in the user's own repos go through the host: run `node vgit.js <repo name or full path> <git args>` from your working folder, e.g. `node vgit.js my-app pull --no-rebase --no-edit` or `node vgit.js C:\\Users\\me\\Desktop\\my-blog fetch` (any repo on the PC works; a project name resolves to its folder). It runs the real git with the user's login and prints the real output; its exit code is git's, so read it and react (resolve conflicts, retry, report the actual error). Public repos you may clone directly. ")
          .Append("You also have the user's GitHub connector (tools named github.*, signed in as the user's account; works even in light mode): use it for their GitHub account and a repo's remote state (repos, pull requests, issues, branches, commits on GitHub); git in the local folders stays on the shell. It only sees repos the ChatGPT GitHub app has been granted, so a 404 from it means no access, not that the repo is missing: fall back to `node vgit.js` for git operations on it and say that the connector lacks access. ")
          .Append("You can run PowerShell (sandboxed) to look things up, check the PC ")
          .Append("and do small tasks. Your sandbox CANNOT launch apps, websites or files: never use Start-Process, explorer, start or URI links for that. ")
          .Append("Instead, to open something put one line `OPEN: <target>` in your reply per thing, where target is an app's plain name as it appears ")
          .Append("in the Start menu (e.g. OPEN: Spotify, OPEN: Codex, OPEN: Notepad), a full https URL (for a search use https://www.google.com/search?q=...), ")
          .Append("or the full path of a document/folder. The host opens it for you and strips those lines from what the user sees. ")
          .Append("You may also write inside the user's Downloads, Desktop, Documents, Le code, Spicetify (%APPDATA%\\spicetify, %LOCALAPPDATA%\\spicetify) and Spotify folders when the request needs it ")
          .Append("(e.g. copying an extension into Spicetify's Extensions folder, editing config-xpui.ini, running `spicetify apply`); an access-denied error inside them is a bug: retry another way instead of giving up. Do not force a task to completion: if you reach a sensible answer or a clear next step for the user (for example: now run spicetify apply and restart Spotify), just say that in a sentence. ")
          .Append("Be careful: never delete, overwrite or move the user's files, ")
          .Append("install software, change system settings, or send/post anything unless the request explicitly asks for it. ")
          .Append("For questions, answer directly in a few short sentences (markdown is fine). If you only carried out an action and there is ")
          .Append("nothing worth telling the user, reply with nothing at all (an empty reply). If you couldn't do it, say why in one sentence.\n\n")
          .Append("WORK, DON'T TALK: you are an agent with a shell, not a chat bot. Never reply with a promise or a restatement of the request (\"I'll figure out the merge\", \"I'll look into it\") and never say you can't do something you haven't tried: run the commands in this same turn, look at the output, fix what went wrong, and only then reply with what you actually did and found (commit hashes, versions, file names, errors), then the one thing left for the user if any. Work through multi-step jobs yourself, step by step, checking each result before the next. When you cite a commit hash, name the repo and say whether it is pushed to origin or only exists locally (check with `git branch -r --contains <hash>`), because the user will look for it on GitHub and rewritten history can leave local-only hashes. You have up to 30 minutes, so long jobs are fine, but do not over-verify: one check that the result is right is enough, and never re-run a slow command just to look at its output again. A command that may take over a minute (installs, builds, network git, big searches) should be run in smaller pieces or in the background and checked on. Take the obvious reading of a vague request (\"figure it out\" = inspect the repo or situation first, decide, then act). Ask a question back only when two readings would do different damage. ")
          .Append("Git: in the user's own repos (the folders under Le code, which you may write to) local git works for you: status, diff, log, branch, checkout, merge, commit, tag, stash, and editing files to resolve conflicts or bump a version number (package.json, manifest, a VERSION constant, a changelog). Use `git -C <repo>` and, if git complains about dubious ownership, `-c safe.directory=*`. Only the network steps go through `node vgit.js` (fetch, pull, push, ls-remote): a merge conflict from a pull is yours to resolve in the files, then `git add` and `git commit` locally. Push works only when the user's request says push (the bridge refuses otherwise, and never force-pushes or deletes); when the last step would be a push they didn't ask for, do everything before it and say it is ready to push. Never rewrite history or delete branches unless the request says so.\n\n")
          .Append(resumeNote).Append("Time now: ").Append(DateTime.Now.ToString("dddd d MMMM yyyy, HH:mm")).Append(History()).Append("\n\nRequest: ").Append(request).Append('\n');
        AppendContext(sb, request);
        return sb.ToString();
    }

    // Context only when the request points at something ("this", "that", "selected", "clipboard"...).
    static void AppendContext(StringBuilder sb, string request)
    {
        if (!NeedsContext.IsMatch(request)) return;
        string title = ForegroundTitle(), clip = ClipboardText();
        if (title.Length > 0) sb.Append("\n(Window the user is looking at: ").Append(title).Append(")\n");
        if (clip.Length > 0) sb.Append("\nClipboard text, in case the request refers to it:\n").Append(clip.Length > 8000 ? clip.Substring(0, 8000) + "\n[truncated]" : clip).Append('\n');
    }

    internal static string LastShown;   // last answer text, for "copy that answer" / "read that out loud"

    internal static void Show(string request, string answer, string threadId = null)
    {
        LastShown = answer;
        try
        {
            string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "VowenLite")).FullName;
            string file = Path.Combine(dir, "luna-" + Guid.NewGuid().ToString("N") + ".md");
            File.WriteAllText(file, (threadId != null ? "thread:" + threadId + "\n" : "") + request + "\n\n" + answer, new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo(Environment.ProcessPath, "--answer \"" + file + "\"") { UseShellExecute = false });
        }
        catch (Exception ex) { Log("couldn't open the answer window: " + ex.Message); }
    }

    static string ForegroundTitle()
    {
        try
        {
            var sb = new StringBuilder(256);
            GetWindowText(GetForegroundWindow(), sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return ""; }
    }

    internal static string ClipboardText()
    {
        try
        {
            if (!OpenClipboard(IntPtr.Zero)) return "";
            try
            {
                IntPtr h = GetClipboardData(13 /* CF_UNICODETEXT */);
                if (h == IntPtr.Zero) return "";
                IntPtr p = GlobalLock(h);
                if (p == IntPtr.Zero) return "";
                try { return Marshal.PtrToStringUni(p) ?? ""; } finally { GlobalUnlock(h); }
            }
            finally { CloseClipboard(); }
        }
        catch { return ""; }
    }

    internal static void Log(string s)
    {
        try { File.AppendAllText(Path.Combine(Paths.DataDir, "daemon.log"), DateTime.Now.ToString("s") + " luna: " + s + Environment.NewLine); } catch { }
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
}
