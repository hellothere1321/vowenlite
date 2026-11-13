using System.Text.Json;
using System.Text.RegularExpressions;

namespace VowenLite;

public enum CardIcon { Ring, Spinner, Check, Dot, Play, Pause, Bell }

// One line of the bottom-right overlay stack.
// Detail = text shown in a panel while the card is hovered (what Luna is thinking, or the finished answer).
public readonly record struct Row(CardIcon Icon, string Primary, string Secondary, double Frac, string Id = null, string Detail = null);

// Everything that can appear in the overlay stack besides dictation: Luna working on something, timers,
// a stopwatch, and pinned notes. Snapshot() is polled by the overlay (every second, or ~20x a second
// while Luna is working so the spinner moves).
static class Cards
{
    // ---------------- Luna working

    sealed class Work { public int Id; public string Label, Request; public long Start, DoneAt; public bool Done, Failed, Keep; public string Message, Detail, Thread; public List<string> Steps = new(); }
    static readonly List<Work> works = new();
    static readonly List<System.Threading.Timer> pokes = new();   // fields: unreferenced timers can be collected before they fire
    static int nextId;

    // Shown after 0.7 s, so instant local commands never flash a card.
    public static int BeginWork(string request)
    {
        var w = new Work { Id = Interlocked.Increment(ref nextId), Label = Describe(request), Request = request, Start = Environment.TickCount64 };
        lock (works) works.Add(w);
        Poke(750);
        return w.Id;
    }

    // What Luna is doing right now ("Running Get-ChildItem ...", a reasoning summary...); shown when the working card is hovered.
    public static void Progress(int id, string line)
    {
        if (id == 0 || string.IsNullOrWhiteSpace(line)) return;
        line = Regex.Replace(line.Trim(), @"\s+", " ");
        if (line.Length > 220) line = line.Substring(0, 219) + "…";
        lock (works)
        {
            var w = works.FirstOrDefault(x => x.Id == id);
            if (w == null || w.Done) return;
            if (w.Steps.Count == 0 || w.Steps[^1] != line) { w.Steps.Add(line); if (w.Steps.Count > 40) w.Steps.RemoveAt(0); }
        }
    }

    // message null = nothing to show (finished before the card appeared, or already confirmed elsewhere).
    // detail = the full answer / error, kept on the card so a result is never lost: a card with a detail stays until dismissed
    // (click it to open the answer again), one without goes away by itself after a while.
    public static void EndWork(int id, string message, bool failed = false, string detail = null, string thread = null)
    {
        Task.Run(async () => { await Task.Delay(4000); Native.TrimSelf(); });   // a Codex job leaves a lot of garbage and touched pages behind; give them back once the result is out
        bool keep;
        lock (works)
        {
            var w = works.FirstOrDefault(x => x.Id == id);
            if (w == null) return;
            if (message == null || Environment.TickCount64 - w.Start < 700) works.Remove(w);
            else
            {
                w.Done = true; w.Failed = failed; w.Message = message; w.DoneAt = Environment.TickCount64;
                w.Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim(); w.Thread = thread;
                w.Keep = w.Detail != null;
                while (works.Count(x => x.Done && x.Keep) > 4) works.Remove(works.First(x => x.Done && x.Keep));   // oldest kept result goes first
            }
            keep = works.Any(x => x.Id == id && x.Keep);
        }
        Overlay.ChipRefresh();
        if (!keep) Poke(DoneShownMs + 200);
    }

    const int DoneShownMs = 12_000;

    static void Poke(int ms)
    {
        System.Threading.Timer t = null;
        t = new System.Threading.Timer(_ => { Overlay.ChipRefresh(); lock (pokes) pokes.Remove(t); t.Dispose(); }, null, ms, Timeout.Infinite);
        lock (pokes) pokes.Add(t);
    }

