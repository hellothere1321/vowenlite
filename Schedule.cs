namespace VowenLite;

// Roadmap 2.1-2.3: the planner schedules itself.
//   Estimates: every open task gets Item.Est (guessed from its title/kind, times Habits.EstimateFactor); "that's 2 hours" corrects it.
//   Work blocks (Settings.AutoBlocks): events with Item.BlockFor = task Id, placed in free time before the due date (preferring
//                Habits.WorkHours), at most Settings.BlockMinutes each, never in quiet hours; moved when a real event lands on them or one is skipped.
//   The Now card: one row (id "now") with the single next thing, ranked by due time x Grades.Weight x work left x free time today.
//                Click = Focus.Start(task); x = "later" (snooze it for an hour).
static class Schedule
{
    public static string TryLocal(string l, Settings s) => null;     // "what should I do now", "that's 2 hours", "plan my week", "clear my blocks"
    public static void Register() { }                                // Cards.AddSource("now", ...) when Settings.NowCard
    public static void Tick(Settings s, DateTime now) { }

    public static Item NowPick(DateTime now) => null;                // the task the Now card shows (null = nothing to do)
    public static int EstimateFor(Item task) => task.Est > 0 ? task.Est : 30;   // minutes of work still left
    public static List<Item> MovedToday() => new();                  // blocks moved today (the evening rollover reports them)

    public static void SelfChecks(Action<bool, string> check) { }
    public static int Test(string[] args) { Console.WriteLine("schedtest ok"); return 0; }
}
