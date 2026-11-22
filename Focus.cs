namespace VowenLite;

// Roadmap 2.4 + 4.1: Start sets up the work, and time tracking.
//   Start(task): opens task.Link (URL / file / folder; a repo in VS Code), starts a focus timer for the block, minimizes the
//                Settings.Distractions apps. While focused, one of those coming to the front shows a quiet Nudge ("12 min left on lab report").
//   Sample (every 15 s, daemon): front window's process + user idle time -> TimeLog.Add (seconds); the active task's Item.Spent grows.
static class Focus
{
    public static string TryLocal(string l, Settings s) => null;     // "start the next thing", "start <task>", "I'm done with it", "stop focus"
    public static string Start(Item task, Settings s) => null;       // returns the confirmation text ("✓ Started ...")
    public static string ActiveTaskId => null;                       // task being worked on now (null = none)
    public static void Sample(Settings s, DateTime now) { }
    public static void SelfChecks(Action<bool, string> check) { }
    public static int Test(string[] args) { Console.WriteLine("focustest ok"); return 0; }
}
