namespace VowenLite;

// The daemon side of the assistant modules: registers their cards and runs their timers.
// Tick handlers run on a timer thread and must return quickly: anything slow (network, a model call) starts its own Task with its own
// in-flight guard. Never touch WinForms here (the daemon stays WinForms-free); windows are their own processes.
static class Proactive
{
    static System.Threading.Timer minute, sample;
    static int busy;
    static Settings cached;
    static DateTime cachedStamp;

    public static void Start()
    {
        Cards.AddSource("inbox", Inbox.Rows, Inbox.Activate, Inbox.DismissCard);
        Cards.AddSource("nudge:", Nudge.Rows, Nudge.Activate, Nudge.Dismiss);
        Safe("schedule register", Schedule.Register);   // the Now card
        minute = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(25), TimeSpan.FromMinutes(1));
        sample = new System.Threading.Timer(_ => Safe("focus sample", () => Focus.Sample(S(), DateTime.Now)), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    // Settings, re-read only when settings.json changes (Settings.Load rewrites the file, so it is not called every minute).
    public static Settings S()
    {
        var stamp = Settings.Stamp();
        if (cached == null || stamp != cachedStamp) { cached = Settings.Peek(); cachedStamp = stamp; }
        return cached;
    }

    static void Tick()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        try
        {
            var s = S();
            var now = DateTime.Now;
            Safe("feeds", () => Feeds.Tick(s, now));
            Safe("schedule", () => Schedule.Tick(s, now));
            Safe("habits", () => Habits.Tick(s, now));
            Safe("coach", () => Coach.Tick(s, now));
            Safe("overnight", () => Overnight.Tick(s, now));
            Safe("timelog", TimeLog.Flush);
        }
        finally { Interlocked.Exchange(ref busy, 0); }
    }

    public static void Safe(string what, Action a)
    {
        try { a(); }
        catch (Exception ex) { Luna.Log($"proactive {what}: {ex.GetType().Name}: {ex.Message}"); }
    }
}
