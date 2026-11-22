using System.Text.Json;
using System.Text.RegularExpressions;

namespace VowenLite;

// A suggested planner item waiting for the user's yes (from "plan this", the promise catcher, a lecture summary...).
sealed class Proposal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "task";          // "task" | "event"
    public string Title { get; set; } = "";
    public DateTime? Start { get; set; }                // task: due date; event: start
    public DateTime? End { get; set; }
    public bool AllDay { get; set; }
    public string Cat { get; set; } = "";
    public int Priority { get; set; }
    public string Notes { get; set; } = "";
    public string Source { get; set; } = "";            // same values as Item.Source
    public string Link { get; set; } = "";
    public int Est { get; set; }
    public string Why { get; set; } = "";               // short quote of where it came from ("…I'll send it Friday…")
    public DateTime Created { get; set; } = DateTime.Now;
}

// The planner inbox: inbox.json, shared by the daemon and the Plan window (its own process), so every change goes through a named mutex.
// A card ("3 suggestions") shows while it is not empty; clicking it opens the Plan window on its inbox view.
static class Inbox
{
    static string FilePath => Path.Combine(Paths.DataDir, "inbox.json");
    static readonly Mutex mx = new(false, "Local\\VowenLite.Inbox");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static List<Proposal> Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Proposal>>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }

    static void Edit(Action<List<Proposal>> change)
    {
        bool got = false;
        try { got = mx.WaitOne(3000); } catch (AbandonedMutexException) { got = true; }
        try
        {
            var l = Load();
            change(l);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(l, Opts));
        }
        finally { if (got) mx.ReleaseMutex(); }
        Changed();
    }

    // Same thing on the same day = duplicate ("Read ch. 14" due Mon twice, or already an open planner task).
    internal static string Key(string title, DateTime? when)
    {
        string t = Regex.Replace((title ?? "").ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
        t = Regex.Replace(t, @"^(?:the|a|an|to)\s+", "");
        return t + "|" + (when?.ToString("yyyy-MM-dd") ?? "");
    }

    // Adds what isn't already in the inbox or open in the planner. Returns how many were added.
    public static int Add(IEnumerable<Proposal> items)
    {
        var list = items.Where(p => p != null && !string.IsNullOrWhiteSpace(p.Title)).ToList();
        if (list.Count == 0) return 0;
        var known = new HashSet<string>(Planner.Live().Where(i => !(i.Kind == "task" && i.Done)).Select(i => Key(i.Title, i.Start)));
        int added = 0;
        if (Luna.DryRun) return list.Count(p => !known.Contains(Key(p.Title, p.Start)));
        Edit(l =>
        {
            foreach (var k in l) known.Add(Key(k.Title, k.Start));
            foreach (var p in list)
            {
                if (!known.Add(Key(p.Title, p.Start))) continue;
                p.Title = p.Title.Trim();
                l.Add(p); added++;
            }
        });
        if (added > 0) hidden = false;
        return added;
    }

    // Moves one proposal into the planner (and so to the phone). Returns the new planner item, or null if it was gone.
    public static Item Accept(string id)
    {
        Proposal p = null;
        Edit(l => { p = l.Find(x => x.Id == id); if (p != null) l.Remove(p); });
        return p == null ? null : ToPlanner(p);
    }

    public static int AcceptAll()
    {
        List<Proposal> all = null;
        Edit(l => { all = l.ToList(); l.Clear(); });
        foreach (var p in all) ToPlanner(p);
        return all.Count;
    }

    public static void Dismiss(string id) => Edit(l => l.RemoveAll(x => x.Id == id));
    public static int DismissAll() { int n = 0; Edit(l => { n = l.Count; l.Clear(); }); return n; }

    static Item ToPlanner(Proposal p)
    {
        Item it = p.Kind == "event" && p.Start != null
            ? Planner.AddEvent(p.Title, p.Start.Value, p.End, p.AllDay, cat: p.Cat, prio: p.Priority, notes: p.Notes)
            : Planner.AddTask(p.Title, p.Start, p.Notes, null, p.Cat, p.Priority);
        if (p.Source.Length > 0 || p.Link.Length > 0 || p.Est > 0)
            Planner.Update(it.Id, x => { x.Source = p.Source; x.Link = p.Link; x.Est = p.Est; });
        return it;
    }

    // ---------------- the card (daemon)

    static int count = -1;                 // cached; -1 = re-read the file
    static DateTime fileStamp;
    static long checkedAt;
    static bool hidden;                    // x on the card: hide it until something new arrives

    static void Changed() { count = -1; Overlay.ChipRefresh(); }

    static int Count()
    {
        long now = Environment.TickCount64;
        if (count >= 0 && now - checkedAt < 5000) return count;
        checkedAt = now;
        var stamp = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
        if (count < 0 || stamp != fileStamp) { fileStamp = stamp; count = Load().Count; }
        return count;
    }

    public static IEnumerable<Row> Rows()
    {
        int n = Count();
        if (n == 0 || hidden) yield break;
        yield return new Row(CardIcon.Dot, n == 1 ? "1 suggestion" : n + " suggestions", "planner inbox · click to review", 0, "inbox");
    }

    public static void Activate(string id) => Planner.OpenWindow("inbox");
    public static void DismissCard(string id) => hidden = true;

    // ---------------- voice / typed

    public static string TryLocal(string l, Settings s)
    {
        if (Regex.IsMatch(l, @"^(?:open |show |check |what'?s in )?(?:my |the )?(?:planner )?(?:inbox|suggestions)$", RegexOptions.IgnoreCase))
            return Load().Count == 0 ? "✓ Your planner inbox is empty" : Planner.OpenWindow("inbox");
        if (Regex.IsMatch(l, @"^(?:accept|add|approve|take|yes to) (?:them all|all(?: of them)?(?: the)? suggestions?|all|the suggestions|everything in (?:my |the )?inbox)$", RegexOptions.IgnoreCase))
        {
            if (Luna.DryRun) return "DRY RUN: accept every inbox suggestion";
            int n = AcceptAll();
            return n == 0 ? "✓ Nothing in the inbox" : $"✓ Added {n} to your planner";
        }
        if (Regex.IsMatch(l, @"^(?:clear|empty|dismiss|reject|ignore)(?: all)?(?: my| the)? (?:planner )?(?:inbox|suggestions)$", RegexOptions.IgnoreCase))
        {
            if (Luna.DryRun) return "DRY RUN: clear the inbox";
            int n = DismissAll();
            return n == 0 ? "✓ Nothing in the inbox" : $"✓ Dismissed {n} suggestions";
        }
        return null;
    }

    // ---------------- tests

    public static void SelfChecks(Action<bool, string> check)
    {
        var d = new DateTime(2026, 11, 23);
        check(Key("Read Ch. 14!", d) == Key("read ch 14", d), "inbox: key ignores case and punctuation");
        check(Key("The essay", d) == Key("essay", d), "inbox: key drops a leading article");
        check(Key("essay", d) != Key("essay", d.AddDays(1)), "inbox: key includes the day");
    }

    public static int Test(string[] args)
    {
        int fails = 0;
        void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL " + what); } }
        DismissAll();
        var due = DateTime.Today.AddDays(3);
        int n = Add(new[] { new Proposal { Title = "Inboxtest read chapter 14", Start = due, Source = "lecture", Link = "https://example.com/x", Est = 45 },
                            new Proposal { Title = "inboxtest READ chapter 14.", Start = due },
                            new Proposal { Title = "Inboxtest call Sam" } });
        Check(n == 2, "duplicates dropped on add (got " + n + ")");
        Check(Load().Count == 2, "two in the file");
        var first = Load().First(p => p.Title.Contains("chapter"));
        var it = Accept(first.Id);
        Check(it != null && Planner.Live().Any(i => i.Id == it.Id && i.Source == "lecture" && i.Link == "https://example.com/x" && i.Est == 45), "accept keeps source, link and estimate");
        Check(Add(new[] { new Proposal { Title = "Inboxtest read chapter 14", Start = due } }) == 0, "an open planner task blocks a duplicate");
        Check(DismissAll() == 1, "dismiss all");
        foreach (var x in Planner.Live().Where(i => i.Title.StartsWith("Inboxtest", StringComparison.OrdinalIgnoreCase))) Planner.Remove(x.Id);
        Console.WriteLine(fails == 0 ? "inboxtest ok" : fails + " failed");
        return fails == 0 ? 0 : 1;
    }
}
