using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VowenLite;

// Roadmap 1.5: course grading weights and the grade calculator (grades.json).
//   Weights per course ("quizzes 20%, final 30%") from a syllabus (Capture calls SaveWeights) or by voice.
//   Scores per category from a pasted / screenshotted gradebook, or by voice.
//   "can I drop to a B in art", "what do I need on the final to keep an A": pure math, answered locally.
static class Grades
{
    sealed class GScore { public string Label { get; set; } = ""; public double Earned { get; set; } public double Possible { get; set; } = 100; }
    sealed class GCat { public double Weight { get; set; } public int Expected { get; set; } public List<GScore> Scores { get; set; } = new(); }
    sealed class GCourse
    {
        public string Name { get; set; } = "";
        public List<string> Aliases { get; set; } = new();
        public Dictionary<string, GCat> Categories { get; set; } = new();
        public Dictionary<string, double> Cutoffs { get; set; }   // letter -> percent; null = A 90, B 80, C 70, D 60
    }
    sealed class GFile { public List<GCourse> Courses { get; set; } = new(); }

    static readonly object gate = new();
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    static string FilePath => Path.Combine(Paths.DataDir, "grades.json");
    const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static bool Is(string l, string pattern, out Match m) { m = Regex.Match(l, pattern, IC); return m.Success; }

