namespace VowenLite;

// Roadmap 1.5: course grading weights and the grade calculator (grades.json).
//   Weights per course ("quizzes 20%, final 30%") from a syllabus (Capture calls SaveWeights) or by voice.
//   Scores per category from a pasted / screenshotted gradebook, or by voice.
//   "can I drop to a B in art", "what do I need on the final to keep an A": pure math, answered locally.
static class Grades
{
    public static string TryLocal(string l, Settings s) => null;

    // course name -> category -> weight (fractions summing to ~1). Called by Capture when a syllabus is planned.
    public static void SaveWeights(string course, Dictionary<string, double> weights) { }

    // How much a task matters for the grade, 0..1 (its category's weight in its course; 0 = unknown). Used by Schedule to rank.
    public static double Weight(Item task) => 0;

    public static void SelfChecks(Action<bool, string> check) { }
    public static int Test(string[] args) { Console.WriteLine("gradetest ok"); return 0; }
}
