using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VowenLite;

sealed class Turn { public string Q { get; set; } = ""; public string A { get; set; } = ""; public bool Task { get; set; } public DateTime At { get; set; } }
sealed class ConvThread { public string Id { get; set; } = ""; public DateTime Updated { get; set; } public List<Turn> Turns { get; set; } = new(); }

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

    // Adds an exchange (creating the thread when the id is new); returns the id.
    public static string Append(string id, string q, string a, bool task)
    {
        if (string.IsNullOrWhiteSpace(a)) return id;
        lock (gate)
        {
            Load();
            if (string.IsNullOrEmpty(id)) id = NewId();
            var t = list.FirstOrDefault(x => x.Id == id);
            if (t == null) { t = new ConvThread { Id = id }; list.Add(t); }
            t.Turns.Add(new Turn { Q = Clip(q, MaxQuestion), A = Clip(a, MaxAnswer), Task = task, At = DateTime.Now });
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
        sb.Append("The new request may continue this (\"and now...\", \"do it\", \"why not?\", \"the same for X\"); if it does, carry on from where that left off instead of starting over or asking what they mean.\n");
        return sb.ToString();
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
        Console.WriteLine(bad == 0 ? "ALL PASS" : bad + " FAILED");
        return bad == 0 ? 0 : 1;
    }
}
