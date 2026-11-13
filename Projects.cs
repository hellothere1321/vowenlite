using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VowenLite;

// Project memory for Luna, built to SAVE context rather than fill it. Folders under the first TerminalRoot ("Le code") are the project list.
// What goes into a Codex prompt is tiered by how relevant the project is, and capped:
//   tier 0  request talks about "my projects" / "where we left off" but names none -> one line: the 6 latest projects with their state (~60 tokens)
//   tier 1  last project talked about, > 10 min ago (within 30)                    -> one light line (name + state), ignore unless relevant (~50 tokens)
//   tier 2  project named, or a follow-up within 10 min                             -> a DIGEST of at most ~1200 chars: summary, state, next step, facts (version, repo...),
//                                                                                     live git line, 3 recent requests, 3 notes
//   tier 2b a second project named in the same request                              -> a brief digest (~500 chars) so cross-project requests work
// Everything else (all notes, full history) lives in a per-project file `luna\projects\<name>.md` that Codex reads ONLY when the digest is not enough.
// Memory is current-state, not a log: `PROJECT: name | state: ...` / `next: ...` / `summary: ...` / `fact: key = value` REPLACE the old value;
// only free-form lines append. States are also filled in locally from task answers and from `GIT:` hook results, so it stays up to date without extra tokens.
static class Projects
{
    sealed class Project
    {
        public string Name { get; set; }
        public string Dir { get; set; }
        public List<string> Aliases { get; set; } = new();
        public string Summary { get; set; }                 // what it is, one line (seeded from AGENTS.md/README once, then Codex can replace it)
        public string State { get; set; }                   // where things stand now (replaced, never appended)
        public DateTime StateAt { get; set; }
        public string Next { get; set; }                    // the next step, if any
        public Dictionary<string, string> Facts { get; set; } = new();   // version, repo, branch, how to run... (keyed, replaced)
        public List<string> Notes { get; set; } = new();    // other lasting facts (free-form, append)
        public List<string> History { get; set; } = new();
        public DateTime Updated { get; set; }
    }
    sealed class Store
    {
        public List<Project> Items { get; set; } = new();
        public string Active { get; set; }
        public DateTime ActiveAt { get; set; }
    }

    const int FullBudget = 1000, BriefBudget = 450;
    // Answers that say nothing about the project's state: refusals, questions back, promises.
    static readonly Regex NotState = new(@"^\W*(?:Luna couldn|I\s+couldn|I\s+can[’']?t|I\s+cannot|I[’']ll\b|I\s+will\b|Could you|Can you|Not completely|Sorry|Which\b|What\b|Are you|Do you)", RegexOptions.IgnoreCase);
    static readonly object gate = new();
    static Store store;
    static string FilePath => Path.Combine(Paths.DataDir, "projects.json");
    static string DocDir => Path.Combine(Paths.DataDir, "luna", "projects");   // inside Luna's working folder, so Codex can read it
    static readonly TimeSpan FollowWindow = TimeSpan.FromMinutes(30), FreshWindow = TimeSpan.FromMinutes(10);
    static string explicitProject;   // set when the current request belongs to a project, so the exchange can be logged
    static bool stateWritten;        // Codex wrote a `state:` line during this request (then no automatic one)

