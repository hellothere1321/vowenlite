namespace VowenLite;

// Roadmap 4.2-4.3: learns from TimeLog and finished tasks (habits.json).
//   Estimate vs actual per kind of task (essay, quiz, reading, lab, code...): EstimateFactor ("essays take you 1.7x your guess").
//   Real working hours: which hours of the week you actually work, so Schedule puts blocks there.
static class Habits
{
    public static string TryLocal(string l, Settings s) => null;     // "how long do essays take me", "when do I usually work"
    public static void Tick(Settings s, DateTime now) { }           // recompute once a day
    public static double EstimateFactor(string title) => 1.0;       // actual / estimated for tasks like this one (1 = no data)
    public static int[] WorkHours(DayOfWeek day) => new[] { 16, 17, 18, 19, 20, 21, 22 };   // preferred hours for work blocks, best first
    public static void SelfChecks(Action<bool, string> check) { }
    public static int Test(string[] args) { Console.WriteLine("habittest ok"); return 0; }
}
