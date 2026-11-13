using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VowenLite;

// "Luna, ..." requests that are just PC controls. Plain rules, no model, no tokens.
// Try() returns null when the request isn't one of these (it then goes on to Codex), "" when done silently,
// or text to show: "✓ ..." is a short confirmation that closes itself, anything else stays until dismissed.
static partial class Commands
{
    const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_ALT = 0x12, VK_SHIFT = 0x10, VK_LWIN = 0x5B, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
    const ushort VK_MUTE = 0xAD, VK_VOLDOWN = 0xAE, VK_VOLUP = 0xAF, VK_NEXT = 0xB0, VK_PREV = 0xB1, VK_PLAYPAUSE = 0xB3;


    static string Act(string what, Action a, string confirm = "")
    {
        if (Luna.DryRun) return "DRY RUN: " + what;
        try { a(); return confirm; }
        catch (Exception ex) { return "I couldn't do that: " + ex.Message; }
    }

    static bool M(string text, string pattern, out Match m)
    {
        m = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success;
    }

    public static string Try(string request, Settings s)
    {
        string l = Regex.Replace(request.Trim().TrimEnd('.', '!', '?', ',').Trim(), @"^(?:please|hey|can you|could you|will you)\s+", "", RegexOptions.IgnoreCase).Trim();
        l = Regex.Replace(l, @"\s+please$", "", RegexOptions.IgnoreCase);
        Match m;

        // ---- the password vault for Luna's background browser (VaultForm.cs)
        if (M(l, @"^(?:(?:open|show|launch|bring up|pull up|manage|edit)(?: me)? )?(?:the |my )?(?:passwords?|password (?:vault|manager|list)|(?:saved )?logins?(?: vault)?|vault)$", out _))
            return Act("open the password vault (--vault)", () => Process.Start(new ProcessStartInfo(Environment.ProcessPath, "--vault") { UseShellExecute = false }));

        // ---- an alarm is ringing: "stop" / "dismiss" silences it (before "stop" can mean pause the music)
        if (Cards.IsRinging && (DismissRx.IsMatch(l) || StopTimersRx.IsMatch(Luna.Norm(l))))
            return Act("stop the alarm", () => Cards.StopRinging(), "✓ Alarm stopped");

        // ---- sound and media
        if (M(l, @"^(?:un)?mute(?: (?:the )?(?:sound|volume|audio|computer|pc|it))?$", out _)) return Act("mute toggle", () => Native.Tap(VK_MUTE), "✓ Mute toggled.");
        if (M(l, @"^(?:(?<p>(?:turn |make )?(?:the )?(?:volume|sound|it|music|this)) )?(?<d>up|down|louder|quieter|softer|lower|higher)(?: (?<n>a lot|a bit|a little|slightly))?$", out m)
            && (m.Groups["p"].Success || m.Groups["d"].Value.ToLowerInvariant() is "louder" or "quieter" or "softer"))
        {
            bool up = m.Groups["d"].Value.ToLowerInvariant() is "up" or "louder" or "higher";
            int n = m.Groups["n"].Value.ToLowerInvariant() switch { "a lot" => 10, "a bit" or "a little" or "slightly" => 2, _ => 5 };
            return Act($"volume {(up ? "up" : "down")} x{n}", () => Native.Tap(up ? VK_VOLUP : VK_VOLDOWN, n), "");
        }
        if (M(l, @"^(?:set )?(?:the )?volume (?:to |at )?(\d{1,3})(?: ?percent|%)?$", out m))
        {
            int pct = Math.Clamp(int.Parse(m.Groups[1].Value), 0, 100);
            return Act($"volume to {pct}%", () => { Native.Tap(VK_VOLDOWN, 50); if (pct > 0) Native.Tap(VK_VOLUP, pct / 2); }, $"✓ Volume {pct}%.");
        }
        if (M(l, @"^(?:pause|play|resume|stop|unpause)(?: the)?(?: music| media| video| song| track| playback| it)?$", out _)) return Act("play/pause", () => Native.Tap(VK_PLAYPAUSE));
        if (M(l, @"^(?:next|skip)(?: the)?(?: song| track)?$", out _)) return Act("next track", () => Native.Tap(VK_NEXT));
        if (M(l, @"^(?:previous|last|go back(?: to the)?)(?: the)?(?: song| track)$", out _)) return Act("previous track", () => Native.Tap(VK_PREV));

        // ---- screen and windows
        if (M(l, @"^(?:take (?:a )?)?(?:screenshot|screen shot|screen capture|snip)(?: this)?$", out _)) return Act("screenshot (Win+Shift+S)", () => Native.Combo(VK_LWIN, VK_SHIFT, 0x53));
        if (M(l, @"^lock(?: the| my)?(?: pc| computer| screen| laptop)?$", out _)) return Act("lock the PC", () => Process.Start("rundll32.exe", "user32.dll,LockWorkStation"));
        if (M(l, @"^(?:snap|move|put)(?: this| the)?(?: window)? (?:to the |to )?(left|right)$", out m)) { bool left = m.Groups[1].Value.ToLowerInvariant() == "left"; return Act("snap " + m.Groups[1].Value, () => Native.Combo(VK_LWIN, left ? VK_LEFT : VK_RIGHT)); }
        if (M(l, @"^maxim[iz]e(?: this| the)?(?: window)?$", out _)) return Act("maximize", () => Native.Combo(VK_LWIN, VK_UP));
        if (M(l, @"^minim[iz]e(?: this| the)?(?: window)?$", out _)) return Act("minimize", () => Native.Combo(VK_LWIN, VK_DOWN));
        if (M(l, @"^show(?: the)? desktop$", out _)) return Act("show desktop", () => Native.Combo(VK_LWIN, 0x44));
        if (M(l, @"^(?:switch|next) window$", out _)) return Act("alt+tab", () => Native.Combo(VK_ALT, VK_TAB));

        // ---- theme
        if (M(l, @"^(?:turn on |switch to |enable |go )?(dark|light)(?: mode| theme)$", out m)) return SetTheme(m.Groups[1].Value.ToLowerInvariant() == "light");
        if (M(l, @"^toggle (?:dark |light )?(?:mode|theme)$", out _)) return SetTheme(!IsLight());

        // ---- folders in Explorer / VS Code
        if (M(l, @"^(?:open|show|reveal)(?: me)? (?:the |my )?(.+?)(?: folder)? in (?:file )?(?:explorer|files|file manager)$", out m) ||
            M(l, @"^(?:open|show)(?: me)? (?:the |my )?(.+?) folder$", out m))
        {
            string dir = Folders.Resolve(m.Groups[1].Value, s, out string problem);
            if (dir == null) return M(l, @" folder$", out _) && !M(l, @"explorer|files|file manager", out _) ? null : problem;
            return Act("explorer at " + dir, () => Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = false }));
        }
        if (M(l, @"^(?:open|edit)(?: up)? (?:the |my )?(.+?)(?: folder| project| repo)? in (?:vs ?code|v s code|visual studio code|code)$", out m))
        {
            string dir = Folders.Resolve(m.Groups[1].Value, s, out string problem);
            if (dir == null) return problem;
            return Act("VS Code at " + dir, () => Process.Start(new ProcessStartInfo("cmd.exe", "/c code \"" + dir + "\"") { CreateNoWindow = true, UseShellExecute = false }));
        }

        // ---- timers and reminders
        string t = Cards.Try(l);                      // stopwatch and pinned notes
        if (t != null) return t;
        t = Planner.TryLocal(l);                      // tasks + calendar (synced with Google)
        if (t != null) return t;
        t = Assistant.TryLocal(l);                    // "what do you remember", "forget everything"
        if (t != null) return t;
        t = Brain.TryLocal(l, s);                     // "usage", "be quiet" / "talk to me", "humanize the clipboard"
        if (t != null) return t;
        t = Timers(WordsToDigits(l));
        if (t != null) return t;
        t = More(l, s);
        if (t != null) return t;
        return null;
    }

    // ---------------- theme

    static bool IsLight()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return (k?.GetValue("AppsUseLightTheme") as int? ?? 1) == 1;
    }

    static string SetTheme(bool light) => Act(light ? "light mode" : "dark mode", () =>
    {
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
        {
            k.SetValue("AppsUseLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
            k.SetValue("SystemUsesLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
        }
        SendMessageTimeout((IntPtr)0xFFFF, 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, "ImmersiveColorSet", 2, 1000, out _);
    }, light ? "✓ Light mode." : "✓ Dark mode.");

    // ---------------- timers / reminders (kept in memory: a restart of the background app clears them)

    static readonly Regex Unit = new(@"(\d+(?:\.\d+)?|an?|half an?)\s*(seconds?|secs?|minutes?|mins?|hours?|hrs?)", RegexOptions.IgnoreCase);

    sealed class Pending
    {
        public DateTime Due;
        public TimeSpan Total;
        public string Label;
        public TimeSpan? Repeat;      // recurring reminder: reschedules itself
        public bool Quiet;            // chime + pill instead of the alarm and a popup (focus sessions, recurring reminders)
        public string Title, Text;
        public Action Then;           // runs when it ends (pomodoro: start the break)
        public System.Threading.Timer T;
        public string Id = Guid.NewGuid().ToString("N");
    }
    static readonly List<Pending> pending = new();

    // For the overlay cards: what is running and how long is left.
    public static List<(TimeSpan Left, TimeSpan Total, string Label, bool Repeating, string Id)> Running()
    {
        lock (pending)
            return pending.Select(p => (TimeSpan.FromTicks(Math.Max(0, (p.Due - DateTime.Now).Ticks)), p.Total, p.Label, p.Repeat != null, p.Id)).OrderBy(x => x.Item1).ToList();
    }

    internal static void Schedule(TimeSpan span, string label, string title, string text, bool quiet = false, TimeSpan? repeat = null, Action then = null, string id = null)
    {
        var p = new Pending { Id = id ?? Guid.NewGuid().ToString("N"),  Due = DateTime.Now + span, Total = span, Label = label, Title = title, Text = text, Quiet = quiet, Repeat = repeat, Then = then };
        p.T = new System.Threading.Timer(_ => Fire(p), null, span, Timeout.InfiniteTimeSpan);
        lock (pending) pending.Add(p);
        Overlay.ChipRefresh();
    }

    static void Fire(Pending p)
    {
        lock (pending) pending.Remove(p);
        p.T.Dispose();
        if (p.Repeat != null)
        {
            Schedule(p.Repeat.Value, p.Label, p.Title, p.Text, p.Quiet, p.Repeat, p.Then);
            Overlay.Notice("⏰ " + (p.Label ?? p.Text).TrimEnd('.'), 4000);
            Feedback.Set();
        }
        else if (p.Quiet)
        {
            Overlay.Notice("⏰ " + p.Text.TrimEnd('.'), 4500);
            Feedback.Set();
        }
        else
        {
            Cards.StartRinging(p.Title, p.Text);   // rings (and shows a card) until you click it or say "stop"
        }
        p.Then?.Invoke();
        Overlay.ChipRefresh();
    }

    public static void CancelTimer(string id)
    {
        lock (pending)
        {
            var p = pending.FirstOrDefault(x => x.Id == id);
            if (p == null) return;
            p.T.Dispose();
            pending.Remove(p);
        }
        Assistant.Cancelled(id);   // a saved reminder must not come back after a restart
        Overlay.ChipRefresh();
    }

    // "stop timer", "cancel the timers", "kill the alarm" and the ways speech-to-text mangles them ("stocktimer").
    static readonly Regex StopTimersRx = new(
        @"^(?:stop|stock|stoop|stap|stopp|cancel|kill|end|clear|dismiss|silence|delete|remove|shutoff|turnoff|quit|abort|snooze)(?:the|my|all|that|this|every|ringing)*(?:timers?|alarms?|reminders?|pomodoros?|focus(?:session)?|ringing)$",
        RegexOptions.IgnoreCase);
    static readonly Regex DismissRx = new(@"^(?:stop|dismiss|silence|shut up|quiet|enough|okay stop|ok stop|got it|stop it|stop that|stop ringing|stop the alarm|stop the timer)$", RegexOptions.IgnoreCase);

    static string Timers(string l)
    {
        // Ringing takes priority: a bare "stop" silences the alarm instead of pausing the music.
        if (Cards.IsRinging && (DismissRx.IsMatch(l) || StopTimersRx.IsMatch(Luna.Norm(l))))
            return Act("stop the alarm", () => Cards.StopRinging(), "✓ Alarm stopped");
        if (M(l, @"^(?:cancel|stop|clear|delete|kill|end)(?: all| my| the)? (?:timers?|reminders?|pomodoro|focus(?: session)?)$", out _) || StopTimersRx.IsMatch(Luna.Norm(l)))
            return Act("cancel timers", () =>
            {
                lock (pending) { foreach (var p in pending) p.T.Dispose(); pending.Clear(); }
                Cards.StopRinging();
                Overlay.ChipRefresh();
            }, "✓ Timers cancelled");
        if (M(l, @"^(?:how long|time) (?:is )?(?:left|remaining)(?: on (?:my|the) timer)?$|^(?:what|which) timers?(?: are| do i have)?(?: running| set)?$|^(?:list|show) (?:my |the )?(?:timers?|reminders?)$", out _))
        {
            var run = Running();
            if (run.Count == 0) return "✓ No timers running";
            return "✓ " + string.Join("  ·  ", run.Take(3).Select(r => $"{Clock(r.Left)}{(r.Label != null ? " " + r.Label : "")}"));
        }

        Match m;
        // focus sessions: "start a pomodoro", "focus for 45 minutes" -> focus timer, then an automatic 5 minute break
        if (M(l, @"^(?:start |begin |do |run )?(?:a |an |the )?(?:(?<n>\d+)[- ]?(?:minute|min) )?(?:pomodoro|focus(?: session| timer| mode)|focus(?= for ))(?: for (?<d>.+))?$", out m) && !M(l, @"\bremind\b", out _))
        {
            var focus = m.Groups["d"].Success ? ParseDuration(m.Groups["d"].Value) : m.Groups["n"].Success ? TimeSpan.FromMinutes(int.Parse(m.Groups["n"].Value)) : TimeSpan.FromMinutes(25);
            if (focus == null || focus.Value.TotalMinutes < 1) focus = TimeSpan.FromMinutes(25);
            var brk = TimeSpan.FromMinutes(focus.Value.TotalMinutes >= 40 ? 10 : 5);
            string fp = Pretty(focus.Value), bp = Pretty(brk);
            return Act($"focus {fp} then break {bp}", () =>
                Schedule(focus.Value, "Focus", "Focus", $"Focus session done. Take a {bp} break.", quiet: true, then: () =>
                    Schedule(brk, "Break", "Break", "Break's over. Ready for another round?", quiet: true)),
                $"✓ Focus · {fp}, then a {bp} break");
        }

        // recurring: "remind me every 30 minutes to stand up"
        if (M(l, @"^remind me every (.+?) (?:to|that|about) (.+)$", out m) || M(l, @"^every (.+?) remind me (?:to|that|about) (.+)$", out m))
        {
            var every = ParseDuration(m.Groups[1].Value.StartsWith("hour") || m.Groups[1].Value.StartsWith("minute") ? "1 " + m.Groups[1].Value : m.Groups[1].Value);
            if (every == null || every.Value.TotalSeconds < 30) return "I didn't catch how often. Try \"remind me every 30 minutes to stretch\".";
            string what = m.Groups[2].Value.Trim();
            string text = char.ToUpperInvariant(what[0]) + what.Substring(1);
            string lab = text.Length > 28 ? text.Substring(0, 27) + "…" : text;
            return Act($"every {Pretty(every.Value)}: {text}", () => Schedule(every.Value, lab, "Reminder", text + ".", quiet: true, repeat: every.Value),
                $"✓ Every {Pretty(every.Value)} · {lab}");
        }

        string label = null, durationText = null;
        if (M(l, @"^remind me (?:in|after) (.+?) (?:to|that|about) (.+)$", out m)) { durationText = m.Groups[1].Value; label = m.Groups[2].Value; }
        else if (M(l, @"^remind me (?:to|that|about) (.+?) (?:in|after) (.+)$", out m)) { durationText = m.Groups[2].Value; label = m.Groups[1].Value; }
        else if (M(l, @"\btimer\b", out _) && !M(l, @"\bremind\b", out _)) { durationText = l; }
        if (durationText == null) return null;

        var span = ParseDuration(durationText);
        if (span == null || span.Value.TotalSeconds < 1) return label != null || M(l, @"\btimer\b", out _) ? "I didn't catch how long. Try \"timer 10 minutes\"." : null;
        string pretty = Pretty(span.Value);
        string title = label == null ? "Timer" : "Reminder";
        string body = label == null ? $"Your {pretty} timer is up." : label[0].ToString().ToUpperInvariant() + label.Substring(1) + ".";
        string shortLabel = label == null ? null : (label.Length > 28 ? label.Substring(0, 27) + "…" : label);
        return Act($"{title.ToLower()} in {pretty}: {body}", () =>
        {
            Schedule(span.Value, shortLabel, title, body);
            Feedback.Set();
        }, label == null ? $"✓ Timer set · {pretty}" : $"✓ Reminder set · {pretty}");
    }

    public static string Clock(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    static TimeSpan? ParseDuration(string text)
    {
        double secs = 0;
        if (Regex.IsMatch(text, @"half an? hour", RegexOptions.IgnoreCase)) secs += 1800;
        foreach (Match m in Unit.Matches(text))
        {
            string n = m.Groups[1].Value.ToLowerInvariant();
            if (n.StartsWith("half")) continue;
            double v = n is "a" or "an" ? 1 : double.Parse(n, System.Globalization.CultureInfo.InvariantCulture);
            string u = m.Groups[2].Value.ToLowerInvariant();
            secs += u.StartsWith("h") ? v * 3600 : u.StartsWith("m") ? v * 60 : v;
        }
        return secs > 0 ? TimeSpan.FromSeconds(secs) : null;
    }

    static string Pretty(TimeSpan t)
    {
        var parts = new List<string>();
        if ((int)t.TotalHours > 0) parts.Add($"{(int)t.TotalHours} hour{((int)t.TotalHours == 1 ? "" : "s")}");
        if (t.Minutes > 0) parts.Add($"{t.Minutes} minute{(t.Minutes == 1 ? "" : "s")}");
        if (t.Seconds > 0 && t.TotalHours < 1) parts.Add($"{t.Seconds} second{(t.Seconds == 1 ? "" : "s")}");
        return string.Join(" ", parts);
    }

    static readonly Dictionary<string, int> Ones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
        ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
    };
    static readonly Dictionary<string, int> Tens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    // "twenty five minutes" -> "25 minutes"
    internal static string WordsToDigits(string s)
    {
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var outp = new List<string>();
        for (int i = 0; i < words.Length; i++)
        {
            string w = words[i].Trim(',').Replace("-", " ");
            if (Tens.TryGetValue(w, out int t))
            {
                if (i + 1 < words.Length && Ones.TryGetValue(words[i + 1].Trim(','), out int o) && o is >= 1 and <= 9) { t += o; i++; }
                outp.Add(t.ToString());
            }
            else if (Ones.TryGetValue(w, out int one)) outp.Add(one.ToString());
            else outp.Add(words[i]);
        }
        return string.Join(" ", outp);
    }

    [DllImport("user32.dll")] static extern bool MessageBeep(uint type);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);
}
