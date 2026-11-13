using System.Text;
using System.Text.RegularExpressions;

namespace VowenLite;

// Turns a spoken folder name ("better verilog", "better very log", "downloads") into a real folder.
// Speech-to-text mangles names, so matching is forgiving: exact > aliases > known folders >
// fuzzy (spelling distance AND a sound-alike key) over folders two levels under Settings.TerminalRoots.
static class Folders
{
    static readonly string[] Skip = { "node_modules", "bin", "obj", "target", "dist", "build", "vendor", "__pycache__", ".git", "venv", ".venv", "out" };
    static List<string> cache;
    static DateTime cacheAt = DateTime.MinValue;

    public static string Resolve(string phrase, Settings s, out string problem)
    {
        problem = null;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        phrase = Regex.Replace((phrase ?? "").Trim().Trim('"', '\'', '.', ','), @"\s+(folder|directory|dir|project|repo|repository|repo)$", "", RegexOptions.IgnoreCase).Trim();
        phrase = Regex.Replace(phrase, @"^(?:the|my|our)\s+", "", RegexOptions.IgnoreCase).Trim();
        if (phrase.Length == 0 || Regex.IsMatch(phrase, @"^(home|user|user folder|home folder|here|this folder|current folder)$", RegexOptions.IgnoreCase)) return home;
        if (Path.IsPathRooted(phrase)) { if (Directory.Exists(phrase)) return phrase; problem = $"I couldn't find the folder {phrase}."; return null; }

        string q = Norm(phrase), qp = Phon(phrase);

        // 1. Your own aliases (exact, then fuzzy on the alias).
        foreach (var kv in s.FolderAliases)
            if (Directory.Exists(kv.Value) && (Norm(kv.Key) == q || Phon(kv.Key) == qp && q.Length >= 4)) return kv.Value;

        // 1b. "Le code" (the main projects folder) however speech-to-text hears it: le/lay/lee/la code, le coat, lecode, or just "code".
        string leCode = Path.Combine(home, "Le code");
        if (Directory.Exists(leCode) && Regex.IsMatch(phrase, @"^(?:le|lay|lee|la|luh|li|lei|leh|lé)?[\s-]*(?:code|coat|kode|cod|coed|pod|cold|cote|coad|cood|codes)$", RegexOptions.IgnoreCase)) return leCode;

        // 2. Windows known folders.
        var known = new Dictionary<string, string>
        {
            ["desktop"] = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            ["documents"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ["downloads"] = Path.Combine(home, "Downloads"),
            ["pictures"] = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            ["videos"] = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            ["music"] = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        };
        if (known.TryGetValue(q, out var k) && Directory.Exists(k)) return k;

        // 3. Fuzzy over the project folders.
        var scored = Candidates(s)
            .Select(d => (Dir: d, Name: Path.GetFileName(d.TrimEnd('\\')), Score: Score(Path.GetFileName(d.TrimEnd('\\')), q, qp)))
            .Where(x => x.Score > 0.45).OrderByDescending(x => x.Score).ThenBy(x => x.Dir.Length).ToList();
        if (scored.Count > 0 && scored[0].Score >= 0.74) return scored[0].Dir;

        problem = $"I couldn't find a folder called \"{phrase}\".";
        var close = scored.Select(x => x.Name).Distinct().Take(3).ToList();
        if (close.Count > 0) problem += " Closest: " + string.Join(", ", close) + ".";
        return null;
    }

    static List<string> Candidates(Settings s)
    {
        if (cache != null && DateTime.UtcNow - cacheAt < TimeSpan.FromSeconds(60)) return cache;
        var list = new List<string>();
        string leCode = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Le code");
        if (Directory.Exists(leCode)) list.Add(leCode);   // the projects folder itself ("le code", "lec code", "labcode"...)
        foreach (var root in s.TerminalRoots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                list.Add(root);
                foreach (var d1 in Directory.EnumerateDirectories(root))
                {
                    string n1 = Path.GetFileName(d1);
                    if (n1.StartsWith('.') || Skip.Contains(n1, StringComparer.OrdinalIgnoreCase)) continue;
                    list.Add(d1);
                    try
                    {
                        foreach (var d2 in Directory.EnumerateDirectories(d1).Take(80))
                        {
                            string n2 = Path.GetFileName(d2);
                            if (!n2.StartsWith('.') && !Skip.Contains(n2, StringComparer.OrdinalIgnoreCase)) list.Add(d2);
                        }
                    }
                    catch { }
                    if (list.Count > 4000) break;
                }
            }
            catch { }
        }
        cache = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        cacheAt = DateTime.UtcNow;
        return cache;
    }

    // 0..1. Spelling similarity, sound-alike similarity (slightly discounted), or containment.
    static double Score(string name, string q, string qp)
    {
        string n = Norm(name);
        if (n.Length == 0) return 0;
        if (n == q) return 1;
        if (q.Length < 4) return n.StartsWith(q) ? 0.8 : 0;   // too short to fuzzy-match safely
        double best = Math.Max(Sim(n, q), 0.97 * Sim(Phon(name), qp));
        if (q.Length >= 4 && n.Length >= 4 && (n.Contains(q) || q.Contains(n))) best = Math.Max(best, 0.82);
        return best;
    }

    static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]", "");

    // Rough sound-alike key: first letter, then consonants only, doubles collapsed ("betterverilog" ~ "better very log").
    static string Phon(string s)
    {
        string t = Norm(s)
            .Replace("ph", "f").Replace("ck", "k").Replace("ch", "x").Replace("sh", "x").Replace("kn", "n").Replace("wr", "r")
            .Replace('c', 'k').Replace('q', 'k').Replace('z', 's').Replace('w', 'v');
        if (t.Length == 0) return "";
        var sb = new StringBuilder().Append(t[0]);
        for (int i = 1; i < t.Length; i++)
        {
            char c = t[i];
            if ("aeiouyh".IndexOf(c) >= 0 || c == sb[^1]) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    static double Sim(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        int[] prev = new int[b.Length + 1], cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return 1.0 - (double)prev[b.Length] / Math.Max(a.Length, b.Length);
    }
}
