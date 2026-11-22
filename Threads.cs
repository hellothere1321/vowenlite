using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VowenLite;

sealed class Turn { public string Q { get; set; } = ""; public string A { get; set; } = ""; public bool Task { get; set; } public bool Web { get; set; } public DateTime At { get; set; } }
// Pending = a request that stopped to wait for the user (a sign-in, a failed run): "done" / "go on" resumes exactly that request.
sealed class ConvThread { public string Id { get; set; } = ""; public DateTime Updated { get; set; } public List<Turn> Turns { get; set; } = new(); public string Pending { get; set; } }

// Conversation threads: every answer belongs to one, so it can be replied to (answer box, typing box) even after the daemon restarts.
// Kept in threads.json (recent 8, last 6 exchanges each, dropped after 24 h). Shared by the daemon (writes) and the answer / ask
// processes (read), so every call re-reads the file when it changed.
static class Threads
{
    public const int MaxThreads = 8, MaxTurns = 6, MaxAnswer = 900, MaxQuestion = 400, MaxThreadChars = 6000;
    public static readonly TimeSpan Expiry = TimeSpan.FromHours(24);
    static readonly object gate = new();
    static List<ConvThread> list;
    static DateTime loadedStamp;
    static string FilePath => Path.Combine(Paths.DataDir, "threads.json");

