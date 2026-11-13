using System.Text;
using System.Text.RegularExpressions;

namespace VowenLite;

// Everything that helps whisper with words it is weak at:
//  * Prompt(): the --prompt glossary (names, projects, tech, slang), most important last because whisper
//    keeps the END of an over-long prompt.
//  * Fix(): cleanup after transcription: fixes mis-hearings (Settings.Replacements), drops "um/uh" and
//    silence hallucinations.
static class Vocab
{
    const int PromptChars = 640;   // ~200 tokens; whisper's prompt window is ~224

    // Least important first.
    static readonly string[] Slang = { "lowkey", "highkey", "no cap", "ngl", "fr", "bruh", "vibe", "sus", "cringe", "based", "gonna", "wanna", "kinda", "lol", "lmao", "IRL", "GG", "meme" };
    static readonly string[] Apps = { "YouTube", "TikTok", "Reddit", "Discord", "Twitch", "Spotify", "Steam", "Minecraft", "Roblox", "Wi-Fi", "Bluetooth" };
    static readonly string[] School = { "AP CSP", "AP Physics", "AP Gov", "FRQ", "MCQ", "concept check" };
    static readonly string[] Tech = {
        "GitHub", "npm", "Node.js", "TypeScript", "JavaScript", "Python", "Rust", "C#", ".NET", "WinForms", "PowerShell", "WSL", "VS Code",
        "Docker", "Linux", "JSON", "regex", "API", "LLM", "GPU", "CUDA", "FPGA", "Vivado", "Verilog", "SystemVerilog", "whisper.cpp",
    };
    static readonly string[] AI = { "ChatGPT", "OpenAI", "Anthropic", "Claude", "Claude Code", "Codex", "Cursor" };

    static string[] projects = Array.Empty<string>();
    static DateTime projectsAt = DateTime.MinValue;

    // Folder names under Settings.TerminalRoots (your projects), cached for a few minutes.
    public static string[] ProjectNames(Settings s)
    {
        if (DateTime.UtcNow - projectsAt < TimeSpan.FromMinutes(5)) return projects;
        var names = new List<string>();
        // Only the first root is your projects folder; Documents/Desktop/Downloads hold too much unrelated stuff.
        string root = s.TerminalRoots.FirstOrDefault();
        try
        {
            if (root != null && Directory.Exists(root))
                names.AddRange(Directory.EnumerateDirectories(root).Select(Path.GetFileName)
                    .Where(n => Regex.IsMatch(n, @"^[A-Za-z][A-Za-z\- ]{3,23}$") && n != n.ToUpperInvariant()));
        }
        catch { }
        projects = names.Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray();
        projectsAt = DateTime.UtcNow;
        return projects;
    }

    // maxChars: meetings can afford the full glossary; dictation uses a short one because every prompt
    // token adds latency (~+0.4 s at 250 chars, +1.3 s at 640 for 8 s of speech).
    public static string Prompt(Settings s, int maxChars = PromptChars)
    {
        var parts = new List<string>();
        parts.AddRange(Slang); parts.AddRange(Apps); parts.AddRange(School); parts.AddRange(Tech); parts.AddRange(AI);
        parts.AddRange(ProjectNames(s));
        parts.AddRange(Split(s.Vocabulary));   // learned from meeting corrections
        parts.AddRange(Lexicon.TopNames(s, 5).Reverse());           // most-mentioned names in your meetings
        parts.AddRange(Lexicon.Learned().Terms.TakeLast(15));   // names Codex fixed in earlier meetings (learned.json)
        parts.AddRange(Split(s.Names));        // you and your people: last = kept first
        parts.AddRange(Lexicon.ContextTerms()); // words from the window you are typing into (doc/repo/chat names)
        parts.Add("Planner");                  // the bare "Planner, ..." trigger (whisper hears it as planter/Blane/Dragon otherwise)
        parts.Add("Sol"); parts.Add("Terra");  // model-pick names ("Terra, ..." was heard as "Cara")
        parts.Add("Luna");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<string>();
        for (int i = parts.Count - 1; i >= 0; i--) if (seen.Add(parts[i])) unique.Add(parts[i]);   // keep the LAST occurrence
        unique.Reverse();
        while (unique.Count > 1 && string.Join(", ", unique).Length > maxChars) unique.RemoveAt(0);
        return string.Join(", ", unique);
    }

    // Proper-noun-like terms the app knows about (for Lexicon.Snap): apps, tech, AI names, your project folders.
    public static IEnumerable<string> KnownTerms(Settings s) =>
        Apps.Concat(School).Concat(Tech).Concat(AI).Concat(ProjectNames(s)).Concat(new[] { "Spicetify", "Spotify", "Planner", "Terra" });

    static IEnumerable<string> Split(string csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static Dictionary<string, string> DefaultReplacements() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["cloud code"] = "Claude Code", ["clawed code"] = "Claude Code", ["claude's code"] = "Claude Code",
        ["code x"] = "Codex", ["chat gpt"] = "ChatGPT", ["chat g p t"] = "ChatGPT", ["open ai"] = "OpenAI",
        ["get hub"] = "GitHub", ["git hub"] = "GitHub", ["power shell"] = "PowerShell", ["you tube"] = "YouTube", ["tik tok"] = "TikTok",
        ["v s code"] = "VS Code", ["vs code"] = "VS Code", ["a p i"] = "API", ["l l m"] = "LLM", ["f p g a"] = "FPGA",
        ["very log"] = "Verilog", ["veri log"] = "Verilog", ["verilock"] = "Verilog", ["system very log"] = "SystemVerilog",
    };

    static readonly Regex Filler = new(@"\b(?:um+|uh+|uhm+|erm+|hmm+|mhm+)\b[,.]?\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly HashSet<string> Hallucinations = new(StringComparer.OrdinalIgnoreCase)
        { "you", "thanks for watching", "thank you for watching", "please subscribe", "bye", "bye bye", "." };

    static Regex[] compiled;
    static string compiledKey;

    // Applies replacements (whole words, any case) to `text`.
    public static string Replace(string text, Settings s)
    {
        // Your replacements win; fixes learned from Codex corrections (learned.json) fill in the rest.
        var all = new Dictionary<string, string>(s.Replacements, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Lexicon.Learned().Replacements) all.TryAdd(kv.Key, kv.Value);
        string key = string.Join("|", all.Select(kv => kv.Key + ">" + kv.Value));
        if (compiledKey != key)
        {
            compiled = all.OrderByDescending(kv => kv.Key.Length)
                .Select(kv => new Regex(@"\b" + Regex.Escape(kv.Key).Replace("\\ ", @"[\s-]+") + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();
            compiledKey = key;
        }
        var ordered = all.OrderByDescending(kv => kv.Key.Length).ToArray();
        for (int i = 0; i < compiled.Length; i++) text = compiled[i].Replace(text, ordered[i].Value);
        return text;
    }

    // Dictation clean-up: fillers, silence hallucinations, then the replacements.
    public static string Fix(string text, Settings s)
    {
        text = text.Trim();
        if (Hallucinations.Contains(text.Trim(' ', '.', '!', '?', ','))) return "";
        bool cap = text.Length > 0 && char.IsUpper(text[0]);
        text = Filler.Replace(text, "").Trim();
        if (text.Length == 0) return "";
        if (cap && char.IsLower(text[0])) text = char.ToUpperInvariant(text[0]) + text.Substring(1);
        return Replace(text, s).Trim();
    }
}
