using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace VowenLite;

// Decides, without any model, what a spoken Luna request means. Anything it can handle is free and instant;
// only what it can't match goes on to Codex. Lenient on purpose: lead-ins ("can you please..."), many verbs
// for "open", apps / websites / settings pages / folders / running windows, searches on several sites.
static class Router
{
    // ---- normalising the sentence --------------------------------------------------------------

    static readonly Regex LeadIns = new(
        @"^(?:(?:hey|ok|okay|yo|um+|uh+|so|well|alright|all right|right|now|and|then|luna|lunar)[\s,]+|(?:can|could|would|will)\s+you\s+(?:please\s+)?|please\s+|kindly\s+|i\s+(?:want|need|would like|wanna|'d like)\s+(?:you\s+)?to\s+|i\s+(?:want|need)\s+to\s+|i\s+wanna\s+|let'?s\s+|go\s+ahead\s+and\s+|just\s+|i'?m\s+gonna\s+|you\s+can\s+|you\s+should\s+|try\s+to\s+)+",
        RegexOptions.IgnoreCase);
    static readonly Regex Trailers = new(@"[\s,]+(?:for me|please|now|right now|real quick|quickly|asap|thanks|thank you|if you can|okay|ok)$", RegexOptions.IgnoreCase);

    public static string Clean(string r)
    {
        r = r.Trim();
        for (int i = 0; i < 3; i++)
        {
            string before = r;
            r = LeadIns.Replace(r, "").Trim();
            r = Regex.Replace(r, @"[\s.!?,;:]+$", "");
            r = Trailers.Replace(r, "").Trim();
            if (r == before) break;
        }
        return Regex.Replace(r, @"\s{2,}", " ");
    }

    public static async Task<string> Local(string request, Settings s)
    {
        string r = Vocab.Replace(Clean(request), s);
        if (r.Length == 0) return null;

        string t = Commands.Try(r, s);
        if (t != null) return t;

        return await OpenAndSearch(r, s);
    }

    // ---- open / switch / search / play ---------------------------------------------------------

    const string OpenVerb = @"(?:open|launch|start|run|fire\s+up|boot\s+up|bring\s+up|pull\s+up|load|show(?:\s+me)?|go\s+to|take\s+me\s+to|switch\s+to|jump\s+to|navigate\s+to|visit|head\s+to|get\s+(?:me\s+)?(?:to\s+)?|i\s+want\s+to\s+see)";
    static readonly Regex OpenCmd = new(@"^" + OpenVerb + @"(?:\s+up)?\s+(?:the\s+|my\s+|a\s+)?(?:app\s+|website\s+|site\s+|page\s+|program\s+)?(.+?)(?:\s+(?:app|application|website|site|page|program|window))?$", RegexOptions.IgnoreCase);
    static readonly Regex FocusCmd = new(@"^(?:switch\s+to|go\s+to|jump\s+to|focus(?:\s+on)?|alt\s*tab\s+to|bring\s+up|back\s+to|show(?:\s+me)?)\s+(?:the\s+|my\s+)?(.+?)(?:\s+window)?$", RegexOptions.IgnoreCase);
    static readonly Regex LooksLikeUrl = new(@"^(?:https?://)?[a-z0-9-]+(?:\.[a-z0-9-]+)*\.(?:com|org|net|io|ai|dev|app|edu|gov|co|tv|gg|me|us|uk|xyz|info|so|sh|to|fm|ly)(?:/\S*)?$", RegexOptions.IgnoreCase);

    static readonly Dictionary<string, string> Engines = new(StringComparer.OrdinalIgnoreCase)
    {
        ["youtube"] = "https://www.youtube.com/results?search_query={0}", ["you tube"] = "https://www.youtube.com/results?search_query={0}",
        ["amazon"] = "https://www.amazon.com/s?k={0}", ["github"] = "https://github.com/search?q={0}", ["wikipedia"] = "https://en.wikipedia.org/w/index.php?search={0}",
        ["reddit"] = "https://www.reddit.com/search/?q={0}", ["maps"] = "https://www.google.com/maps/search/{0}", ["google maps"] = "https://www.google.com/maps/search/{0}",
        ["spotify"] = "spotify:search:{0}", ["bing"] = "https://www.bing.com/search?q={0}", ["duckduckgo"] = "https://duckduckgo.com/?q={0}",
        ["stack overflow"] = "https://stackoverflow.com/search?q={0}", ["google"] = "https://www.google.com/search?q={0}", ["images"] = "https://www.google.com/search?tbm=isch&q={0}",
        ["google images"] = "https://www.google.com/search?tbm=isch&q={0}", ["twitter"] = "https://x.com/search?q={0}", ["x"] = "https://x.com/search?q={0}",
        ["netflix"] = "https://www.netflix.com/search?q={0}", ["imdb"] = "https://www.imdb.com/find/?q={0}",
    };

