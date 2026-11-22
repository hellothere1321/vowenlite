namespace VowenLite;

// Roadmap 2.5, 2.6, 3.4, 4.4: the assistant talks first (never in quiet hours: Nudge.Quiet).
//   Evening rollover (Settings.Rollover): one Nudge: done today, what's slipping, what Schedule moved; click = accept the moves.
//   Escalation (Settings.Escalate): due tonight and not started -> card (evening), then spoken, then a phone alert (a Google event at that time).
//   Morning brief v2: due today, today's blocks, the Now pick, what Overnight produced. Used by "good morning" / "daily briefing".
//   Sunday review: hours by course (TimeLog), what's due next week, grade outlook (Grades).
static class Coach
{
    public static string TryLocal(string l, Settings s) => null;     // "how did I do today", "weekly review"
    public static void Tick(Settings s, DateTime now) { }
    public static string MorningBrief(DateTime now) => null;         // null = fall back to the old briefing
    public static void SelfChecks(Action<bool, string> check) { }
    public static int Test(string[] args) { Console.WriteLine("coachtest ok"); return 0; }
}
