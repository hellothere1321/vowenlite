namespace VowenLite;

// Roadmap 1.2-1.4: the planner fills itself. Everything found goes to Inbox.Add (never straight into the planner), then a Nudge/the inbox card.
//   "plan this": picture of the window in front (Ai.Screenshot) + clipboard text -> Ai.Ask -> JSON items -> Inbox. A syllabus = the whole semester,
//                and it also hands grading weights to Grades.SaveWeights.
//   promise catcher (Settings.PromiseCatcher, opt-in): OnDictated gets every dictated sentence; a local pattern check spots commitments
//                ("I'll send it by Friday") and shows one Nudge "Add 'Send it' for Fri?" (click = add to planner, x = drop). No model call.
//   lecture -> tasks: FromLecture(folder) after a meeting summary is written: assignments said in class become inbox proposals.
static class Capture
{
    public static string TryLocal(string l, Settings s) => null;
    public static void OnDictated(string text, Settings s) { }       // called from Dictation after text is typed; must return at once
    public static Task FromLecture(string folder, Settings s) => Task.CompletedTask;
    public static void SelfChecks(Action<bool, string> check) { }
    public static int Test(string[] args) { Console.WriteLine("capturetest ok"); return 0; }
}
