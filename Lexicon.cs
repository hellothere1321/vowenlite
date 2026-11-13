using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VowenLite;

// Proper-noun accuracy WITHOUT a bigger model (same whisper models, same RAM):
//  * Words carry whisper's own per-token confidence (whisper-cli -ojf).
//  * Snap(): a word whisper was UNSURE about that sounds like a known name (your Names, vocabulary, project folders,
//    apps, names in your planner, terms learned from Codex corrections) is replaced by that name. Confident words are
//    never touched, so "and" can't turn into "Sam".
//  * Canonicalize(): inside one meeting, different spellings of the same sound ("Priya"/"Preea"/"Pria") collapse
//    to the known name, or to the most frequent spelling.
//  * Learn(): word substitutions Codex made when it corrected a transcript become deterministic fixes
//    (learned.json), so the same mis-hearing is fixed for free in dictation and meetings from then on.
//  * ContextTerms(): words from the window you are dictating into are added to the whisper prompt.
static class Lexicon
{
    // ----------------------------------------------------------------- words with confidence

    public sealed class W
    {
        public string Pre = "", Core = "", Post = "";
        public double P = 1;                 // lowest token probability inside the word (1 = sure)
        public string Text => Pre + Core + Post;
    }

    static readonly Regex Split3 = new(@"^([^\p{L}\p{N}]*)(.*?)([^\p{L}\p{N}]*)$", RegexOptions.Singleline);

    static W MakeWord(string raw, double p)
    {
        var m = Split3.Match(raw);
        return new W { Pre = m.Groups[1].Value, Core = m.Groups[2].Value, Post = m.Groups[3].Value, P = p };
    }

    // Plain text (no token data): every word counts as sure, so Snap leaves it alone.
    public static List<W> FromText(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => MakeWord(t, 1)).ToList();

    // Words of one whisper JSON segment, from its tokens (falls back to the text when there are none).
    public static List<W> FromSegment(JsonElement seg)
    {
        string text = seg.TryGetProperty("text", out var te) ? te.GetString() ?? "" : "";
        if (!seg.TryGetProperty("tokens", out var toks) || toks.ValueKind != JsonValueKind.Array || text.IndexOfAny(new[] { '[', '(' }) >= 0)
            return FromText(text.Trim());
        var words = new List<W>();
        var raw = new StringBuilder();
        double p = 1;
        bool hasLetters = false;
        void Flush()
        {
            if (raw.Length > 0) words.Add(MakeWord(raw.ToString(), hasLetters ? p : 1));
            raw.Clear(); p = 1; hasLetters = false;
        }
        foreach (var t in toks.EnumerateArray())
        {
            string tt = t.TryGetProperty("text", out var x) ? x.GetString() ?? "" : "";
            if (tt.StartsWith("[_") || tt.StartsWith("<|") || tt.Length == 0) continue;
            if (tt.StartsWith(' ') || tt.StartsWith('\n')) { Flush(); tt = tt.TrimStart(); }
            raw.Append(tt);
            if (tt.Any(char.IsLetterOrDigit))
            {
                hasLetters = true;
                double tp = t.TryGetProperty("p", out var pe) && pe.TryGetDouble(out double d) ? d : 1;
                p = Math.Min(p, tp);
            }
        }
        Flush();
        return words;
    }

    public static string Join(List<W> ws) => string.Join(" ", ws.Select(w => w.Text)).Trim();

    // ----------------------------------------------------------------- sound key

