using System.Text.RegularExpressions;

namespace VowenLite;

// The assistant built around the planner (docs/roadmap.md) is split into modules, one file each:
//   Feeds (calendar links -> tasks), Capture ("plan this", promise catcher, lecture -> tasks), Grades (weights + "can I keep a B"),
//   Schedule (estimates, work blocks, the Now card), Focus (Start, distraction watch, time tracking), Habits (estimate vs actual, real hours),
//   Coach (rollover, escalation, morning brief, Sunday review), Overnight (queued jobs, study packs, after-quiz review).
// Shared pieces: Inbox (suggested items waiting for a yes), Nudge (one-tap cards), TimeLog (time tracking data), Ai (one-shot model calls),
// Proactive (the daemon's timers). This file is the glue: the local-command chain, the test modes and the self-test checks.
static class Extras
{
    // Called by Router.Local after the existing free commands (they win). l arrives cleaned by Router.Clean.
    public static string TryLocal(string request, Settings s)
    {
        string l = Regex.Replace(request.Trim().TrimEnd('.', '!', '?', ',').Trim(), @"^(?:please|hey|can you|could you|will you)\s+", "", RegexOptions.IgnoreCase).Trim();
        l = Regex.Replace(l, @"\s+please$", "", RegexOptions.IgnoreCase);
        if (l.Length == 0) return null;
        return WebBridge.TryLocal(l, s) ?? Inbox.TryLocal(l, s) ?? Feeds.TryLocal(l, s) ?? Capture.TryLocal(l, s) ?? Grades.TryLocal(l, s) ?? Schedule.TryLocal(l, s)
            ?? Focus.TryLocal(l, s) ?? Habits.TryLocal(l, s) ?? Coach.TryLocal(l, s) ?? Overnight.TryLocal(l, s);
    }

    // `VowenLite.exe --<name>test [args]`: prints "<name>test ok" and exits 0 on success.
    public static readonly Dictionary<string, Func<string[], int>> Tests = new()
    {
        ["--inboxtest"] = Inbox.Test,
        ["--feedtest"] = Feeds.Test,
        ["--capturetest"] = Capture.Test,
        ["--gradetest"] = Grades.Test,
        ["--schedtest"] = Schedule.Test,
        ["--focustest"] = Focus.Test,
        ["--habittest"] = Habits.Test,
        ["--coachtest"] = Coach.Test,
        ["--nighttest"] = Overnight.Test,
    };

    public static int RunTest(string name, Func<string[], int> test, string[] args)
    {
        string data = Environment.GetEnvironmentVariable("VOWENLITE_DATA");
        if (string.IsNullOrEmpty(data))
        {
            Console.WriteLine($"refusing to run {name}: set VOWENLITE_DATA to a scratch folder first (with {{\"SpeakReplies\":\"off\"}} in its settings.json).");
            return 2;
        }
        Jobs.Init();
        Luna.DryRun = args.Contains("--dry");
        try { return test(args); }
        catch (Exception ex) { Console.WriteLine(name + " crashed: " + ex); return 1; }
    }

    // Pure checks (no files, no network, no clock dependence) run by --selftest on every build.
    public static void SelfChecks(Action<bool, string> check)
    {
        Inbox.SelfChecks(check); Nudge.SelfChecks(check); TimeLog.SelfChecks(check); Ai.SelfChecks(check);
        Feeds.SelfChecks(check); Capture.SelfChecks(check); Grades.SelfChecks(check); Schedule.SelfChecks(check);
        Focus.SelfChecks(check); Habits.SelfChecks(check); Coach.SelfChecks(check); Overnight.SelfChecks(check);
    }
}
