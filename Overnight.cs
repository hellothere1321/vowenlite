namespace VowenLite;

// Roadmap 3.1-3.3: works while you're away (overnight.json).
//   Queue: "tonight: summarize my art readings" runs when the PC is idle and plugged in at night (Settings.Overnight), lower effort;
//          the result waits as a morning Nudge (click = open it).
//   Study packs (Settings.StudyPacks): for each quiz/test in the planner within 3 days: summaries, ~15 flashcards and a scored practice quiz.
//   After-submit review: when a quiz/test task is ticked done, a Nudge offers to go over the topics.
//   Never answers a live quiz or test: practice and review only.
static class Overnight
{
    public static string TryLocal(string l, Settings s) => null;     // "tonight, <job>", "what's queued", "cancel tonight's jobs", "quiz me on <topic>"
    public static void Queue(string request) { }
    public static List<string> ReadyToday() => new();               // one line per result finished since yesterday evening (Coach's morning brief)
    public static void Tick(Settings s, DateTime now) { }
    public static void SelfChecks(Action<bool, string> check) { }
    public static int Test(string[] args) { Console.WriteLine("nighttest ok"); return 0; }
}
