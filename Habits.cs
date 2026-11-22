namespace VowenLite;

// Roadmap 4.2-4.3: learns from TimeLog and finished tasks (habits.json).
//   Estimate vs actual per kind of task (essay, quiz, reading, lab, code...): EstimateFactor ("essays take you 1.7x your guess").
//   Real working hours: which hours of the week you actually work, so Schedule puts blocks there.
static class Habits
{
    public static string TryLocal(string l, Settings s) => null;     // "how long do essays take me", "when do I usually work"
    public static void Tick(Settings s, DateTime now) { }           // recompute once a day
    public static double EstimateFactor(string title) => 1.0;       // actual / estimated for tasks like this one (1 = no data)

    // The kind of a task from its title: quiz | exam | essay | reading | lab | homework | project | code | discussion | other.
    // Shared by Schedule (estimates) and Habits (estimate vs actual per kind): keep the two in step by changing only this.
    public static string Kind(string title)
    {
        string t = (title ?? "").ToLowerInvariant();
        bool Has(params string[] w) => w.Any(x => System.Text.RegularExpressions.Regex.IsMatch(t, @"\b" + x + @"\b"));
        if (Has("quiz", "quizzes")) return "quiz";
        if (Has("exam", "midterm", "final", "test")) return "exam";
        if (Has("essay", "paper", "report", "write-?up", "thesis", "draft")) return "essay";
        if (Has("read", "reading", "readings", "chapter", "ch\\.?", "textbook")) return "reading";
        if (Has("lab", "labs")) return "lab";
        if (Has("project", "presentation", "slides")) return "project";
        if (Has("code", "coding", "bug", "fix", "implement", "build", "repo", "commit", "pr")) return "code";
        if (Has("discussion", "post", "forum", "reply", "respond")) return "discussion";
        if (Has("homework", "hw", "assignment", "worksheet", "problem set", "pset")) return "homework";
        return "other";
    }

    // Starting estimate in minutes for a kind (before Habits has data on you).
    public static int DefaultMinutes(string kind) => kind switch
    {
        "quiz" => 45, "exam" => 180, "essay" => 180, "reading" => 45, "lab" => 120, "homework" => 60,
        "project" => 240, "code" => 90, "discussion" => 30, _ => 30,
    };
    public static int[] WorkHours(DayOfWeek day) => new[] { 16, 17, 18, 19, 20, 21, 22 };   // preferred hours for work blocks, best first
    public static void SelfChecks(Action<bool, string> check) { }
    public static int Test(string[] args) { Console.WriteLine("habittest ok"); return 0; }
}