    static GFile Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<GFile>(File.ReadAllText(FilePath), Opts) ?? new GFile(); }
        catch (Exception ex) { Luna.Log("grades: load failed: " + ex.Message); }
        return new GFile();
    }
    static void Save(GFile f)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(f, Opts));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) { Luna.Log("grades: save failed: " + ex.Message); }
    }

    // ---------------- names

    static string Norm(string s) => Regex.Replace((s ?? "").ToLowerInvariant().Replace("&", " and "), @"[^a-z0-9]+", " ").Trim();

    // "Quizzes" -> "quiz", "exams" -> "exam": so a spoken "quiz" finds the category the syllabus called "Quizzes".
    internal static string Stem(string s) => string.Join(' ', Norm(s).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(StemWord));
    static string StemWord(string w)
    {
        if (w.Length > 4 && w.EndsWith("zes")) return w.Substring(0, w.Length - 3);
        if (w.Length > 4 && w.EndsWith("ies")) return w.Substring(0, w.Length - 3) + "y";
        if (w.Length > 4 && Regex.IsMatch(w, @"(?:ss|x|ch|sh)es$")) return w.Substring(0, w.Length - 2);
        if (w.Length > 3 && w.EndsWith("s") && !w.EndsWith("ss")) return w.Substring(0, w.Length - 1);
        return w;
    }

    static int Lev(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    // 5 exact, 3 the text is the start of the name ("art" -> "Art 101"), 2 one is a whole-word part of the other, 1 close spelling.
    static int Score(GCourse c, string n)
    {
        int best = 0;
        foreach (var name in new[] { c.Name }.Concat(c.Aliases ?? new()))
        {
            string k = Norm(name);
            if (k.Length == 0) continue;
            int sc = 0;
            if (k == n) sc = 5;
            else if (k.StartsWith(n + " ")) sc = 3;
            else if ((" " + k + " ").Contains(" " + n + " ") || (" " + n + " ").Contains(" " + k + " ")) sc = 2;
            else if (!k.Contains(' ') && !n.Contains(' ') && n.Length >= 4 && k.Length >= 4 && Lev(k, n) <= (k.Length >= 8 ? 2 : 1)) sc = 1;
            best = Math.Max(best, sc);
        }
        return best;
    }

    static GCourse Find(List<GCourse> all, string text)
    {
        string n = Norm(text);
        if (n.Length == 0) return null;
        GCourse best = null; int bs = 0;
        foreach (var c in all) { int sc = Score(c, n); if (sc > bs) { bs = sc; best = c; } }
        return best;
    }

    // "art quiz" -> course Art 101 + "quiz": the longest start of the text that names one of the user's courses.
    static bool Split(List<GCourse> all, string text, out GCourse course, out string rest)
    {
        course = null; rest = "";
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (int thr in new[] { 5, 3, 1 })   // an exact course name beats the start of a name beats a close spelling
            for (int k = Math.Min(words.Length, 4); k >= 1; k--)
            {
                string n = Norm(string.Join(' ', words.Take(k)));
                var c = all.FirstOrDefault(x => Score(x, n) == thr);
                if (c == null) continue;
                course = c; rest = string.Join(' ', words.Skip(k));
                return true;
            }
        return false;
    }

    static KeyValuePair<string, GCat>? FindCat(GCourse c, string phrase)
    {
        string ps = Stem(phrase);
        if (ps.Length == 0) return null;
        var pw = ps.Split(' ');
        foreach (var kv in c.Categories)
        {
            string cs = Stem(kv.Key);
            if (cs == ps) return kv;
        }
        foreach (var kv in c.Categories)
        {
            var cw = Stem(kv.Key).Split(' ');
            if (cw.All(pw.Contains) || pw.All(cw.Contains)) return kv;
        }
        string kind = Habits.Kind(ps);   // "midterm" -> exam; Kind needs the stem since it matches "exam" but not "exams"
        if (kind != "other")
        {
            foreach (var kv in c.Categories) if (Stem(kv.Key) == kind) return kv;
            foreach (var kv in c.Categories) if (Habits.Kind(Stem(kv.Key)) == kind) return kv;
        }
        return null;
    }

    // Finds the category or adds it with no weight yet (the score still counts once a weight is set).
    static GCat CatFor(GCourse c, string phrase, out bool created)
    {
        created = false;
        var hit = FindCat(c, phrase);
        if (hit != null) return hit.Value.Value;
        string name = Cap(Norm(phrase));
        if (name.Length == 0) name = "Other";
        var cat = new GCat();
        c.Categories[name] = cat;
        created = true;
        return cat;
    }
    // spoken names arrive lower-case: "art 101" -> "Art 101" (a name that already has capitals is kept)
    static string Title(string s) => s != s.ToLowerInvariant() ? s : string.Join(' ', s.Split(' ').Select(Cap));
    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    static string CatName(GCourse c, GCat cat) => c.Categories.First(kv => ReferenceEquals(kv.Value, cat)).Key;

    // ---------------- weights

    // Weights that arrive as percents (20 50 30) become fractions; fractions stay as they are.
    internal static Dictionary<string, double> NormWeights(Dictionary<string, double> w)
    {
        double sum = w.Values.Where(v => v > 0).Sum();
        double div = sum > 1.5 ? 100 : 1;
        return w.ToDictionary(kv => kv.Key, kv => Math.Max(0, kv.Value) / div);
    }

    // course name -> category -> weight (fractions summing to ~1). Called by Capture when a syllabus is planned.
    public static void SaveWeights(string course, Dictionary<string, double> weights)
    {
        if (string.IsNullOrWhiteSpace(course) || weights == null || weights.Count == 0) return;
        lock (gate)
        {
            var f = Load();
            var c = Find(f.Courses, course);
            if (c == null) { c = new GCourse { Name = Title(course.Trim()) }; f.Courses.Add(c); }
            ApplyWeights(c, weights);
            Save(f);
        }
        Luna.Log($"grades: weights saved for {course} ({weights.Count} categories)");
    }

    static void ApplyWeights(GCourse c, Dictionary<string, double> weights)
    {
        var norm = NormWeights(weights);
        var keep = new HashSet<string>();
        foreach (var kv in norm)
        {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            var hit = FindExact(c, kv.Key);
            string key = hit ?? Cap(kv.Key.Trim());
            if (!c.Categories.TryGetValue(key, out var cat)) c.Categories[key] = cat = new GCat();
            cat.Weight = kv.Value;
            keep.Add(key);
        }
        foreach (var k in c.Categories.Keys.ToList())   // categories the new list leaves out go, unless they hold scores
            if (!keep.Contains(k) && c.Categories[k].Scores.Count == 0) c.Categories.Remove(k);
    }
    static string FindExact(GCourse c, string name)
    {
        string s = Stem(name);
        foreach (var k in c.Categories.Keys) if (Stem(k) == s) return k;
        return null;
    }

    public static void SetExpected(string course, string category, int count)
    {
        lock (gate)
        {
            var f = Load();
            var c = Find(f.Courses, course);
            var hit = c == null ? null : FindCat(c, category);
            if (hit == null) return;
            hit.Value.Value.Expected = Math.Max(0, count);
            Save(f);
        }
    }

    // "quizzes 20 exams 50 final 30%" -> pairs (percent numbers as spoken).
    internal static Dictionary<string, double> ParsePairs(string text)
    {
        var d = new Dictionary<string, double>();
        foreach (Match m in Regex.Matches(text, @"([a-z][a-z' &/-]*?)\s*[:=]?\s*(\d+(?:\.\d+)?)\s*(?:%|percent)?(?:\s*(?:,|;|and\b))?", IC))
        {
            string name = Regex.Replace(m.Groups[1].Value.Trim(), @"^(?:and|&)\s+", "", IC).Trim();
            if (name.Length == 0) continue;
            d[name] = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        return d;
    }

    // ---------------- using the math on a course

    static List<(double w, double? avg)> Cats(GCourse c) =>
        c.Categories.Values.Where(k => k.Weight > 0).Select(k => (k.Weight, k.Scores.Where(s => s.Possible > 0).Select(s => (double?)(s.Earned / s.Possible)).DefaultIfEmpty(null).Average())).ToList();

    static Dictionary<string, double> Cuts(GCourse c) => c.Cutoffs is { Count: > 0 } ? c.Cutoffs : GradeMath.DefaultCutoffs;
    static string An(string letter) => letter is "A" or "F" ? "an" : "a";
    static string P(double frac, bool up = false) => GradeMath.Pct(frac, up);

    static string NowText(GCourse c, double? cur) => cur == null ? "no scores yet" : $"Now: {P(cur.Value)} ({GradeMath.Letter(cur.Value * 100, Cuts(c))})";

    static string NoWeights(GCourse c) => $"I don't have weights for {c.Name} yet. Say \"set {c.Name.ToLowerInvariant()} weights quizzes 20 exams 50 final 30\".";

    // "can I drop to a B" / "how low can I go": lowest average on the rest that keeps the target (no target = keep the current letter).
    static string KeepAnswer(GCourse c, string letter, string onCat, bool phrasedNeed)
    {
        var cats = Cats(c);
        var (earned, remaining) = GradeMath.Split(cats);
        if (cats.Count == 0) return NoWeights(c);
        double? cur = GradeMath.Current(cats);
        letter = string.IsNullOrEmpty(letter) ? (cur == null ? "A" : GradeMath.Letter(cur.Value * 100, Cuts(c))) : letter.ToUpperInvariant();
        if (!Cuts(c).TryGetValue(letter, out double cut)) return $"{c.Name} has no cutoff for {letter}.";
        double target = cut / 100;
        string now = NowText(c, cur);
        string what = "what's left";
        var hit = onCat == null ? null : FindCat(c, onCat);
        if (hit != null && hit.Value.Value.Scores.Count == 0 && hit.Value.Value.Weight > 0 && remaining > hit.Value.Value.Weight / cats.Sum(x => x.w) + 1e-9)
        {
            // the named item is one of several things still to come: assume the rest goes like the current average
            double wc = hit.Value.Value.Weight / cats.Sum(x => x.w);
            double assumed = cur ?? target;
            earned += (remaining - wc) * assumed;
            remaining = wc;
            what = "the " + hit.Value.Key.ToLowerInvariant() + " (if the rest of what's left goes like your average)";
        }
        else if (hit != null && hit.Value.Value.Scores.Count == 0 && hit.Value.Value.Weight > 0) what = "the " + hit.Value.Key.ToLowerInvariant();
        var need = GradeMath.Needed(earned, remaining, target);
        string L = An(letter) + " " + letter;
        if (cur == null && need.Status == "ok" && remaining >= 1 - 1e-9) return $"No scores in {c.Name} yet. You'd need {P(need.Value, true)} across everything to get {L}.";
        switch (need.Status)
        {
            case "locked": return $"You've already locked in {L}, even with 0% on {what}. {now}.";
            case "impossible":
                double best = earned + remaining;
                return $"{Cap(L)} isn't reachable anymore: even 100% on {what} gives {P(best)}. {now}.";
            default:
                return phrasedNeed ? $"You need {P(need.Value, true)} on {what} to keep {L}. {now}."
                                   : $"You can score as low as {P(need.Value, true)} on {what} and keep {L}. {now}.";
        }
    }

    static string GradeAnswer(GCourse c)
    {
        var cats = Cats(c);
        if (cats.Count == 0) return NoWeights(c);
        double? cur = GradeMath.Current(cats);
        if (cur == null) return $"No scores in {c.Name} yet.";
        var (earned, remaining) = GradeMath.Split(cats);
        string s = $"{P(cur.Value)} ({GradeMath.Letter(cur.Value * 100, Cuts(c))}) in {c.Name}.";
        if (remaining > 1e-9) s += $" {Math.Round(remaining * 100):0}% of the grade is still to come: best case {P(earned + remaining)}, worst case {P(earned)}.";
        return s;
    }

    // ---------------- voice / typed

    public static string TryLocal(string l, Settings s)
    {
        Match m;
        if (string.IsNullOrWhiteSpace(l)) return null;
        // cheap exit: every phrase below mentions one of these
        if (!Regex.IsMatch(l, @"\b(?:weights?|cutoffs?|grades?|got|scored|made|earned|drop|low|need)\b", IC)) return null;

        if (Is(l, @"^update my grades? from (?:my |the )?(screen|clipboard|window)$", out m) || Is(l, @"^(?:import|refresh|sync|read|pull) my grades? from (?:my |the )?(screen|clipboard|window)$", out m))
        {
            bool screen = !m.Groups[1].Value.Equals("clipboard", StringComparison.OrdinalIgnoreCase);
            if (Luna.DryRun) return "DRY RUN: would read your grades from the " + (screen ? "screen" : "clipboard") + " and merge them into grades.json";
            string clip = null;
            if (!screen) { clip = Luna.ClipboardText(); if (clip.Trim().Length == 0) return "✓ Clipboard is empty"; }
            int w = Cards.BeginWork(l);
            Task.Run(async () =>
            {
                try { Cards.EndWork(w, await Import(clip)); }
                catch (Exception ex) { Luna.Log("grades: import failed: " + ex.Message); Cards.EndWork(w, "Couldn't read the grades: " + ex.Message, true); }
            });
            return "✓ Working on it…";
        }

        if (Is(l, @"^set (.+?) (?:grading )?weights?:?\s+(.+)$", out m))
        {
            var pairs = ParsePairs(m.Groups[2].Value);
            if (pairs.Count == 0) return null;
            string name = m.Groups[1].Value.Trim();
            if (Luna.DryRun) return $"DRY RUN: would set the weights for {name}: " + string.Join(", ", pairs.Select(p => $"{p.Key} {p.Value:0.#}"));
            SaveWeights(name, pairs);
            var norm = NormWeights(pairs);
            double sum = norm.Values.Sum();
            string line = string.Join(", ", norm.Select(p => $"{Cap(p.Key)} {Math.Round(p.Value * 100, 1):0.#}%"));
            return $"✓ {Find(Load().Courses, name)?.Name ?? name}: {line}" + (Math.Abs(sum - 1) > 0.015 ? $" (adds up to {Math.Round(sum * 100):0}%)" : "");
        }

        if (Is(l, @"^set (.+?) (?:grade )?cutoffs?:?\s+(.+)$", out m))
        {
            var cuts = new Dictionary<string, double>();
            foreach (Match p in Regex.Matches(m.Groups[2].Value, @"\b([a-d])\s*[:=]?\s*(\d+(?:\.\d+)?)", IC)) cuts[p.Groups[1].Value.ToUpperInvariant()] = double.Parse(p.Groups[2].Value, CultureInfo.InvariantCulture);
            if (cuts.Count == 0) return null;
            lock (gate)
            {
                var f = Load();
                var c = Find(f.Courses, m.Groups[1].Value);
                if (c == null) return null;
                if (Luna.DryRun) return $"DRY RUN: would set the cutoffs for {c.Name}";
                var merged = new Dictionary<string, double>(Cuts(c));
                foreach (var kv in cuts) merged[kv.Key] = kv.Value;
                c.Cutoffs = merged;
                Save(f);
                return $"✓ {c.Name} cutoffs: " + string.Join(", ", merged.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value:0.#}"));
            }
        }

        // "add grade art quiz 8 out of 10", "add grade quiz 8 out of 10 in art"
        if (Is(l, @"^add (?:a |my )?(?:grade|score)s? (?:of |for |in |to )?(.+?)\s+(\d+(?:\.\d+)?)\s*(?:out of|/|over)\s*(\d+(?:\.\d+)?)(?:\s+(?:in|for|to|on)\s+(?:the |my )?(.+))?$", out m))
        {
            var f = Load();
            if (f.Courses.Count == 0) return null;
            GCourse c; string cat;
            if (m.Groups[4].Success) { c = Find(f.Courses, m.Groups[4].Value); cat = m.Groups[1].Value; }
            else if (!Split(f.Courses, m.Groups[1].Value, out c, out cat) && f.Courses.Count == 1) { c = f.Courses[0]; cat = m.Groups[1].Value; }
            if (c == null || Norm(cat).Length == 0) return null;
            return AddScore(c.Name, cat, Num(m.Groups[2].Value), Num(m.Groups[3].Value));
        }
        // "I got 85 on the art midterm", "got 17 out of 20 on the art quiz"
        if (Is(l, @"^(?:i )?(?:got|scored|made|earned) (?:an? )?(\d+(?:\.\d+)?)\s*(?:%|percent)?(?:\s*(?:out of|/|over)\s*(\d+(?:\.\d+)?))?\s+on (?:the |my )?(.+)$", out m))
        {
            var f = Load();
            if (!Split(f.Courses, m.Groups[3].Value, out var c, out string cat) || Norm(cat).Length == 0) return null;
            return AddScore(c.Name, cat, Num(m.Groups[1].Value), m.Groups[2].Success ? Num(m.Groups[2].Value) : 100);
        }

        if (Is(l, @"^what(?:['’]?s| is) my (?:current |overall )?grade (?:in|for|on) (?:the |my )?(.+)$", out m))
        {
            var c = Find(Load().Courses, m.Groups[1].Value);
            return c == null ? null : GradeAnswer(c);
        }

        // "can I drop to a B in art"
        if (Is(l, @"^(?:can|could|may) i (?:drop|fall|slip|go|dip)(?: down)? to (?:an? )?([a-d])(?: grade)? (?:in|for|on) (?:the |my )?(.+)$", out m))
        {
            var c = Find(Load().Courses, m.Groups[2].Value);
            return c == null ? null : KeepAnswer(c, m.Groups[1].Value, null, false);
        }
        // "what do I need on the final to keep an A in art"
        if (Is(l, @"^what do i need (?:on|for|in) (?:the |my )?(.+?) to (?:keep|get|make|earn|have|hold|stay at|reach|maintain|pass with)(?: an?)? ([a-d])(?: grade)?(?: (?:in|for|on) (?:the |my )?(.+))?$", out m))
        {
            var all = Load().Courses;
            var c = m.Groups[3].Success ? Find(all, m.Groups[3].Value) : null;
            string item = m.Groups[1].Value;
            if (c == null && !m.Groups[3].Success && Split(all, item, out c, out string rest)) item = rest;   // "art final"
            if (c == null) return null;
            return KeepAnswer(c, m.Groups[2].Value, item, true);
        }
        // "how low can I go on the final in art"
        if (Is(l, @"^how low can i go(?: on| for| in)? (?:the |my )?(.+?)(?: (?:in|for|on) (?:the |my )?(.+))?$", out m))
        {
            var all = Load().Courses;
            var c = m.Groups[2].Success ? Find(all, m.Groups[2].Value) : null;
            string item = m.Groups[1].Value;
            if (c == null && Split(all, item, out c, out string rest)) item = rest;
            if (c == null) return null;
            return KeepAnswer(c, null, item.Length == 0 ? null : item, false);
        }
        return null;
    }

    static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    static string AddScore(string courseName, string catPhrase, double earned, double possible)
    {
        if (possible <= 0) return "A score needs a total above zero.";
        if (Luna.DryRun) return $"DRY RUN: would add {earned:0.#} out of {possible:0.#} to {catPhrase} in {courseName}";
        lock (gate)
        {
            var f = Load();
            var c = Find(f.Courses, courseName);
            if (c == null) return null;
            var cat = CatFor(c, catPhrase, out bool created);
            string name = CatName(c, cat);
            cat.Scores.Add(new GScore { Label = $"{Regex.Replace(name, "(?:zes|s)$", "", IC)} {cat.Scores.Count + 1}", Earned = earned, Possible = possible });
            Save(f);
            var cats = Cats(c);
            double? cur = GradeMath.Current(cats);
            string now = cur == null ? "" : $", now {P(cur.Value)} ({GradeMath.Letter(cur.Value * 100, Cuts(c))})";
            string note = cat.Weight <= 0 ? $" ({name} has no weight yet, so it doesn't count: say \"set {c.Name.ToLowerInvariant()} weights ...\")" : "";
            return $"✓ Added {earned:0.#}/{possible:0.#} to {name} in {c.Name}{now}{note}";
        }
    }

    // ---------------- screen / clipboard import

    // clip null = look at the screen. Returns the confirmation line(s). Public to the tests only through Test().
    internal static async Task<string> Import(string clip)
    {
        string img = null;
        if (clip == null)
        {
            img = Ai.Screenshot();
            if (img == null) throw new Exception("couldn't capture the screen");
        }
        string prompt = "Extract the course grades from " + (clip == null ? "this screenshot of a gradebook" : "the gradebook text below") + ".\n" +
            "Reply with ONLY JSON: {\"course\": \"course name\", \"categories\": [{\"name\": \"Quizzes\", \"weight\": 20, \"scores\": [{\"label\": \"Quiz 1\", \"earned\": 8, \"possible\": 10}]}]}\n" +
            "Rules: weight is the category's percent of the course grade, left out if not shown; only include scores that are graded (skip blank, missing or pending ones); " +
            "earned and possible are numbers; group items into their category (Quizzes, Exams, Homework, ...). If several courses are shown, reply with a JSON array of those objects.";
        if (clip != null) prompt += "\n\n" + (clip.Length > 12000 ? clip.Substring(0, 12000) : clip);
        string reply = await Ai.Ask(prompt, img, "low");
        var node = Ai.Json(reply);
        var objs = node is JsonArray arr ? arr.OfType<JsonObject>().ToList() : node is JsonObject o ? new List<JsonObject> { o } : new List<JsonObject>();
        if (objs.Count == 0) throw new Exception("no grades found in the reply");
        var lines = objs.Select(Merge).Where(x => x != null).ToList();
        if (lines.Count == 0) throw new Exception("no course name found");
        return string.Join("\n", lines);
    }

    static double? Val(JsonNode n)
    {
        if (n == null) return null;
        try { return n.GetValue<double>(); } catch { }
        try { var m = Regex.Match(n.ToString(), @"-?\d+(?:\.\d+)?"); if (m.Success) return Num(m.Value); } catch { }
        return null;
    }

    static string Merge(JsonObject j)
    {
        string name = j["course"]?.ToString()?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        lock (gate)
        {
            var f = Load();
            var c = Find(f.Courses, name);
            if (c == null) { c = new GCourse { Name = Title(name) }; f.Courses.Add(c); }
            var incoming = new Dictionary<string, double>();
            int added = 0;
            foreach (var cn in (j["categories"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                string cname = cn["name"]?.ToString()?.Trim();
                if (string.IsNullOrEmpty(cname)) continue;
                var cat = CatFor(c, cname, out _);
                if (Val(cn["weight"]) is double wt && wt > 0) incoming[CatName(c, cat)] = wt;
                if (Val(cn["count"]) is double ct && ct > 0) cat.Expected = (int)ct;
                var scores = new List<GScore>();
                foreach (var sc in (cn["scores"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    double? e = Val(sc["earned"]), p = Val(sc["possible"]);
                    if (e == null || p == null || p <= 0) continue;
                    scores.Add(new GScore { Label = sc["label"]?.ToString() ?? "", Earned = e.Value, Possible = p.Value });
                }
                if (scores.Count > 0) { cat.Scores = scores; added += scores.Count; }   // the gradebook is the source of truth for what it lists
            }
            if (incoming.Count > 0)
            {
                var norm = NormWeights(incoming);
                foreach (var kv in norm) c.Categories[kv.Key].Weight = kv.Value;
            }
            Save(f);
            var cats = Cats(c);
            double? cur = GradeMath.Current(cats);
            if (cur == null) return $"✓ Updated {c.Name}: {added} scores saved, but no weights yet to work out a grade";
            return $"✓ Updated {c.Name}: now {P(cur.Value)} ({GradeMath.Letter(cur.Value * 100, Cuts(c))})";
        }
    }

    // ---------------- hooks for the other modules

    // How much a task matters for the grade, 0..1 (its category's weight in its course; 0 = unknown). Used by Schedule to rank.
    public static double Weight(Item task)
    {
        if (task == null) return 0;
        try
        {
            List<GCourse> all;
            lock (gate) all = Load().Courses;
            if (all.Count == 0) return 0;
            string title = " " + Norm(task.Title) + " ";
            GCourse c = null;
            string cat = Norm(task.Cat);
            if (cat.Length > 0) c = all.FirstOrDefault(x => Score(x, cat) >= 5);
            c ??= all.FirstOrDefault(x => new[] { x.Name }.Concat(x.Aliases ?? new()).Any(n => Norm(n).Length > 0 && title.Contains(" " + Norm(n) + " ")));
            if (c == null) return 0;
            // the title minus the course name, so "Art 101 Quiz 13.2" looks for "quiz"
            string phrase = title;
            foreach (var n in new[] { c.Name }.Concat(c.Aliases ?? new())) phrase = phrase.Replace(" " + Norm(n) + " ", " ");
            var hit = FindCat(c, phrase);
            if (hit == null || hit.Value.Value.Weight <= 0) return 0;
            var k = hit.Value.Value;
            double w = k.Expected > 0 ? k.Weight / k.Expected : k.Weight;
            return Math.Clamp(w, 0, 1);
        }
        catch (Exception ex) { Luna.Log("grades: weight failed: " + ex.Message); return 0; }
    }

    // One markdown line per course with grades ("Art: 86.4% (B), needs 72% on what's left to keep a B"); null = no courses yet. Coach's Sunday review.
    public static string Outlook()
    {
        List<GCourse> all;
        lock (gate) all = Load().Courses;
        var lines = new List<string>();
        foreach (var c in all)
        {
            var cats = Cats(c);
            double? cur = GradeMath.Current(cats);
            if (cur == null) continue;
            string letter = GradeMath.Letter(cur.Value * 100, Cuts(c));
            var (earned, remaining) = GradeMath.Split(cats);
            string tail;
            if (remaining <= 1e-9) tail = "all graded";
            else
            {
                var need = GradeMath.Needed(earned, remaining, Cuts(c)[letter] / 100);
                tail = need.Status switch
                {
                    "locked" => $"{An(letter)} {letter} is locked in",
                    "impossible" => $"can't hold {An(letter)} {letter} anymore",
                    _ => $"needs {P(need.Value, true)} on what's left to keep {An(letter)} {letter}",
                };
            }
            lines.Add($"{c.Name}: {P(cur.Value)} ({letter}), {tail}");
        }
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    // ---------------- checks

    public static void SelfChecks(Action<bool, string> check)
    {
        bool Near(double? a, double b) => a != null && Math.Abs(a.Value - b) < 1e-6;
        var cur = GradeMath.Current(new List<(double, double?)> { (0.4, 0.9), (0.6, 0.8) });
        check(Near(cur, 0.84), "grades: weighted current grade");
        check(Near(GradeMath.Current(new List<(double, double?)> { (0.2, 0.8), (0.8, null) }), 0.8), "grades: only graded categories count, re-normalised");
        check(GradeMath.Current(new List<(double, double?)> { (0.5, null), (0.5, null) }) == null, "grades: nothing graded yet");
        var parts = new List<(double, double?)> { (0.2, 0.85), (0.5, 0.85), (0.3, null) };
        var (e, r) = GradeMath.Split(parts);
        check(Math.Abs(e - 0.595) < 1e-9 && Math.Abs(r - 0.3) < 1e-9, "grades: earned and remaining weight");
        var need = GradeMath.Needed(e, r, 0.8);
        check(need.Status == "ok" && Math.Abs(need.Value - 0.68333333) < 1e-6, "grades: average needed on the rest for a B");
        check(GradeMath.Needed(e, r, 0.9).Status == "impossible", "grades: target out of reach");
        check(GradeMath.Needed(0.9, 0.1, 0.85).Status == "locked", "grades: target already locked in");
        var fresh = GradeMath.Needed(0, 1, 0.9);
        check(fresh.Status == "ok" && Math.Abs(fresh.Value - 0.9) < 1e-9, "grades: nothing graded, need the target everywhere");
        check(GradeMath.Needed(0.7, 0, 0.8).Status == "impossible" && GradeMath.Needed(0.85, 0, 0.8).Status == "locked", "grades: everything graded decides it");
        var (be, bw) = GradeMath.Split(new List<(double, double?)> { (0.25, 0.8), (0.25, null) });   // weights not adding to 1 are scaled
        check(Math.Abs(be - 0.4) < 1e-9 && Math.Abs(bw - 0.5) < 1e-9, "grades: weights that don't sum to 1 are scaled");
        check(GradeMath.Letter(90, GradeMath.DefaultCutoffs) == "A" && GradeMath.Letter(89.99, GradeMath.DefaultCutoffs) == "B" && GradeMath.Letter(50, GradeMath.DefaultCutoffs) == "F", "grades: letters at the default cutoffs");
        check(GradeMath.Letter(92, new Dictionary<string, double> { ["A"] = 93, ["B"] = 85 }) == "B", "grades: custom cutoffs");
        var nw = NormWeights(new Dictionary<string, double> { ["q"] = 20, ["e"] = 50, ["f"] = 30 });
        check(Math.Abs(nw["q"] - 0.2) < 1e-9 && Math.Abs(nw.Values.Sum() - 1) < 1e-9, "grades: weights given as 100 become fractions");
        check(Math.Abs(NormWeights(new Dictionary<string, double> { ["q"] = 0.4, ["e"] = 0.6 })["q"] - 0.4) < 1e-9, "grades: weights given as fractions stay");
        check(Stem("Quizzes") == "quiz" && Stem("exams") == "exam" && Stem("Final Exam") == "final exam", "grades: category stems");
        var pairs = ParsePairs("quizzes 20 exams 50, and final 30%");
        check(pairs.Count == 3 && pairs["final"] == 30 && pairs["exams"] == 50, "grades: weight pairs from speech");
        check(GradeMath.Pct(0.6833333, true) == "68.4%" && GradeMath.Pct(0.864) == "86.4%" && GradeMath.Pct(0.9) == "90%", "grades: percent formatting");
        var (best, worst) = (e + r, e);
        check(Math.Abs(best - 0.895) < 1e-9 && Math.Abs(worst - 0.595) < 1e-9, "grades: best and worst case");
    }

    public static int Test(string[] args)
    {
        int fail = 0;
        void Check(bool ok, string what) { Console.WriteLine((ok ? "pass: " : "FAIL: ") + what); if (!ok) fail++; }
        try { File.Delete(FilePath); } catch { }
        Luna.DryRun = false;

        string r = TryLocal("set art 101 weights quizzes 20 exams 50 final 30", null);
        Check(r != null && r.Contains("Quizzes 20%") && r.Contains("Final 30%"), "set weights: " + r);
        Check(Load().Courses.Count == 1 && Math.Abs(Load().Courses[0].Categories["Exams"].Weight - 0.5) < 1e-9, "weights saved as fractions");
        r = TryLocal("add grade art 101 quiz 8 out of 10", null);
        Check(r != null && r.Contains("8/10") && r.Contains("Quizzes"), "add grade: " + r);
        r = TryLocal("add grade art quiz 9 out of 10", null);
        Check(r != null && r.Contains("9/10") && r.Contains("85%"), "add grade by short course name: " + r);
        r = TryLocal("I got 85 on the art midterm", null);
        Check(r != null && r.Contains("85/100") && r.Contains("Exams") && r.Contains("85%"), "I got ... on the midterm: " + r);
        r = TryLocal("what's my grade in art", null);
        Check(r != null && r.StartsWith("85%") && r.Contains("(B)") && r.Contains("best case 89.5%") && r.Contains("worst case 59.5%"), "grade question: " + r);
        r = TryLocal("can I drop to a B in art", null);
        Check(r != null && r.Contains("68.4%") && r.Contains("keep a B"), "drop to a B: " + r);
        r = TryLocal("what do I need on the final to keep an A in art", null);
        Check(r != null && r.Contains("isn't reachable"), "A out of reach: " + r);
        r = TryLocal("what do I need on the final to keep a B in art", null);
        Check(r != null && r.StartsWith("You need 68.4% on the final"), "need on the final: " + r);
        r = TryLocal("how low can I go on the final in art", null);
        Check(r != null && r.Contains("68.4%"), "how low: " + r);
        r = TryLocal("can I drop to a C in art", null);
        Check(r != null && r.Contains("locked in") || r != null && r.Contains("as low as"), "drop to a C: " + r);
        Check(TryLocal("can I drop to a B in chemistry", null) == null, "unknown course is left alone");
        Check(TryLocal("I got 85 on the test", null) == null, "no course, not ours");
        var o = Outlook();
        Check(o != null && o.Contains("Art 101: 85% (B)") && o.Contains("needs 68.4%"), "outlook: " + o);

        double w = Weight(new Item { Title = "Art 101 Quiz 13.2", Cat = "School" });
        Check(Math.Abs(w - 0.2) < 1e-9, "Weight() of a quiz task = " + w);
        SetExpected("Art 101", "Quizzes", 10);
        w = Weight(new Item { Title = "Art 101 Quiz 13.2", Cat = "School" });
        Check(Math.Abs(w - 0.02) < 1e-9, "Weight() split over 10 quizzes = " + w);
        Check(Weight(new Item { Title = "Buy milk" }) == 0, "Weight() unknown = 0");

        Ai.Fake = _ => "```json\n{\"course\":\"Art 101\",\"categories\":[{\"name\":\"Quizzes\",\"weight\":20,\"scores\":[{\"label\":\"Quiz 1\",\"earned\":10,\"possible\":10},{\"label\":\"Quiz 2\",\"earned\":10,\"possible\":10}]}]}\n```";
        string imp = Import("Quiz 1 10/10\nQuiz 2 10/10").GetAwaiter().GetResult();
        Check(imp.StartsWith("✓ Updated Art 101: now 89.3%"), "clipboard import: " + imp);
        Ai.Fake = null;

        if (fail == 0) Console.WriteLine("gradetest ok");
        return fail == 0 ? 0 : 1;
    }
}

// The grade math, pure (no files, clock or model) so SelfChecks can test it. Percentages are fractions 0..1 here.
static class GradeMath
{
    public static readonly Dictionary<string, double> DefaultCutoffs = new() { ["A"] = 90, ["B"] = 80, ["C"] = 70, ["D"] = 60 };

    // (weight, category average or null when nothing is graded in it)
    public static double? Current(IList<(double w, double? avg)> cats)
    {
        double g = cats.Where(c => c.avg != null && c.w > 0).Sum(c => c.w);
        return g <= 0 ? null : cats.Where(c => c.avg != null && c.w > 0).Sum(c => c.w * c.avg.Value) / g;
    }

    // Points banked so far and weight still to come, both as a share of the whole course (weights are scaled to sum to 1).
    public static (double earned, double remaining) Split(IList<(double w, double? avg)> cats)
    {
        double tw = cats.Where(c => c.w > 0).Sum(c => c.w);
        if (tw <= 0) return (0, 0);
        double earned = cats.Where(c => c.avg != null && c.w > 0).Sum(c => c.w / tw * c.avg.Value);
        double remaining = cats.Where(c => c.avg == null && c.w > 0).Sum(c => c.w / tw);
        return (earned, remaining);
    }

    // The average needed on everything remaining to finish at target (this is also the lowest that still keeps it).
    // locked = already safe even at 0%; impossible = out of reach even at 100%.
    public static (string Status, double Value) Needed(double earned, double remaining, double target)
    {
        if (earned >= target - 1e-9) return ("locked", 0);
        if (remaining <= 1e-9) return ("impossible", 0);
        double x = (target - earned) / remaining;
        return x > 1 + 1e-9 ? ("impossible", x) : ("ok", Math.Max(0, x));
    }

    public static string Letter(double percent, IDictionary<string, double> cutoffs)
    {
        foreach (var kv in cutoffs.OrderByDescending(k => k.Value))
            if (percent >= kv.Value - 1e-9) return kv.Key;
        return "F";
    }

    // 0.8644 -> "86.4%"; up = round up to the next tenth so a needed score is never a hair short.
    public static string Pct(double frac, bool up = false)
    {
        double v = frac * 100;
        v = up ? Math.Ceiling(v * 10 - 1e-6) / 10 : Math.Round(v, 1);
        return v.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }
}