    public static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 12);

    static void Load()
    {
        try
        {
            var fi = new FileInfo(FilePath);
            var stamp = fi.Exists ? fi.LastWriteTimeUtc : default;
            if (list != null && stamp == loadedStamp) return;
            list = fi.Exists ? JsonSerializer.Deserialize<List<ConvThread>>(File.ReadAllText(FilePath)) ?? new() : new();
            loadedStamp = stamp;
        }
        catch { list ??= new(); }
        Prune();
    }

    static void Prune()
    {
        list.RemoveAll(t => t.Turns.Count == 0 || DateTime.Now - t.Updated > Expiry);
        list.Sort((a, b) => b.Updated.CompareTo(a.Updated));
        if (list.Count > MaxThreads) list.RemoveRange(MaxThreads, list.Count - MaxThreads);
    }

    static void Save()
    {
        try
        {
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list));
            File.Move(tmp, FilePath, true);
            loadedStamp = new FileInfo(FilePath).LastWriteTimeUtc;
        }
        catch { }
    }

    // Forget the in-memory copy (tests: simulates a daemon restart).
    public static void Reset() { lock (gate) { list = null; loadedStamp = default; } }

    public static ConvThread Get(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (gate) { Load(); return list.FirstOrDefault(t => t.Id == id); }
    }

    public static ConvThread Newest(TimeSpan within)
    {
        lock (gate) { Load(); var t = list.FirstOrDefault(); return t != null && DateTime.Now - t.Updated <= within ? t : null; }
    }

    public static Turn Last(string id) => Get(id)?.Turns.LastOrDefault();

    // Adds an exchange (creating the thread when the id is new); returns the id. web = the run used the background browser.
    public static string Append(string id, string q, string a, bool task, bool web = false)
    {
        if (string.IsNullOrWhiteSpace(a)) return id;
        lock (gate)
        {
            Load();
            if (string.IsNullOrEmpty(id)) id = NewId();
            var t = list.FirstOrDefault(x => x.Id == id);
            if (t == null) { t = new ConvThread { Id = id }; list.Add(t); }
            t.Turns.Add(new Turn { Q = Clip(q, MaxQuestion), A = Clip(a, MaxAnswer), Task = task, Web = web, At = DateTime.Now });
            t.Updated = DateTime.Now;
            while (t.Turns.Count > MaxTurns) t.Turns.RemoveAt(0);
            while (t.Turns.Count > 1 && t.Turns.Sum(x => x.Q.Length + x.A.Length) > MaxThreadChars) t.Turns.RemoveAt(0);
            Prune();
            Save();
            return id;
        }
    }

    // The "earlier in this conversation" prompt block for a thread: its last 3 exchanges, newest last.
    public static string History(string id)
    {
        var t = Get(id);
        if (t == null || t.Turns.Count == 0) return "";
        var sb = new StringBuilder("\nEarlier in this conversation (newest last):\n");
        foreach (var x in t.Turns.Skip(Math.Max(0, t.Turns.Count - 3)))
            sb.Append("- The user asked \"").Append(x.Q).Append("\" and you answered \"").Append(x.A).Append("\"\n");
        sb.Append("The new request may continue this (\"and now...\", \"do it\", \"done\", \"I'm signed in\", \"go on\", \"why not?\", \"the same for X\"); if it does, carry on from where that left off instead of starting over or asking what they mean.\n");
        return sb.ToString();
    }

    // ---- "done" / "I'm signed in" / "go on" / "ok try now": the user did what Luna was waiting for and wants the request finished.
    // These used to run as a request of their own ("I don't have another action to finish from 'done'"), or were dropped as one-word mis-hearings.
    const string Core = @"(?:(?:all\s+)?done|finished|ready|(?:signed|logged)\s+(?:in|on)|did\s+it|(?:go|carry|keep|move)\s+on|keep\s+going|continue|proceed|resume|go\s+ahead|(?:try|go)\s+(?:it\s+)?again|(?:try|go|continue)\s+(?:it\s+)?now)(?:\s+(?:now|then|please|again|already|there))*";
    const string Who = @"(?:(?:i['’]?m|i\s+am|i['’]?ve|i\s+have|i|we['’]?re|we|all|it['’]?s|that['’]?s|you\s+can)\s+)?(?:just\s+)?";
    static readonly Regex ContinueRx = new(@"^\W*(?:(?:ok(?:ay)?|alright|all\s+right|right|so|yes|yeah|yep|great|cool|hey|luna)\W+)*" + Who + Core
        + @"(?:\W+(?:and\s+|so\s+)?" + Who + Core + @")*(?:\W+(?:thanks|thank\s+you|please))?\W*$", RegexOptions.IgnoreCase);
    public static bool IsContinue(string text) => !string.IsNullOrWhiteSpace(text) && ContinueRx.IsMatch(text.Trim());

    static readonly TimeSpan PendingWindow = TimeSpan.FromHours(3), RecentWindow = TimeSpan.FromMinutes(30);

    // The request a continuation word resumes, and its thread. The given thread (a reply), else the newest thread still waiting on the user,
    // else (unless pendingOnly) the newest thread from the last 30 min. Its pending request wins; otherwise its newest request that wasn't itself
    // a "done". Null = not a continuation word, or nothing to continue.
    public static string Resume(string text, string id, out string tid, bool pendingOnly = false)
    {
        tid = null;
        if (!IsContinue(text)) return null;
        lock (gate)
        {
            Load();
            var t = !string.IsNullOrEmpty(id) && id != "new" ? list.FirstOrDefault(x => x.Id == id) : null;
            t ??= list.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Pending) && DateTime.Now - x.Updated <= PendingWindow)
               ?? (pendingOnly ? null : list.FirstOrDefault(x => DateTime.Now - x.Updated <= RecentWindow));
            if (t == null) return null;
            string q = !string.IsNullOrWhiteSpace(t.Pending) ? t.Pending
                : pendingOnly ? null : t.Turns.Select(x => x.Q).LastOrDefault(x => !string.IsNullOrWhiteSpace(x) && !IsContinue(x));
            if (string.IsNullOrWhiteSpace(q)) return null;
            tid = t.Id;
            return q;
        }
    }

    // Marks (request) or clears (null) the thread's request that is waiting on the user. No-op for a thread that doesn't exist.
    public static void SetPending(string id, string request)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (gate)
        {
            Load();
            var t = list.FirstOrDefault(x => x.Id == id);
            string v = string.IsNullOrWhiteSpace(request) ? null : Clip(request, 2000);
            if (t == null || t.Pending == v) return;
            t.Pending = v;
            Save();
        }
    }

    static string Clip(string s, int n) { s = (s ?? "").Trim(); return s.Length > n ? s.Substring(0, n) : s; }

    // Request file from the typing box / answer box: an optional first line `thread:<id>` (or `thread:new`), then the text.
    // Without that line the text is a plain request (id null).
    static readonly Regex ThreadLine = new(@"^\s*thread:([0-9a-zA-Z]{1,32})[ \t]*\r?\n", RegexOptions.IgnoreCase);
    public static string Parse(string file, out string id)
    {
        id = null;
        var m = ThreadLine.Match(file ?? "");
        if (!m.Success) return (file ?? "").Trim();
        id = m.Groups[1].Value;
        return file.Substring(m.Length).Trim();
    }
    public static string Compose(string id, string text) => string.IsNullOrEmpty(id) ? text : "thread:" + id + "\n" + text;

    // `--threadtest`: exercises the store against a scratch data dir (set VOWENLITE_DATA).
    public static int Test()
    {
        int bad = 0;
        void Check(string what, bool ok) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) bad++; }
        try { File.Delete(FilePath); } catch { }
        Reset();
        string a = Append(null, "what is rust", "A language.", false);
        Check("append creates a thread", a != null && Get(a)?.Turns.Count == 1);
        Append(a, "and go?", "Another language.", false);
        Check("append joins thread", Get(a).Turns.Count == 2);
        for (int i = 0; i < 9; i++) Append(a, "q" + i, "a" + i, i % 2 == 0);
        Check("turn cap " + MaxTurns, Get(a).Turns.Count == MaxTurns && Get(a).Turns[^1].Q == "q8");
        Append(a, "long", new string('x', 5000), false);
        Check("answer clipped to " + MaxAnswer, Get(a).Turns[^1].A.Length == MaxAnswer);
        for (int i = 0; i < 5; i++) Append(a, "big" + i, new string('y', 800), false);
        Check("character budget", Get(a).Turns.Sum(x => x.Q.Length + x.A.Length) <= MaxThreadChars);
        var ids = new List<string>();
        for (int i = 0; i < 10; i++) ids.Add(Append(null, "t" + i, "r" + i, false));
        Check("thread cap " + MaxThreads, list.Count == MaxThreads && Get(ids[9]) != null && Get(ids[0]) == null);
        Reset();
        Check("persists and reloads", Get(ids[9])?.Turns[0].Q == "t9");
        string h = History(ids[9]);
        Check("history text for id", h.Contains("\"t9\"") && h.Contains("\"r9\"") && History("nope") == "");
        Check("newest within 10 min", Newest(TimeSpan.FromMinutes(10))?.Id == ids[9]);
        // expiry: age the file's newest thread by 25 h
        var old = Get(ids[9]); old.Updated = DateTime.Now.AddHours(-25); old.Turns[0].At = old.Updated; lock (gate) Save();
        Reset();
        Check("expired after 24 h", Get(ids[9]) == null);
        Check("newest ignores old", Newest(TimeSpan.FromMinutes(10))?.Id != ids[9]);
        Check("parse thread line", Parse("thread:abc123\nwhy not?", out string pid) == "why not?" && pid == "abc123");
        Check("parse CRLF", Parse("thread:abc123\r\nhello", out pid) == "hello" && pid == "abc123");
        Check("parse new", Parse("thread:new\nhi", out pid) == "hi" && pid == "new");
        Check("parse plain", Parse("  just text ", out pid) == "just text" && pid == null);
        Check("parse no false positive", Parse("thread: what is a thread?", out pid) == "thread: what is a thread?" && pid == null);
        Check("compose round trip", Parse(Compose("abc", "x y"), out pid) == "x y" && pid == "abc");

        // ---- continuation words ("done" after a sign-in) resume the waiting request
        foreach (var w in new[] { "done", "Done.", "im done", "I'm done", "i signed in", "im signed in", "I'm signed in now", "go on", "continue", "ok try now",
                                  "okay, I'm logged in, go on", "try again", "all done!", "I've signed in, continue please", "finished", "you can try now", "go ahead" })
            Check("continue word: " + w, IsContinue(w));
        foreach (var w in new[] { "done with the essay", "I'm done with it", "continue the essay", "go on youtube", "yes", "what is done", "open spotify", "try the other site", "" })
            Check("not a continue word: " + w, !IsContinue(w));
        try { File.Delete(FilePath); } catch { }
        Reset();
        Check("nothing to continue in an empty store", Resume("done", null, out _) == null);
        const string orig = "go to the course site and check my grades";
        string wt = Append(null, orig, "Sign in in the window I opened, then say done.", true, web: true);
        SetPending(wt, orig);
        string other = Append(null, "what is rust", "A language.", false);   // newer thread, nothing pending
        Check("done -> pending request (voice, no thread)", Resume("done", null, out string rt, pendingOnly: true) == orig && rt == wt);
        Check("im signed in -> pending request (reply in its thread)", Resume("im signed in", wt, out rt) == orig && rt == wt);
        Check("ok try now -> pending request (fresh conversation)", Resume("ok try now", "new", out rt) == orig && rt == wt);
        Check("reply in a thread with nothing pending -> its last request", Resume("go on", other, out rt) == "what is rust" && rt == other);
        Check("pendingOnly skips a thread with nothing pending", Resume("continue", other, out _, pendingOnly: true) == null);
        Check("not a continue word -> null", Resume("check my grades", wt, out _) == null);
        Reset();
        Check("pending survives a restart", Get(wt)?.Pending == orig && Get(wt).Turns[0].Web);
        // the resumed run is remembered under the original text, so a second "done" still means it; a finished run clears Pending
        Append(wt, orig, "Your grades: Art 101 A-.", true, web: true);
        SetPending(wt, null);
        Check("finished run clears pending", Get(wt).Pending == null && Resume("done", null, out _, pendingOnly: true) == null);
        Check("done after it -> the same request", Resume("done", wt, out rt) == orig && rt == wt);
        Append(wt, "done", "Nothing else to do.", true);   // older data stored the word itself
        Check("skips a stored done", Resume("continue", wt, out _) == orig);
        var aged = Get(other); aged.Updated = DateTime.Now.AddMinutes(-45); lock (gate) Save();
        var agedWeb = Get(wt); agedWeb.Updated = DateTime.Now.AddMinutes(-45); lock (gate) Save();
        Check("no recent thread -> nothing to continue", Resume("done", null, out _) == null);
        SetPending(wt, orig);
        var agedP = Get(wt); agedP.Updated = DateTime.Now.AddMinutes(-45); lock (gate) Save();
        Check("pending still resumes 45 min later", Resume("done", null, out rt) == orig && rt == wt);
        agedP.Updated = DateTime.Now.AddHours(-4); lock (gate) Save();
        Check("pending expires after 3 h", Resume("done", null, out _) == null);
        Check("answer waits on user: say done", Luna.WaitsOnUser("Sign in in the window I opened, then say “done”."));
        Check("answer waits on user: sign-in timeout", Luna.WaitsOnUser("The sign-in window closed or timed out before you finished. Say “try again” and I'll open it again."));
        Check("answer waits on user: once you have signed in", Luna.WaitsOnUser("Once you've signed in, I can read the gradebook."));
        Check("plain answer does not wait", !Luna.WaitsOnUser("Osmosis is water moving across a membrane toward the side with more dissolved stuff."));

        // ---- routes: knowledge questions are quick (read-only, low effort); "can you <verb>", site work and code stay tasks
        try { File.Delete(FilePath); } catch { }
        Reset();
        void Route(string req, string mode, string effort = null, bool? webOn = null)
        {
            string r = Luna.RouteTest(req);
            bool ok = r.StartsWith(mode + " ") && (effort == null || r.Contains(", " + effort + " effort")) && (webOn == null || r.Contains("+ web browser") == webOn);
            Check($"route \"{req}\" -> {r}", ok);
        }
        Route("what is osmosis", "question", "low");
        Route("how do I convert celsius to fahrenheit", "question", "low");
        Route("why is the sky blue", "question", "low");
        Route("explain how vaccines work", "question", "low");
        Route("who wrote hamlet", "question", "low");
        Route("15 times 23", "question", "low");
        Route("what's the difference between weather and climate", "question", "low");
        Route("is 91 a prime number", "question", "low");
        Route("can you explain photosynthesis", "question", "low");
        Route("go to my school site and check my grades", "task", webOn: true);
        Route("fix the build", "task", webOn: false);
        Route("can you open my readings", "task", webOn: true);
        Route("can you then just do all the readings for me and summarise them", "task", webOn: true);
        Route("do a git merge", "task", webOn: false);
        // right after a browser task: a knowledge question is still quick, a follow-up keeps the task and the browser
        Append(null, "go to the course site and open this week's module", "Opened Week 3: four readings and a quiz.", true, web: true);
        Route("what is osmosis", "question", "low", false);
        Route("now do all the readings and summarise them", "task", webOn: true);
        Route("yes", "task", webOn: true);
        Route("what's the due date", "task", webOn: true);
        // right after a question: "and in kilometres?" is more of the same question
        Append(null, "how far away is the moon", "About 239,000 miles.", false);
        Route("and in kilometres?", "question", "low", false);
        Route("and now open spotify", "task");
        Console.WriteLine(bad == 0 ? "ALL PASS" : bad + " FAILED");
        return bad == 0 ? 0 : 1;
    }
}