    static Store Load()
    {
        lock (gate)
        {
            if (store != null) return store;
            try { store = File.Exists(FilePath) ? JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath)) ?? new() : new(); }
            catch { store = new(); }
            foreach (var p in store.Items)
            {
                p.Facts ??= new();
                // refusals and promises ("I can't run git…", "I'll figure it out") taught nothing: drop their answers from history and from state
                for (int i = 0; i < p.History.Count; i++)
                {
                    int arrow = p.History[i].IndexOf(" -> ", StringComparison.Ordinal);
                    if (arrow > 0 && NotState.IsMatch(p.History[i].Substring(arrow + 4))) p.History[i] = p.History[i].Substring(0, arrow);
                }
                if (p.State != null && NotState.IsMatch(p.State)) p.State = null;
                // no state yet: the newest history answer that says what was done
                if (string.IsNullOrWhiteSpace(p.State))
                    foreach (var h in Enumerable.Reverse(p.History))
                    {
                        int arrow = h.IndexOf(" -> ", StringComparison.Ordinal);
                        if (arrow < 0) continue;
                        string ans = h.Substring(arrow + 4).Trim();
                        if (ans.Length >= 25 && !ans.EndsWith("?") && !NotState.IsMatch(ans)) { p.State = Regex.Replace(ans, @"[*`]", ""); break; }
                    }
            }
            return store;
        }
    }
    static void Save()
    {
        lock (gate)
        {
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Load(), new JsonSerializerOptions { WriteIndented = true })); } catch { }
            try { WriteDocs(); } catch { }
        }
    }

    // One readable file per project with everything (the prompt only carries the digest).
    static string DocPath(Project p) => Path.Combine(DocDir, Regex.Replace(p.Name, @"[^A-Za-z0-9._-]", "_") + ".md");
    static void WriteDocs()
    {
        Directory.CreateDirectory(DocDir);
        foreach (var p in Load().Items)
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(p.Name).Append('\n');
            if (p.Dir != null) sb.Append("Folder: ").Append(p.Dir).Append('\n');
            if (!string.IsNullOrWhiteSpace(p.Summary)) sb.Append("Summary: ").Append(p.Summary).Append('\n');
            if (!string.IsNullOrWhiteSpace(p.State)) sb.Append("State: ").Append(p.State).Append(p.StateAt != default ? "  (" + p.StateAt.ToString("yyyy-MM-dd HH:mm") + ")" : "").Append('\n');
            if (!string.IsNullOrWhiteSpace(p.Next)) sb.Append("Next: ").Append(p.Next).Append('\n');
            if (p.Facts.Count > 0) { sb.Append("\n## Facts\n"); foreach (var kv in p.Facts) sb.Append("- ").Append(kv.Key).Append(": ").Append(kv.Value).Append('\n'); }
            if (p.Notes.Count > 0) { sb.Append("\n## Notes\n"); foreach (var n in p.Notes) sb.Append("- ").Append(n).Append('\n'); }
            if (p.History.Count > 0) { sb.Append("\n## Recent requests\n"); foreach (var h in p.History) sb.Append("- ").Append(h).Append('\n'); }
            string path = DocPath(p), text = sb.ToString();
            if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text, new UTF8Encoding(false));
        }
    }

    static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]", "");

    // ---------------- project list: folders under the projects root + whatever is already tracked

    static readonly string[] SkipDirs = { "node_modules", "bin", "obj", "target", "dist", "build", "vendor", "__pycache__", "venv", "out" };
    static List<(string Name, string Dir)> cand;
    static DateTime candAt = DateTime.MinValue;

    static List<(string Name, string Dir)> Candidates(Settings s)
    {
        if (cand != null && DateTime.UtcNow - candAt < TimeSpan.FromSeconds(60)) return cand;
        var list = new List<(string, string)>();
        try
        {
            string root = s.TerminalRoots.FirstOrDefault();
            if (root != null && Directory.Exists(root))
                foreach (var d in Directory.EnumerateDirectories(root))
                {
                    string n = Path.GetFileName(d);
                    if (n.StartsWith('.') || SkipDirs.Contains(n, StringComparer.OrdinalIgnoreCase) || Norm(n).Length < 5) continue;
                    list.Add((n, d));
                }
        }
        catch { }
        lock (gate) foreach (var p in Load().Items) if (!list.Any(x => Norm(x.Item1) == Norm(p.Name))) list.Add((p.Name, p.Dir));
        cand = list; candAt = DateTime.UtcNow;
        return cand;
    }

    // ---------------- finding the project(s) a sentence is about

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

    // The tracked/candidate projects named in the text, best first (1 to 3 consecutive words are tried per name).
    // The first needs any fuzzy match; further ones need a strong match (>= 0.85) so a stray word doesn't pull in a second project.
    static List<(string Name, string Dir)> FindMentions(string text, Settings s, int max)
    {
        var res = new List<(string Name, string Dir, double Score)>();
        var words = Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+").Select(m => m.Value).ToArray();
        if (words.Length == 0) return new();
        foreach (var c in Candidates(s))
        {
            var keys = new List<string> { Norm(c.Name) };
            lock (gate) { var p = Load().Items.FirstOrDefault(x => Norm(x.Name) == Norm(c.Name)); if (p != null) keys.AddRange(p.Aliases.Select(Norm)); }
            double bestScore = 0;
            foreach (var key in keys.Where(k => k.Length >= 5))
                for (int i = 0; i < words.Length; i++)
                {
                    string joined = "";
                    for (int n = 1; n <= 3 && i + n <= words.Length; n++)
                    {
                        joined += words[i + n - 1];
                        if (joined.Length < 4) continue;
                        int allow = key.Length >= 9 ? 2 : 1;
                        double score = 0;
                        if (joined == key) score = 1;
                        else if (Lev(joined, key) <= allow) score = 0.9 - 0.1 * Lev(joined, key);
                        else if (joined.Length >= key.Length && joined.StartsWith(key) && joined.Length <= key.Length + 4) score = 0.75;   // "free jammed"
                        if (score > bestScore) bestScore = score;
                    }
                }
            if (bestScore > 0) res.Add((c.Name, c.Dir, bestScore));
        }
        var ordered = res.OrderByDescending(x => x.Score).ToList();
        return ordered.Where((x, i) => i == 0 || x.Score >= 0.85).Take(max).Select(x => (x.Name, x.Dir)).ToList();
    }

    static (string Name, string Dir)? FindMention(string text, Settings s)
    {
        var l = FindMentions(text, s, 1);
        if (l.Count == 0) return null;
        return l[0];
    }

    // The folder of the project named in a phrase ("my-app", "the my app repo"), or null.
    public static string ResolveDir(string phrase, Settings s) => FindMention(phrase, s)?.Dir;

    static Project Get(string name, string dir, bool create)
    {
        lock (gate)
        {
            var st = Load();
            var p = st.Items.FirstOrDefault(x => Norm(x.Name) == Norm(name));
            if (p == null && create) { p = new Project { Name = name, Dir = dir, Updated = DateTime.Now }; st.Items.Add(p); SeedFromLog(p); }
            if (p != null && p.Dir == null && dir != null) p.Dir = dir;
            return p;
        }
    }

    // A project tracked for the first time starts with what daemon.log already knows: earlier Luna requests and answers that named it.
    static void SeedFromLog(Project p)
    {
        try
        {
            string log = Path.Combine(Paths.DataDir, "daemon.log");
            if (!File.Exists(log)) return;
            string key = Norm(p.Name);
            var lines = File.ReadAllLines(log).TakeLast(1500).Where(x => Norm(x).Contains(key) && x.Contains("luna:")).ToList();
            foreach (var x in lines.Where(x => x.Contains("luna: answer:") && !x.Contains("couldn") && !x.Contains("couldn2019t")).TakeLast(3))
                p.Notes.Add("(earlier answer) " + Clip(x.Substring(x.IndexOf("answer:", StringComparison.Ordinal) + 7).Trim(), 210));
            foreach (var x in lines.Where(x => x.Contains("-> codex")).TakeLast(4))
            {
                int q = x.IndexOf('"');
                p.History.Add(x.Substring(0, Math.Min(16, x.Length)).Replace('T', ' ') + "  " + Clip(q >= 0 ? x.Substring(q).Trim('"') : x, 120));
            }
        }
        catch { }
    }

    static void SetActive(string name) { lock (gate) { var st = Load(); st.Active = name; st.ActiveAt = DateTime.Now; } }

    // ---------------- the block that goes into a Luna prompt ("" = no project involved)

    static readonly Regex ProjectTalk = new(@"\b(?:projects?|repos?|repositor(?:y|ies)|my (?:app|apps|site|website|extension|extensions)|continue|resume|pick up|carry on|left off|last time|where (?:was|were|are) we)\b", RegexOptions.IgnoreCase);
    static readonly Regex FollowCue = new(@"^\W*(?:and|also|now|then|next|ok(?:ay)?|yes|yeah|yep|sure|go ahead|do it|continue|keep going|try again|again|retry|why|what (?:happened|about)|how about|so|can you (?:also|now|then))\b", RegexOptions.IgnoreCase);

    public static string Context(string request, Settings s)
    {
        explicitProject = null; stateWritten = false;
        try
        {
            var hits = FindMentions(request, s, 2);
            if (hits.Count == 0)
            {
                var st = Load();
                var known = st.Active == null || DateTime.Now - st.ActiveAt > FollowWindow ? null : Get(st.Active, null, false);
                if (known == null) return Index(request);
                bool fresh = DateTime.Now - st.ActiveAt <= FreshWindow;
                if (!fresh) return Light(known);
                if (FollowCue.IsMatch(request)) { explicitProject = known.Name; SetActive(known.Name); }   // a real follow-up keeps the chain going and gets logged
                return Digest(known, FullBudget, follow: true);
            }
            var first = Get(hits[0].Name, hits[0].Dir, create: true);
            explicitProject = first.Name; SetActive(first.Name);
            var sb = new StringBuilder(Digest(first, FullBudget, follow: false));
            if (hits.Count > 1) sb.Append(Digest(Get(hits[1].Name, hits[1].Dir, create: true), BriefBudget, follow: false, brief: true));
            Save();
            return sb.ToString();
        }
        catch { return ""; }
    }

    // Tier 0: no project named, but the request talks about projects / picking up where we left off.
    static string Index(string request)
    {
        if (!ProjectTalk.IsMatch(request)) return "";
        var items = Load().Items.OrderByDescending(x => x.Updated).Take(6).ToList();
        if (items.Count == 0) return "";
        return "Projects the user has (name one and its memory loads; ignore this if the request isn't about a project): "
             + string.Join("; ", items.Select(p => { string t = !string.IsNullOrWhiteSpace(p.State) ? p.State : p.Summary; return p.Name + (string.IsNullOrWhiteSpace(t) ? "" : " — " + Clip(t, 55)); })) + "\n\n";
    }

    // Tier 1: last project more than 10 min ago.
    static string Light(Project p)
        => $"Last project talked about: \"{p.Name}\"{(string.IsNullOrWhiteSpace(p.State) ? "" : " — " + Clip(p.State, 90))}. Ignore unless the request is about it (naming it loads its memory).\n\n";

    // Tier 2: the digest, lines in priority order, cut at the budget; the instruction footer always survives.
    static string Digest(Project p, int budget, bool follow, bool brief = false)
    {
        EnsureSummary(p);
        var lines = new List<string>();
        string head = (follow ? "Project context (the user's last conversation was about it; use only if the new request is about it): " : "Project context: ")
                    + "\"" + p.Name + "\"" + (p.Dir != null ? " at " + p.Dir : "") + (string.IsNullOrWhiteSpace(p.Summary) ? "" : " — " + p.Summary);
        lines.Add(head);
        if (!string.IsNullOrWhiteSpace(p.State)) lines.Add("State: " + Clip(p.State, 200) + (p.StateAt != default ? " (" + Ago(DateTime.Now - p.StateAt) + ")" : ""));
        if (!string.IsNullOrWhiteSpace(p.Next)) lines.Add("Next: " + Clip(p.Next, 160));
        if (p.Facts.Count > 0) lines.Add("Facts: " + string.Join("; ", p.Facts.Select(kv => kv.Key + " = " + kv.Value)));
        if (!brief)
        {
            if (p.Dir != null && Directory.Exists(p.Dir))   // the folder listing only when there is no summary to say what it is
                lines.AddRange(SnapshotLines(p.Dir).Where(l => string.IsNullOrWhiteSpace(p.Summary) || !l.StartsWith("Top level:")));
            if (p.History.Count > 0) lines.Add("Recent asks: " + string.Join(" | ", p.History.TakeLast(2).Select(h => Clip(Regex.Replace(h, @"^\d\d-\d\d \d\d:\d\d\s+|^\d{4}-\d\d-\d\d \d\d:\d\d\s+", "").Split(" -> ")[0], 75))));
            if (p.Notes.Count > 0) lines.Add("Notes: " + string.Join(" | ", p.Notes.TakeLast(2).Select(n => Clip(n.Replace("(earlier answer) ", ""), 110))) + (p.Notes.Count > 2 ? $" (+{p.Notes.Count - 2} more in the file)" : ""));
        }
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            if (sb.Length + l.Length + 1 > budget && sb.Length > 0) break;
            sb.Append(l).Append('\n');
        }
        if (!brief)
        {
            sb.Append("More (only if needed): ").Append(DocPath(p)).Append('\n');
            sb.Append("After real work add `PROJECT: ").Append(p.Name).Append(" | state: <where it stands, one sentence>` (replaces the old state); also `| next: <step or none>`, `| fact: version = 1.2.0` (replaces that key).\n");
        }
        return sb.Append('\n').ToString();
    }

    // First sentence/paragraph of AGENTS.md or README.md, once, so a project has a summary without any model call.
    static void EnsureSummary(Project p)
    {
        if (p.Summary != null) return;
        p.Summary = "";
        try
        {
            if (p.Dir == null) return;
            foreach (var name in new[] { "AGENTS.md", "CLAUDE.md", "README.md", "readme.md" })
            {
                string f = Path.Combine(p.Dir, name);
                if (!File.Exists(f)) continue;
                var para = new List<string>();
                foreach (var raw in File.ReadLines(f).Take(80))
                {
                    string l = raw.Trim();
                    if (l.Length == 0 || l.StartsWith('#') || l.StartsWith("```") || l.StartsWith('|') || l.StartsWith('>') || l.StartsWith("![") || l.StartsWith('<') || l.StartsWith("@") || l.StartsWith("[!"))
                    { if (para.Count > 0 && l.Length == 0) break; continue; }
                    para.Add(l);
                    if (string.Join(" ", para).Length > 260) break;
                }
                string t = Regex.Replace(string.Join(" ", para), @"\[([^\]]*)\]\([^)]*\)|[*_`]", "$1");
                if (t.Length >= 30) { p.Summary = Clip(t, 220); lock (gate) p.Updated = p.Updated == default ? DateTime.Now : p.Updated; return; }
            }
        }
        catch { }
    }

    // After an exchange: log what was asked, and fill in the project state from a task answer when Codex didn't write one itself.
    public static void AfterExchange(string request, string answer, bool task = false)
    {
        string name = explicitProject; explicitProject = null;
        if (name == null) return;
        var p = Get(name, null, false);
        if (p == null) return;
        string a = string.IsNullOrWhiteSpace(answer) ? "done" : Regex.Replace(answer.Trim(), @"\s+", " ");
        string line = DateTime.Now.ToString("MM-dd HH:mm") + "  " + Clip(request.Trim(), 100) + (NotState.IsMatch(a) ? "" : " -> " + Clip(a, 110));   // a refusal/promise is not worth remembering
        lock (gate)
        {
            p.History.Add(line); while (p.History.Count > 15) p.History.RemoveAt(0);
            p.Updated = DateTime.Now;
            // a finished task with a plain statement of what happened is a good-enough state; questions, failures and questions-back are not
            if (task && !stateWritten && answer != null && a.Length >= 25 && !a.EndsWith("?") && !NotState.IsMatch(a))
            {
                int dot = Regex.Match(a, @"[.!](?:\s|$)").Index;
                p.State = Clip(Regex.Replace(dot > 30 ? a.Substring(0, dot + 1) : a, @"[*`]", ""), 180);
                p.StateAt = DateTime.Now;
            }
        }
        Save();
    }

    // PROJECT: hook from Codex. "state: ..", "next: ..", "summary: ..", "fact: key = value" replace; "done" clears next; anything else is a note.
    public static void AddNote(string nameText, string note, Settings s)
    {
        nameText = nameText.Trim();
        var hit = FindMention(nameText, s);
        var p = hit != null ? Get(hit.Value.Name, hit.Value.Dir, true) : Get(nameText, null, true);
        note = Regex.Replace(note.Trim().TrimEnd('.'), @"\s+", " ");
        if (note.Length < 4) return;
        var m = Regex.Match(note, @"^(state|next|summary|fact|done)\s*[:=]?\s*(.*)$", RegexOptions.IgnoreCase);
        lock (gate)
        {
            if (m.Success && (m.Groups[1].Value.ToLowerInvariant() == "done" || m.Groups[2].Value.Length >= 2))
            {
                string v = m.Groups[2].Value.Trim();
                switch (m.Groups[1].Value.ToLowerInvariant())
                {
                    case "state": p.State = Clip(v, 200); p.StateAt = DateTime.Now; stateWritten = true; break;
                    case "next": p.Next = Regex.IsMatch(v, @"^(?:none|nothing|n/?a|-)$", RegexOptions.IgnoreCase) ? null : Clip(v, 180); break;
                    case "done": p.Next = null; break;
                    case "summary": p.Summary = Clip(v, 220); break;
                    case "fact":
                    {
                        var kv = Regex.Match(v, @"^([^=:]{1,30}?)\s*[=:]\s*(.+)$");
                        if (kv.Success)
                        {
                            p.Facts[kv.Groups[1].Value.Trim().ToLowerInvariant()] = Clip(kv.Groups[2].Value.Trim(), 90);
                            while (p.Facts.Count > 12) p.Facts.Remove(p.Facts.Keys.First());
                        }
                        else if (!p.Notes.Any(n => n.Equals(v, StringComparison.OrdinalIgnoreCase))) p.Notes.Add(Clip(v, 220));
                        break;
                    }
                }
            }
            else if (!p.Notes.Any(n => n.Equals(note, StringComparison.OrdinalIgnoreCase))) p.Notes.Add(Clip(note, 220));
            while (p.Notes.Count > 20) p.Notes.RemoveAt(0);
            p.Updated = DateTime.Now;
        }
        SetActive(p.Name);
        Save();
    }

    // A `GIT:` hook result for a repo that is a tracked project: remember it as a fact (free, no model involved).
    public static void NoteGit(string dir, string op, string result)
    {
        try
        {
            if (dir == null) return;
            Project p;
            lock (gate) p = Load().Items.FirstOrDefault(x => x.Dir != null && string.Equals(x.Dir.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (p == null) return;
            string r = Regex.Replace(result ?? "", @"\s+", " ").Trim();
            int colon = r.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0 && colon < 30) r = r.Substring(colon + 2);
            if (op == "status" || op == "log" || r.Length == 0) return;   // those are read-only lookups, not events
            lock (gate) { p.Facts["last git"] = op + ": " + Clip(r, 70) + " (" + DateTime.Now.ToString("MM-dd HH:mm") + ")"; p.Updated = DateTime.Now; }
            Save();
        }
        catch { }
    }

    static string Clip(string t, int n) => t.Length > n ? t.Substring(0, n - 1).TrimEnd() + "…" : t;

    // ---------------- quick local look at the folder (cached a minute): git state, recent changes, top-level names

    static readonly Dictionary<string, (DateTime At, List<string> Lines)> snaps = new();

    static List<string> SnapshotLines(string dir)
    {
        lock (snaps) if (snaps.TryGetValue(dir, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(60)) return c.Lines;
        var res = new List<string>();
        try
        {
            string g = GitLine(dir);
            if (g != null) res.Add(g);

            var recent = new List<(string Rel, DateTime T)>();
            int seen = 0;
            void Walk(string d, int depth)
            {
                if (depth > 3 || seen > 1500) return;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(d)) { if (++seen > 1500) return; recent.Add((Path.GetRelativePath(dir, f), File.GetLastWriteTime(f))); }
                    foreach (var sub in Directory.EnumerateDirectories(d))
                    {
                        string n = Path.GetFileName(sub);
                        if (n.StartsWith('.') || SkipDirs.Contains(n, StringComparer.OrdinalIgnoreCase)) continue;
                        Walk(sub, depth + 1);
                    }
                }
                catch { }
            }
            Walk(dir, 0);
            var latest = recent.OrderByDescending(x => x.T).Take(4).ToList();
            if (latest.Count > 0)
                res.Add("Recently changed: " + string.Join(", ", latest.Select(x => $"{x.Rel} ({Ago(DateTime.Now - x.T)})")));

            var top = Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName)
                .Where(n => !n.StartsWith('.') && !SkipDirs.Contains(n, StringComparer.OrdinalIgnoreCase)).Take(12).ToList();
            if (top.Count > 0) res.Add("Top level: " + string.Join(", ", top));
        }
        catch { }
        lock (snaps) snaps[dir] = (DateTime.UtcNow, res);
        return res;
    }

    // "Git: main @ 483bf39 "Merge notes" · origin github.com/user/my-app · 2 uncommitted" (null if not a repo). A few quick local git calls, no network.
    static string GitLine(string dir)
    {
        if (!Directory.Exists(Path.Combine(dir, ".git")) && !File.Exists(Path.Combine(dir, ".git"))) return null;
        string branch = GitOut(dir, "rev-parse", "--abbrev-ref", "HEAD");
        if (branch == null) return null;
        string last = GitOut(dir, "log", "-1", "--format=%h %s");
        string remote = GitOut(dir, "remote", "get-url", "origin");
        string dirty = GitOut(dir, "status", "--porcelain");
        var sb = new StringBuilder("Git: ").Append(branch);
        if (!string.IsNullOrWhiteSpace(last)) { int sp = last.IndexOf(' '); sb.Append(" @ ").Append(sp > 0 ? last.Substring(0, sp) + " “" + Clip(last.Substring(sp + 1), 60) + "”" : last); }
        if (!string.IsNullOrWhiteSpace(remote)) sb.Append(" · origin ").Append(Regex.Replace(remote, @"^(?:https?://|git@|ssh://git@)|\.git$|^[^/@:]*@", "").Replace(':', '/'));
        int n = string.IsNullOrWhiteSpace(dirty) ? 0 : dirty.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        sb.Append(n == 0 ? " · clean" : $" · {n} uncommitted");
        return sb.ToString();
    }

    static string GitOut(string dir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("core.quotepath=off");
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var p = Process.Start(psi);
            var o = p.StandardOutput.ReadToEndAsync(); p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(2500)) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 ? o.Result.Trim() : null;
        }
        catch { return null; }
    }

    static string Ago(TimeSpan t) => t.TotalMinutes < 60 ? $"{Math.Max(1, (int)t.TotalMinutes)}m ago" : t.TotalHours < 48 ? $"{(int)t.TotalHours}h ago" : $"{(int)t.TotalDays}d ago";

    // ---------------- free spoken commands (answered from memory, no Codex call)

    // "where did we leave off with my-app": what the memory says, plus the live git line.
    static string Briefing(Project p)
    {
        EnsureSummary(p);
        var sb = new StringBuilder("**").Append(p.Name).Append("**");
        if (!string.IsNullOrWhiteSpace(p.Summary)) sb.Append(" — ").Append(p.Summary);
        sb.Append('\n');
        if (!string.IsNullOrWhiteSpace(p.State)) sb.Append("Where it stands: ").Append(p.State).Append(p.StateAt != default ? " (" + Ago(DateTime.Now - p.StateAt) + ")" : "").Append('\n');
        if (!string.IsNullOrWhiteSpace(p.Next)) sb.Append("Next: ").Append(p.Next).Append('\n');
        if (p.Facts.Count > 0) sb.Append(string.Join("; ", p.Facts.Select(kv => kv.Key + " = " + kv.Value))).Append('\n');
        if (p.Dir != null && Directory.Exists(p.Dir)) { string g = GitLine(p.Dir); if (g != null) sb.Append(g).Append('\n'); }
        foreach (var h in p.History.TakeLast(3)) sb.Append("• ").Append(Clip(h, 140)).Append('\n');
        return sb.ToString().TrimEnd();
    }

    public static string TryLocal(string l)
    {
        Match m;
        if (Regex.IsMatch(l, @"^(?:what|which) projects?(?: are| do) (?:we|you|i)(?: (?:know|track\w*|remember|working on|have))?$|^(?:list|show)(?: me)?(?: my| the)?(?: tracked)? projects$|^what(?:'s| is) the (?:active|current) project$|^what project (?:are we|am i) (?:on|working on)$", RegexOptions.IgnoreCase))
        {
            var st = Load();
            var items = st.Items.OrderByDescending(x => x.Updated).Take(8).ToList();
            if (items.Count == 0) return "✓ No projects tracked yet. Mention one and I'll start.";
            return string.Join("\n", items.Select(p => $"• {p.Name}{(p.Name == st.Active && DateTime.Now - st.ActiveAt < FollowWindow ? "  (active)" : "")}"
                + (string.IsNullOrWhiteSpace(p.State) ? $" · {p.Notes.Count} note{(p.Notes.Count == 1 ? "" : "s")}" : " — " + Clip(p.State, 80))));
        }
        if ((m = Regex.Match(l, @"^(?:where (?:did|have) we (?:leave|left|get|got|stop|stopped)(?: off)?(?: (?:on|with|in))?|catch me up on|brief me on|remind me (?:about|where we (?:are|were) (?:on|with))|what(?:'s| is) the (?:state|progress) (?:of|on|with)|how far (?:are we|did we get) (?:on|with)|what(?:'s| is) (?:left|next) (?:on|for|with)) ?(?:the )?(.*)$", RegexOptions.IgnoreCase)).Success)
        {
            var s = Settings.Load();
            string what = m.Groups[1].Value.Trim().TrimEnd('?', '.', '!');
            Project p = null;
            if (what.Length == 0) { var st = Load(); if (st.Active != null && DateTime.Now - st.ActiveAt < FollowWindow) p = Get(st.Active, null, false); }
            else { var hit = FindMention(what, s); if (hit != null) p = Get(hit.Value.Name, hit.Value.Dir, true); }
            if (p == null || (string.IsNullOrWhiteSpace(p.State) && string.IsNullOrWhiteSpace(p.Next) && p.History.Count == 0)) return null;   // nothing remembered: Codex can look
            if (Luna.DryRun) return "DRY RUN: briefing for " + p.Name;
            SetActive(p.Name); Save();
            return Briefing(p);
        }
        if ((m = Regex.Match(l, @"^(?:(?:we(?:'re| are)|i(?:'m| am)) (?:now |back |currently )?(?:working|back) on|(?:let's|lets) (?:work|get back) on|switch(?: the)?(?: active)? project to|switch to project|back to project) (.+)$", RegexOptions.IgnoreCase)).Success)
        {
            var hit = FindMention(m.Groups[1].Value, Settings.Load());
            if (hit == null) return null;   // not one of the projects: let Codex handle it
            if (Luna.DryRun) return "DRY RUN: active project " + hit.Value.Name;
            Get(hit.Value.Name, hit.Value.Dir, true); SetActive(hit.Value.Name); Save();
            return "✓ Project · " + hit.Value.Name;
        }
        if ((m = Regex.Match(l, @"^(?:forget|clear|reset|delete)(?: the)?(?: notes (?:for|on|about)| project(?: notes)?| memory (?:for|of|about)) (.+)$", RegexOptions.IgnoreCase)).Success)
        {
            var hit = FindMention(m.Groups[1].Value, Settings.Load());
            if (hit == null) return null;
            if (Luna.DryRun) return "DRY RUN: forget project " + hit.Value.Name;
            lock (gate) { var st = Load(); st.Items.RemoveAll(x => Norm(x.Name) == Norm(hit.Value.Name)); if (st.Active != null && Norm(st.Active) == Norm(hit.Value.Name)) st.Active = null; }
            Save();
            return "✓ Forgot what I knew about " + hit.Value.Name;
        }
        return null;
    }
}