    static string Describe(string request)
    {
        string r = request.Trim().TrimEnd('.', '?', '!');
        string Trunc(string s, int n) => s.Length > n ? s.Substring(0, n - 1).TrimEnd() + "…" : s;
        if (Luna.IsWidgetRequest(r))
        {
            string what = Regex.Replace(r, @"^(?:please\s+)?(?:make|build|create|generate|design|code|give|show|draw|set up|whip up)(?:\s+me)?\s+(?:an?\s+|the\s+|some\s+)?", "", RegexOptions.IgnoreCase);
            return "Building " + Trunc(what, 28);
        }
        return Trunc(r, 34);
    }

    // ---------------- stopwatch

    static bool swExists, swRunning;
    static long swStartTick;
    static TimeSpan swAcc;
    static readonly List<TimeSpan> laps = new();

    static TimeSpan SwElapsed() => swAcc + (swRunning ? TimeSpan.FromMilliseconds(Environment.TickCount64 - swStartTick) : TimeSpan.Zero);

    // ---------------- notes (pinned to the overlay, kept across restarts)

    static List<string> notes;
    static string NotesFile => Path.Combine(Paths.DataDir, "notes.json");
    static List<string> Notes()
    {
        if (notes != null) return notes;
        try { notes = File.Exists(NotesFile) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(NotesFile)) ?? new() : new(); }
        catch { notes = new(); }
        return notes;
    }
    static void SaveNotes() { try { File.WriteAllText(NotesFile, JsonSerializer.Serialize(Notes())); } catch { } }

    // ---------------- spoken commands for stopwatch and notes (null = not one of these)

    static bool M(string l, string pattern, out Match m)
    {
        m = Regex.Match(l, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success;
    }

    public static string Try(string l)
    {
        Match m;
        // stopwatch
        if (M(l, @"^(?:start|begin|run)(?: a| the| my)? stop ?watch$|^(?:resume|continue)(?: the| my)? stop ?watch$", out _))
            return Do("start stopwatch", () => { if (!swExists) { swExists = true; swAcc = TimeSpan.Zero; laps.Clear(); } if (!swRunning) { swRunning = true; swStartTick = Environment.TickCount64; } }, "✓ Stopwatch started");
        if (M(l, @"^(?:stop|pause|end)(?: the| my)? stop ?watch$", out _))
        {
            if (!swExists) return "✓ No stopwatch running";
            string shown = Commands.Clock(SwElapsed());
            return Do("stop stopwatch", () => { if (swRunning) { swAcc = SwElapsed(); swRunning = false; } }, "✓ Stopwatch · " + shown);
        }
        if (M(l, @"^(?:reset|clear|remove|dismiss|hide)(?: the| my)? stop ?watch$", out _))
            return Do("clear stopwatch", () => { swExists = swRunning = false; swAcc = TimeSpan.Zero; laps.Clear(); }, "✓ Stopwatch cleared");
        if (M(l, @"^(?:stop ?watch )?lap$|^mark (?:a )?lap$", out _))
        {
            if (!swExists || !swRunning) return "✓ No stopwatch running";
            return Do("lap", () => laps.Add(SwElapsed()), "✓ Lap " + laps.Count + " · " + Commands.Clock(SwElapsed()));
        }

        // notes pinned on screen
        if ((M(l, @"^(?:pin|add|put|make|take|leave|write|save)(?: me)?(?: up)?(?: a| an)?(?: sticky| quick)? note(?: to self)?(?: (?:that|saying|to|about|says|reading))?[:,]?\s*(.+)$", out m)
            || M(l, @"^note(?: to self)?[:,]?\s+(.+)$", out m) || M(l, @"^remember(?: that)?\s+(.+)$", out m)) && m.Groups[1].Value.Trim().Length > 0)
        {
            string text = m.Groups[1].Value.Trim().TrimEnd('.');
            text = char.ToUpperInvariant(text[0]) + text.Substring(1);
            return Do("pin note: " + text, () => { var n = Notes(); n.Add(text); if (n.Count > 20) n.RemoveAt(0); SaveNotes(); }, "✓ Pinned · " + (text.Length > 30 ? text.Substring(0, 29) + "…" : text));
        }
        if (M(l, @"^(?:clear|remove|delete|dismiss|unpin)(?: all| my| the| that)?(?: sticky| pinned)? notes?$|^(?:clear|remove|delete|dismiss|unpin) (?:the )?(?:last|latest|newest) note$|^(?:clear|remove|delete|dismiss|unpin) (?:the )?(?:first|oldest) note$", out m))
        {
            bool all = !Regex.IsMatch(l, @"\b(last|latest|newest|first|oldest)\b", RegexOptions.IgnoreCase) && !M(l, @"\bthat\b", out _);
            bool first = Regex.IsMatch(l, @"\b(first|oldest)\b", RegexOptions.IgnoreCase);
            return Do("remove notes", () =>
            {
                var n = Notes();
                if (n.Count == 0) return;
                if (all || n.Count == 1) n.Clear(); else if (first) n.RemoveAt(0); else n.RemoveAt(n.Count - 1);
                SaveNotes();
            }, "✓ Note removed");
        }
        if (M(l, @"^(?:what(?:'s| are| is) )?(?:my )?(?:pinned |sticky )?notes$|^(?:show|read)(?: me)?(?: my)?(?: pinned| sticky)? notes$", out _))
        {
            var n = Notes();
            return n.Count == 0 ? "✓ No notes pinned" : string.Join("\n", n.Select((x, i) => $"{i + 1}. {x}"));
        }
        return null;
    }

    static string Do(string what, Action a, string confirm)
    {
        if (Luna.DryRun) return "DRY RUN: " + what;
        try { a(); Overlay.ChipRefresh(); return confirm; }
        catch (Exception ex) { return "I couldn't do that: " + ex.Message; }
    }

    // ---------------- ringing alarms: ring until clicked (or "stop") instead of beeping once and vanishing

    sealed class Ring { public string Id, Title, Text; public CancellationTokenSource Cts; }
    static readonly List<Ring> rings = new();

    public static bool IsRinging { get { lock (rings) return rings.Count > 0; } }

    public static void StartRinging(string title, string text)
    {
        var r = new Ring { Id = Guid.NewGuid().ToString("N"), Title = title, Text = text, Cts = new CancellationTokenSource() };
        lock (rings) rings.Add(r);
        Overlay.ChipRefresh();
        var ct = r.Cts.Token;
        Task.Run(async () =>
        {
            for (int i = 0; i < 12 && !ct.IsCancellationRequested; i++)   // ~85 s, then it goes quiet but the card stays
            {
                Feedback.Pattern();
                try { await Task.Delay(7000, ct); } catch { break; }
            }
        });
    }

    public static bool StopRinging()
    {
        List<Ring> list;
        lock (rings) { list = rings.ToList(); rings.Clear(); }
        foreach (var r in list) r.Cts.Cancel();
        if (list.Count > 0) Overlay.ChipRefresh();
        return list.Count > 0;
    }

    // ---------------- clicks on cards (see Overlay: x button / right-click = dismiss, body click = activate)

    public static void Dismiss(string id)
    {
        if (id == null) return;
        if (id.StartsWith("timer:")) Commands.CancelTimer(id.Substring(6));
        else if (id.StartsWith("ring:"))
        {
            Ring r;
            lock (rings) { r = rings.FirstOrDefault(x => x.Id == id.Substring(5)); if (r != null) rings.Remove(r); }
            r?.Cts.Cancel();
        }
        else if (id.StartsWith("work:")) { Luna.CancelRunning(); lock (works) works.RemoveAll(w => w.Id.ToString() == id.Substring(5)); }
        else if (id.StartsWith("done:")) { lock (works) works.RemoveAll(w => w.Id.ToString() == id.Substring(5)); }
        else if (id == "sw") { swExists = swRunning = false; swAcc = TimeSpan.Zero; laps.Clear(); }
        else if (id.StartsWith("note:")) { var n = Notes(); int i = n.FindIndex(x => x == id.Substring(5)); if (i >= 0) { n.RemoveAt(i); SaveNotes(); } }
        Overlay.ChipRefresh();
    }

    public static void Activate(string id)
    {
        if (id == "sw")
        {
            if (swRunning) { swAcc = SwElapsed(); swRunning = false; }
            else { swRunning = true; swStartTick = Environment.TickCount64; }
            Overlay.ChipRefresh();
        }
        else if (id != null && id.StartsWith("ring:")) Dismiss(id);
        else if (id != null && id.StartsWith("note:")) Luna.Show("Pinned note", id.Substring(5));   // click = the whole note in a window (copyable)
        else if (id != null && id.StartsWith("done:"))
        {
            // A finished card with an answer opens it again (the x button dismisses); one without just goes away.
            Work w;
            lock (works) w = works.FirstOrDefault(x => x.Id.ToString() == id.Substring(5));
            if (w?.Detail != null && !w.Failed) Luna.Show(w.Request, w.Detail, w.Thread);
            else Dismiss(id);
        }
    }

    // ---------------- what the overlay draws

    public static List<Row> Snapshot()
    {
        var rows = new List<Row>();
        long now = Environment.TickCount64;

        lock (rings)
            foreach (var r in rings) rows.Add(new Row(CardIcon.Bell, "Time!", r.Title == "Timer" || r.Title == "Reminder" ? ShortText(r.Text) : r.Title, 0, "ring:" + r.Id));

        lock (works)
        {
            works.RemoveAll(w => w.Done && !w.Keep && now - w.DoneAt > DoneShownMs);
            foreach (var w in works)
            {
                if (w.Done)
                    rows.Add(new Row(w.Failed ? CardIcon.Dot : CardIcon.Check, w.Failed ? "Failed" : "Done",
                        w.Message, 0, "done:" + w.Id, w.Detail != null ? Clip(w.Detail, 1400) : null));
                else if (now - w.Start >= 700)
                    rows.Add(new Row(CardIcon.Spinner, Commands.Clock(TimeSpan.FromMilliseconds(now - w.Start)), w.Label, 0, "work:" + w.Id,
                        Thinking(w)));
            }
        }

        foreach (var t in Commands.Running())
            rows.Add(new Row(CardIcon.Ring, Commands.Clock(t.Left), (t.Label ?? "Timer") + (t.Repeating ? "  ↻" : ""),
                t.Total.TotalSeconds > 0 ? Math.Clamp(t.Left.TotalSeconds / t.Total.TotalSeconds, 0, 1) : 0, "timer:" + t.Id));

        if (swExists)
            rows.Add(new Row(swRunning ? CardIcon.Play : CardIcon.Pause, Commands.Clock(SwElapsed()), laps.Count > 0 ? $"Stopwatch · lap {laps.Count}" : "Stopwatch", 0, "sw"));

        var n = Notes();
        foreach (var note in n.TakeLast(3).Reverse()) rows.Add(new Row(CardIcon.Dot, note, "", 0, "note:" + note, note.Length > 26 ? note : null));   // hover = full text (only when the card truncates it)
        if (n.Count > 3) rows.Add(new Row(CardIcon.Dot, $"+{n.Count - 3} more notes", "", 0, null, string.Join("\n\n", n.Take(n.Count - 3).Reverse().Select(x => "• " + x))));

        return rows.Take(6).ToList();
    }

    static string Clip(string t, int n) => t.Length > n ? t.Substring(0, n - 1).TrimEnd() + "…" : t;

    // Hover text of a working card: the request, then the latest steps (newest last).
    static string Thinking(Work w)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Clip(w.Request.Trim(), 160)).Append("\n\n");
        if (w.Steps.Count == 0) sb.Append("Thinking…");
        else sb.Append(string.Join("\n", w.Steps.TakeLast(7).Select(x => "• " + x)));
        return sb.ToString();
    }

    static string ShortText(string t) => t.Length > 24 ? t.Substring(0, 23) + "…" : t.TrimEnd('.');
}