    static async Task<string> OpenAndSearch(string r, Settings s)
    {
        Match m;

        // play <thing> [on spotify|youtube]
        if ((m = Regex.Match(r, @"^play\s+(?:some\s+)?(.+?)\s+on\s+(spotify|youtube|you tube)$", RegexOptions.IgnoreCase)).Success)
            return await Go(string.Format(Engines[m.Groups[2].Value], Uri.EscapeDataString(m.Groups[1].Value)));
        if ((m = Regex.Match(r, @"^play\s+(?:some\s+|me\s+)?(.+)$", RegexOptions.IgnoreCase)).Success && !Regex.IsMatch(m.Groups[1].Value, @"^(?:next|the next|previous|the previous|music|media|video|song|track|it)\b", RegexOptions.IgnoreCase))
            return await Go(string.Format(Engines["youtube"], Uri.EscapeDataString(m.Groups[1].Value)));

        // directions / weather / define
        if ((m = Regex.Match(r, @"^(?:(?:give me |get )?directions to|navigate to|route to|how do i get to|how to get to)\s+(.+)$", RegexOptions.IgnoreCase)).Success)
            return await Go("https://www.google.com/maps/dir/?api=1&destination=" + Uri.EscapeDataString(m.Groups[1].Value));
        if ((m = Regex.Match(r, @"^(?:what(?:'s| is) )?(?:the )?weather(?: (?:like|today|now|tomorrow|this week))?(?: in (.+))?$", RegexOptions.IgnoreCase)).Success)
            return await Go("https://www.google.com/search?q=" + Uri.EscapeDataString("weather " + m.Groups[1].Value));
        if ((m = Regex.Match(r, @"^(?:define|definition of|meaning of|what does (.+?) mean)\s*(.*)$", RegexOptions.IgnoreCase)).Success)
        {
            string word = m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value;
            if (word.Length > 0) return await Go("https://www.google.com/search?q=" + Uri.EscapeDataString("define " + word));
        }
        // search <q> [on <engine>]  /  search <engine> for <q>
        if ((m = Regex.Match(r, @"^(?:search|look\s+up|look|find|google|bing|search\s+up|search\s+for)(?:\s+up)?(?:\s+for)?\s+(.+?)\s+(?:on|in|at|using)\s+(?:the\s+)?(youtube|you tube|amazon|github|wikipedia|reddit|google maps|maps|spotify|bing|duckduckgo|stack overflow|google images|images|google|twitter|x|netflix|imdb)$", RegexOptions.IgnoreCase)).Success)
            return await Go(string.Format(Engines[m.Groups[2].Value], Uri.EscapeDataString(m.Groups[1].Value)));
        if ((m = Regex.Match(r, @"^(?:search|look\s+up|find)(?:\s+on|\s+in)?\s+(youtube|you tube|amazon|github|wikipedia|reddit|google maps|maps|spotify|stack overflow|google images|images|imdb)(?:\s+for)?\s+(.+)$", RegexOptions.IgnoreCase)).Success)
            return await Go(string.Format(Engines[m.Groups[1].Value], Uri.EscapeDataString(m.Groups[2].Value)));
        if ((m = Regex.Match(r, @"^(?:search(?:\s+the\s+web|\s+online|\s+google)?(?:\s+for)?|search\s+up|look\s+up|google|bing|look\s+(.+?)\s+up|find\s+out|find)\s*(.*)$", RegexOptions.IgnoreCase)).Success)
        {
            string q = m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value;
            // "find" alone is too vague (find my files...); only treat as web search with a verb that clearly means it.
            if (q.Length > 0 && !Regex.IsMatch(r, @"^find\b(?!\s+out)", RegexOptions.IgnoreCase))
                return await Go("https://www.google.com/search?q=" + Uri.EscapeDataString(q));
        }

        if ((m = Regex.Match(r, @"^(.+?)\s+near\s+me$", RegexOptions.IgnoreCase)).Success)
            return await Go("https://www.google.com/maps/search/" + Uri.EscapeDataString(m.Groups[1].Value + " near me"));

        // switch to a window that's already open
        if ((m = FocusCmd.Match(r)).Success)
        {
            string f = Focus(m.Groups[1].Value);
            if (f != null) return f;
        }

        // open <target>
        if (!(m = OpenCmd.Match(r)).Success) return null;
        string t = m.Groups[1].Value.Trim().TrimEnd('.');
        if (t.Split(' ').Length > 5) return null;                         // "open the file I downloaded yesterday..." needs a model

        string spoken = t.Replace(" dot ", ".").Replace(" slash ", "/");
        if (LooksLikeUrl.IsMatch(spoken.Replace(" ", "")))
        {
            spoken = spoken.Replace(" ", "");
            return await Go(spoken.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? spoken : "https://" + spoken);
        }

        var sys = Commands.SystemTarget(t);                                // settings pages, task manager, recycle bin...
        if (sys != null) return sys;

        if (Regex.IsMatch(t, @"^(downloads|documents|desktop|pictures|videos|music|home)$", RegexOptions.IgnoreCase))
            return Explorer(t, s);

        if (Aliases.TryGetValue(t, out var alias)) t = alias;
        var app = await Luna.FindApp(t);
        bool exactApp = app != null && Luna.Norm(app.Value.Name) == Luna.Norm(t);
        if (exactApp) return await Luna.Open(t) ?? "";

        if (s.Sites.TryGetValue(t, out var site) || s.Sites.TryGetValue(Regex.Replace(t, @"\s+", " "), out site)) return await Go(site);
        if (app != null) return await Luna.Open(t) ?? "";

        string dir = Folders.Resolve(t, s, out _);
        if (dir != null) return Explorer(t, s);

        // Programs that register themselves with Windows but have no Start-menu entry (Chrome, etc.).
        string exe = Regex.Replace(t.ToLowerInvariant(), @"\s+", "");
        if (exe.Length >= 2 && AppPathRegistered(exe))
        {
            if (Luna.DryRun) return Luna.Dry("open program " + exe);
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return "";
        }

        // One or two unknown words after "open": answer straight away instead of spending a Codex call.
        if (t.Split(' ').Length <= 2 && Regex.IsMatch(r, @"^(?:open|launch|start|run)\b", RegexOptions.IgnoreCase))
            return $"I couldn't find \"{t}\" on this PC. For a website, add it under Sites in settings.json.";
        return null;
    }

    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vs code"] = "Visual Studio Code", ["vscode"] = "Visual Studio Code", ["code"] = "Visual Studio Code", ["visual studio"] = "Visual Studio Code",
        ["edge"] = "Microsoft Edge", ["office"] = "Word", ["spreadsheet"] = "Excel", ["slides app"] = "PowerPoint", ["zoom"] = "Zoom Workplace", ["terminal app"] = "Terminal",
    };

    static bool AppPathRegistered(string exe)
    {
        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            try { using var k = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe + ".exe"); if (k != null) return true; } catch { }
        }
        return false;
    }

    static string Explorer(string phrase, Settings s)
    {
        string dir = Folders.Resolve(phrase, s, out string problem);
        if (dir == null) return problem;
        if (Luna.DryRun) return Luna.Dry("explorer at " + dir);
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = false });
        return "";
    }

    static Task<string> Go(string url)
    {
        if (Luna.DryRun) return Task.FromResult(Luna.Dry("open " + url));
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return Task.FromResult("");  }
        catch (Exception ex) { return Task.FromResult("I couldn't open that: " + ex.Message); }
    }

    // ---- bring an already-open window to the front ---------------------------------------------

    static string Focus(string name)
    {
        string q = Luna.Norm(name);
        if (q.Length < 3) return null;
        Process best = null;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                string pn = Luna.Norm(p.ProcessName), title = Luna.Norm(p.MainWindowTitle);
                if (pn == q || title == q) { best = p; break; }
                if (best == null && (pn.Contains(q) || title.Contains(q) || (pn.Length >= 4 && q.Contains(pn)))) best = p;
            }
            catch { }
        }
        if (best == null) return null;
        if (Luna.DryRun) return Luna.Dry($"focus window \"{best.MainWindowTitle}\" ({best.ProcessName})");
        IntPtr h = best.MainWindowHandle;
        if (IsIconic(h)) ShowWindow(h, 9);
        keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);   // lets SetForegroundWindow through
        SetForegroundWindow(h);
        return "";
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