    // Rough "how does it sound" key: merges look-alike consonants (b/p, d/t, g/k, v/f, c/k/s, ch/sh...), drops
    // vowels and collapses repeats. "Priya", "Preea", "Pria" all give the same key.
    public static string Key(string word)
    {
        string t = new string(word.ToLowerInvariant().Where(c => c >= 'a' && c <= 'z').ToArray());
        if (t.Length == 0) return "";
        t = Regex.Replace(t, "^(kn|gn|pn)", "n");
        t = Regex.Replace(t, "^wr", "r");
        t = Regex.Replace(t, "^wh", "w");
        t = Regex.Replace(t, "^sch", "sk");
        t = t.Replace("ph", "f").Replace("ght", "t").Replace("ck", "k").Replace("tch", "x")
             .Replace("ch", "x").Replace("sh", "x").Replace("th", "t").Replace("qu", "kw").Replace("dg", "j");
        t = Regex.Replace(t, "c(?=[eiy])", "s");
        var sb = new StringBuilder();
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            char m = c switch { 'c' => 'k', 'q' => 'k', 'z' => 's', 'v' => 'f', 'b' => 'p', 'd' => 't', 'g' => 'k', 'j' => 'x', _ => c };
            if (i > 0 && t[i - 1] == c) continue;                  // "nn" -> "n" (before vowels are dropped, so f-v-f stays two f)
            if (i > 0 && "aeiouyhw".IndexOf(m) >= 0) continue;     // vowels only matter at the start
            sb.Append(m);
        }
        return sb.ToString();
    }

    public static int Lev(string a, string b)
    {
        if (a == b) return 0;
        var prev = new int[b.Length + 1]; var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    static string Letters(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    // Same sound AND similar spelling (so "coffin" never becomes "Gavin").
    public static bool SoundsPublic(string a, string b) => Sounds(a, b);
    static bool Sounds(string a, string b)
    {
        string ka = Key(a), kb = Key(b);
        if (ka.Length < 3 || kb.Length < 3) return false;
        int d = Lev(ka, kb);
        string la = Letters(a), lb = Letters(b);
        double ratio = (double)Lev(la, lb) / Math.Max(la.Length, lb.Length);
        if (d == 0) return ratio <= 0.5;
        if (d == 1) return ka.Length >= 5 && kb.Length >= 5 ? ratio <= 0.5 : ratio <= 0.5 && ka[0] == kb[0];   // short keys: same first sound too
        return false;
    }

    // ----------------------------------------------------------------- common words (never snapped)

    static readonly HashSet<string> Common = new(
        ("a about above after again against all also am an and any are as at be because been before being below between both but by " +
         "can could did do does doing done down during each few for from further get got had has have having he her here hers herself him " +
         "himself his how i if in into is it its itself just like me more most my myself no nor not now of off on once only or other our ours " +
         "out over own same she should so some such than that the their theirs them then there these they this those through to too under " +
         "until up very was we were what when where which while who whom why will with would you your yours yourself yeah yes okay ok oh uh " +
         "um well right know think going gonna want wanna need make made say said see saw come came take took give gave look looked tell told " +
         "ask asked work worked try tried use used find found call called good great bad big small new old first last long little high low " +
         "next real sure maybe really actually basically literally probably pretty thing things stuff something anything nothing everything " +
         "someone anyone everyone people person guy guys man men woman women kid kids friend friends time times day days week weeks year years " +
         "today tonight tomorrow yesterday night morning evening home house school class game games play played playing watch watched video " +
         "videos phone computer screen call talk talked talking mean means meant let lets put keep kept feel felt seem seems turn turned start " +
         "started stop stopped open opened close closed read write wrote run ran move moved bring brought hold held show showed hear heard " +
         "love hate help helps wait waited pay paid buy bought sell sold eat ate drink drank sleep slept walk walked drive drove ride rode " +
         "one two three four five six seven eight nine ten hundred thousand million first second third many much lot lots every another " +
         "still even ever never always often sometimes already almost enough quite rather around away back again together without within " +
         "between across along behind beside beyond inside outside toward upon whether either neither though although since unless " +
         "mom dad mother father brother sister son daughter dude bro bruh man lol lmao fr ngl idk tbh imo basically anyway whatever " +
         "money dollar dollars cost price free cheap hard easy fast slow best better worse worst same different important point question " +
         "answer problem idea plan part place way ways case fact kind sort type group team company state country city world life water " +
         "food car room door window table paper book page word words number numbers name names line lines end side top bottom left center " +
         "email message text file files folder code data list item items test tests check checks build built fix fixed bug bugs update " +
         "version server client app apps site web page link links post posts chat chats voice mic audio sound music song songs movie movies " +
         "show shows episode season level levels point points score win won lose lost round match team player players server servers").Split(' '), StringComparer.OrdinalIgnoreCase);

    // ----------------------------------------------------------------- the lexicon

    public sealed record Entry(string Term, string Key, string Lower, int Words);

    static List<Entry> entries;
    static DateTime entriesAt = DateTime.MinValue;

    public static IEnumerable<string> SplitCsv(string csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Names to snap to, from everything the app already knows about you. Cached for a minute.
    public static List<Entry> Entries(Settings s)
    {
        if (entries != null && DateTime.UtcNow - entriesAt < TimeSpan.FromSeconds(60)) return entries;
        var terms = new List<string>();
        terms.AddRange(SplitCsv(s.Names));
        terms.AddRange(SplitCsv(s.Vocabulary));
        terms.AddRange(Vocab.KnownTerms(s));
        var l = Learned();
        terms.AddRange(l.Terms);
        terms.AddRange(l.Replacements.Values);
        terms.AddRange(s.Replacements.Values);
        terms.AddRange(PlannerNames());
        var list = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in terms)
        {
            string term = t.Trim();
            int words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (term.Length < 3 || term.Length > 40 || words > 3 || !term.Any(char.IsUpper)) continue;   // only proper-noun-like terms
            if (words == 1 && Common.Contains(term)) continue;
            string key = Key(term);
            if (key.Length < 3 || !seen.Add(term)) continue;
            list.Add(new Entry(term, key, term.ToLowerInvariant(), words));
        }
        // Names mined from your meetings come last: a spelling you set yourself wins any same-sound conflict.
        foreach (var mn in MeetingNames())
        {
            string mk = Key(mn);
            if (mk.Length >= 3 && !list.Any(e => e.Words == 1 && e.Key == mk) && seen.Add(mn)) list.Add(new Entry(mn, mk, mn.ToLowerInvariant(), 1));
        }
        // Project folders are usually lowercase ("my-app"): distinctive ones count as names too.
        foreach (var pn in Vocab.ProjectNames(s))
            if (pn.Length >= 6 && !pn.Contains(' ') && !Common.Contains(pn) && Key(pn).Length >= 3 && seen.Add(pn))
                list.Add(new Entry(pn, Key(pn), pn.ToLowerInvariant(), 1));
        entries = list;
        entriesAt = DateTime.UtcNow;
        return list;
    }

    // Capitalised words from your planner (events/tasks: people, places, classes): Payton, Aliesha, MMT...
    static IEnumerable<string> PlannerNames()
    {
        var names = new List<string>();
        try
        {
            foreach (var it in Planner.Live().OrderByDescending(i => i.Updated).Take(60))
                foreach (Match m in Regex.Matches(it.Title ?? "", @"(?<![.!?]\s)\b[A-Z][A-Za-z'\-]{3,}\b"))
                    if (!Common.Contains(m.Value.TrimEnd('\'', 's')) && !Common.Contains(m.Value)) names.Add(m.Value.Replace("'s", ""));
        }
        catch { }
        return names.Distinct().Take(40);
    }

    static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday", "January", "February", "March", "April", "June", "July",
      "August", "September", "October", "November", "December", "Thanks", "Okay", "Yeah", "Hello", "Sorry", "Please", "English", "American", "Google" };

    static List<(string Name, int Count)> meetingNames;
    static DateTime meetingNamesAt = DateTime.MinValue;

    // Names (capitalised mid-sentence words) that keep coming up in your own meeting transcripts: your friends,
    // teachers, games. A word needs 3+ mentions across your 20 newest meetings. Cached for 10 minutes.
    public static List<(string Name, int Count)> MeetingNamesCounted(string dir = null)
    {
        if (dir == null && meetingNames != null && DateTime.UtcNow - meetingNamesAt < TimeSpan.FromMinutes(10)) return meetingNames;
        var counts = new Dictionary<string, int>();
        try
        {
            dir ??= Paths.MeetingsDir;
            foreach (var d in Directory.GetDirectories(dir).OrderByDescending(x => x).Take(20))
            {
                string f = Path.Combine(d, "transcript.md");
                if (!File.Exists(f) || new FileInfo(f).Length > 600_000) continue;
                foreach (Match m in Regex.Matches(File.ReadAllText(f), @"(?<=[a-z,;] )[A-Z][a-z]{2,}\b"))
                    if (!Common.Contains(m.Value) && !NotNames.Contains(m.Value)) counts[m.Value] = counts.GetValueOrDefault(m.Value) + 1;
            }
        }
        catch { }
        var list = counts.Where(kv => kv.Value >= 3).OrderByDescending(kv => kv.Value).Take(40).Select(kv => (kv.Key, kv.Value)).ToList();
        if (dir == null || meetingNames == null) { meetingNames = list; meetingNamesAt = DateTime.UtcNow; }
        return list;
    }

    static IEnumerable<string> MeetingNames() => MeetingNamesCounted().Select(x => x.Name);

    // The few most-mentioned names, for the whisper prompt itself (so it hears them right the first time).
    public static string[] TopNames(Settings s, int n)
    {
        var mine = new HashSet<string>(SplitCsv(s.Names).Concat(SplitCsv(s.Vocabulary)).Concat(Vocab.KnownTerms(s)).Select(Key));
        return MeetingNamesCounted().Select(x => x.Name).Where(x => !mine.Contains(Key(x))).Take(n).ToArray();
    }

    // For Codex's correction prompt: the names it should prefer.
    public static string KnownList(Settings s, int max = 60)
    {
        var l = Entries(s).Where(e => e.Words <= 3).Select(e => e.Term).ToList();
        var first = SplitCsv(s.Names).Concat(SplitCsv(s.Vocabulary)).Concat(Learned().Terms).Concat(PlannerNames());
        return string.Join(", ", first.Concat(l).Distinct(StringComparer.OrdinalIgnoreCase).Take(max));
    }

    // ----------------------------------------------------------------- snapping

    const double Gate = 0.62;   // only words whisper was less than this sure about can be snapped

    static bool SpanOk(List<W> ws, int i, int n)
    {
        for (int k = i; k < i + n - 1; k++)
            if (ws[k].Post.Length > 0 && ws[k].Post.Trim(',', ';', ':', '-').Length > 0) return false;   // don't cross . ? ! etc.
        return true;
    }

    // Replaces unsure words that sound like a known name. Returns how many words were snapped.
    public static int Snap(List<W> ws, Settings s)
    {
        if (Environment.GetEnvironmentVariable("VOWENLITE_NOSNAP") == "1") return 0;   // baseline for --acctest
        var ents = Entries(s);
        if (ents.Count == 0) return 0;
        int snapped = 0;
        for (int i = 0; i < ws.Count; i++)
        {
            Entry best = null; int bestN = 0; double bestScore = double.MaxValue;
            for (int n = Math.Min(3, ws.Count - i); n >= 1; n--)
            {
                if (!SpanOk(ws, i, n)) continue;
                var span = ws.GetRange(i, n);
                string joined = string.Concat(span.Select(w => w.Core));
                string jl = Letters(joined);
                if (jl.Length < 3) continue;
                bool unsure = span.Min(w => w.P) < Gate && !span.All(w => w.Core.Length < 3 || Common.Contains(w.Core));
                foreach (var e in ents)
                {
                    string el = Letters(e.Term);
                    // Same letters, different spacing ("my app" -> MyApp, "Spice tify" -> Spicetify): safe whatever the confidence.
                    bool exact = n >= 2 && jl == el && el.Length >= 6 && e.Words == 1;
                    // Same word, wrong casing ("Vowenlite" -> VowenLite, "github" -> GitHub); only for names with an inner capital.
                    bool caseFix = n == 1 && jl == el && joined != e.Term && e.Term.Skip(1).Any(char.IsUpper);
                    if (!exact && !caseFix)
                    {
                        if (!unsure || jl == el || !Sounds(joined, e.Term)) continue;
                        // A span with an ordinary word in it ("Sam to") may only match a name of the same word count.
                        if (n > 1 && e.Words != n && span.Any(w => w.Core.Length < 3 || Common.Contains(w.Core))) continue;
                    }
                    double score = (exact || caseFix) ? -1 : Lev(Key(joined), e.Key) + (double)Lev(jl, el) / Math.Max(Math.Max(jl.Length, el.Length), 1) - n * 0.01;
                    if (score < bestScore) { best = e; bestN = n; bestScore = score; }
                }
                if (best != null) break;                                  // prefer the longest matching span
            }
            if (best == null) continue;
            var last = ws[i + bestN - 1];
            ws[i].Core = best.Term; ws[i].Post = last.Post; ws[i].P = 1;
            if (bestN > 1) ws.RemoveRange(i + 1, bestN - 1);
            snapped++;
        }
        return snapped;
    }

    // Text from one whisper JSON file: word confidence -> snap -> text (segments joined). Used by dictation.
    public static string FromJson(string json, Settings s, out int snapped)
    {
        snapped = 0;
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        if (!doc.RootElement.TryGetProperty("transcription", out var arr)) return "";
        foreach (var seg in arr.EnumerateArray())
        {
            var ws = FromSegment(seg);
            if (Environment.GetEnvironmentVariable("VOWENLITE_ACCDEBUG") == "1")
                Console.WriteLine("   p: " + string.Join(" ", ws.Where(w => w.P < 0.9).Select(w => w.Core + "=" + w.P.ToString("0.00"))));
            snapped += Snap(ws, s);
            string t = Join(ws);
            t = Regex.Replace(t, @"[\[\(][^\]\)]*[\]\)]", "").Trim();
            if (t.Length > 0) sb.Append(sb.Length > 0 ? " " : "").Append(t);
        }
        return sb.ToString();
    }

    // ----------------------------------------------------------------- one meeting: consistent spellings

    // Capitalised mid-sentence words that sound the same but are spelled differently are made one spelling:
    // a known name wins, otherwise the most frequent spelling (needs at least 2 uses to out-vote another).
    public static int Canonicalize(List<List<W>> segments, Settings s)
    {
        var ents = Entries(s);
        var counts = new Dictionary<string, Dictionary<string, int>>();   // key -> spelling -> count
        foreach (var ws in segments)
            for (int i = 0; i < ws.Count; i++)
            {
                var w = ws[i];
                if (!IsNameLike(ws, i)) continue;
                string k = Key(w.Core);
                if (k.Length < 3) continue;
                if (!counts.TryGetValue(k, out var d)) counts[k] = d = new();
                d[w.Core] = d.GetValueOrDefault(w.Core) + 1;
            }
        var winner = new Dictionary<string, string>();   // spelling -> chosen spelling
        foreach (var (k, d) in counts)
        {
            if (d.Count < 2) continue;
            string pick = ents.FirstOrDefault(e => e.Words == 1 && e.Key == k && d.ContainsKey(e.Term))?.Term
                       ?? d.OrderByDescending(x => x.Value).First().Key;
            int pickCount = d[pick];
            foreach (var (sp, c) in d)
                if (sp != pick && (c < pickCount || ents.Any(e => e.Term == pick)) && Sounds(sp, pick)) winner[sp] = pick;
        }
        int changed = 0;
        if (winner.Count == 0) return 0;
        foreach (var ws in segments)
            foreach (var w in ws)
                if (winner.TryGetValue(w.Core, out var to)) { w.Core = to; changed++; }
        return changed;
    }

    static bool IsNameLike(List<W> ws, int i)
    {
        var w = ws[i];
        if (w.Core.Length < 3 || !char.IsUpper(w.Core[0]) || Common.Contains(w.Core)) return false;
        if (i == 0) return false;                                  // can't tell a name from a sentence start
        string prev = ws[i - 1].Post;
        return !(prev.Contains('.') || prev.Contains('?') || prev.Contains('!'));
    }

    // Uncertain words worth showing to Codex (it checks these first). Lowest confidence first, capped.
    public static HashSet<W> Flagged(IEnumerable<List<W>> segments, int totalWords)
    {
        var cands = segments.SelectMany(x => x)
            .Where(w => w.P < 0.45 && Letters(w.Core).Length >= 4 && !Common.Contains(w.Core))
            .OrderBy(w => w.P).Take(Math.Max(25, totalWords / 15));
        return new HashSet<W>(cands);
    }

    public static string JoinMarked(List<W> ws, HashSet<W> flagged) =>
        string.Join(" ", ws.Select(w => flagged.Contains(w) ? w.Pre + "⟦" + w.Core + "⟧" + w.Post : w.Text)).Trim();

    // ----------------------------------------------------------------- learning from Codex corrections

    public sealed class LearnedData
    {
        public List<string> Terms { get; set; } = new();
        public Dictionary<string, string> Replacements { get; set; } = new();
        public Dictionary<string, int> Pending { get; set; } = new();
    }

    static LearnedData learned;
    static DateTime learnedStamp;
    static string LearnedPath => Path.Combine(Paths.DataDir, "learned.json");

    public static LearnedData Learned()
    {
        try
        {
            var fi = new FileInfo(LearnedPath);
            if (learned != null && (!fi.Exists || fi.LastWriteTimeUtc == learnedStamp)) return learned;
            if (fi.Exists)
            {
                learned = JsonSerializer.Deserialize<LearnedData>(File.ReadAllText(LearnedPath)) ?? new();
                learnedStamp = fi.LastWriteTimeUtc;
                entries = null;                                    // lexicon must pick up new terms
            }
        }
        catch { }
        return learned ??= new LearnedData();
    }

    static void SaveLearned(LearnedData d)
    {
        try
        {
            string tmp = LearnedPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, LearnedPath, true);
            learned = d; learnedStamp = new FileInfo(LearnedPath).LastWriteTimeUtc; entries = null;
        }
        catch { }
    }

    static readonly Regex Line = new(@"^\[(\d+(?::\d+)+)\] (You|Others): (.*)$");
    static readonly Regex WordRx = new(@"[\p{L}\p{N}][\p{L}\p{N}'.\-]*");

    // Sentence-initial capitals are lowercased first, so a capital that is left can only mean a name/term.
    static List<string> Words(string t) =>
        WordRx.Matches(Regex.Replace(t, @"(^|[.!?]\s+)([A-Z])(?=[a-z])", m => m.Groups[1].Value + char.ToLowerInvariant(m.Groups[2].Value[0])))
            .Select(m => m.Value.TrimEnd('.', '-', '\'')).Where(w => w.Length > 0).ToList();

    // Compares the speech-to-text transcript with Codex's corrected one, line by line, and remembers the word
    // substitutions that were mis-hearings of a name (not rewordings). A substitution seen twice becomes a fix.
    public static int Learn(string rawText, string fixedText)
    {
        var raw = new Dictionary<string, string>();
        foreach (var line in rawText.Split('\n'))
        { var m = Line.Match(line.TrimEnd('\r')); if (m.Success) raw[m.Groups[1].Value + m.Groups[2].Value] = m.Groups[3].Value; }
        var d = Learned();
        int found = 0;
        foreach (var line in fixedText.Split('\n'))
        {
            var m = Line.Match(line.TrimEnd('\r'));
            if (!m.Success || !raw.TryGetValue(m.Groups[1].Value + m.Groups[2].Value, out var before)) continue;
            foreach (var (from, to) in Diff(Words(before), Words(m.Groups[3].Value)))
            {
                found++;
                if (!d.Terms.Contains(to, StringComparer.OrdinalIgnoreCase) && Settings.GoodTerm(to)) d.Terms.Add(to);
                string key = from.ToLowerInvariant();
                if (d.Replacements.ContainsKey(key)) continue;
                int n = d.Pending.GetValueOrDefault(key + ">" + to) + 1;
                d.Pending[key + ">" + to] = n;
                if (n >= 2) d.Replacements[key] = to;
            }
        }
        while (d.Terms.Count > 120) d.Terms.RemoveAt(0);
        if (found > 0) SaveLearned(d);
        return found;
    }

    // Word substitutions (1-2 words -> 1-2 words) between two versions of a line that look like name mis-hearings.
    static IEnumerable<(string From, string To)> Diff(List<string> a, List<string> b)
    {
        int n = a.Count, m = b.Count;
        if (n == 0 || m == 0 || n > 120 || m > 120) yield break;
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = string.Equals(a[i], b[j], StringComparison.OrdinalIgnoreCase) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && string.Equals(a[x], b[y], StringComparison.OrdinalIgnoreCase)) { x++; y++; continue; }
            int sx = x, sy = y;
            while ((x < n || y < m) && !(x < n && y < m && string.Equals(a[x], b[y], StringComparison.OrdinalIgnoreCase)))
            {
                if (y >= m || (x < n && lcs[x + 1, y] >= lcs[x, y + 1])) x++; else y++;
            }
            var ra = a.GetRange(sx, x - sx); var rb = b.GetRange(sy, y - sy);
            if (ra.Count is < 1 or > 2 || rb.Count is < 1 or > 2) continue;
            string from = string.Join(" ", ra), to = string.Join(" ", rb);
            if (Letters(from) == Letters(to)) continue;                                     // only casing / spacing / hyphens changed
            if (Letters(from).TrimEnd('s') == Letters(to).TrimEnd('s')) continue;           // plural / singular only
            if (!to.Any(c => char.IsUpper(c) || char.IsDigit(c))) continue;                 // fixes to names/terms only
            if (ra.All(w => w.Length < 3 || Common.Contains(w))) continue;
            if (!Sounds(from.Replace(" ", ""), to.Replace(" ", ""))) continue;
            yield return (from, to);
        }
    }

    // ----------------------------------------------------------------- prompt context

    static readonly HashSet<string> UiWords = new(StringComparer.OrdinalIgnoreCase)
    { "Visual", "Studio", "Code", "Google", "Chrome", "Microsoft", "Edge", "Windows", "Explorer", "Document", "Untitled", "Notepad",
      "Settings", "Search", "Inbox", "Home", "Page", "Window", "Browser", "Firefox", "Terminal", "PowerShell", "Command", "Prompt", "File", "Folder" };

    // Proper-noun-like words from the title of the window you are dictating into (a doc, repo, chat, channel...).
    public static string[] ContextTerms()
    {
        string title;
        try { var sb = new StringBuilder(256); GetWindowText(GetForegroundWindow(), sb, sb.Capacity); title = sb.ToString(); }
        catch { return Array.Empty<string>(); }
        return Regex.Matches(title, @"[A-Za-z][A-Za-z0-9'\-]{3,}").Select(m => m.Value)
            .Where(w => w.Any(char.IsUpper) && !Common.Contains(w) && !UiWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
}
