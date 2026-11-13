using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace VowenLite;

// More free commands (see Commands.cs): browser/editor keys, Windows shortcuts, info answers, calculator, brightness.
static partial class Commands
{
    const ushort Ctrl = 0x11, Shift = 0x10, Alt = 0x12, Win = 0x5B;

    static readonly (string Pattern, string Desc, ushort[] Keys)[] KeyMap =
    {
        (@"^close(?: this| the| current)? (?:window|app|program)$", "close window (Alt+F4)", new ushort[] { Alt, 0x73 }),
        (@"^close(?: this| the| current)? tab$", "close tab (Ctrl+W)", new ushort[] { Ctrl, 0x57 }),
        (@"^(?:open |make )?(?:a )?new tab$", "new tab (Ctrl+T)", new ushort[] { Ctrl, 0x54 }),
        (@"^(?:re-?open|restore|undo close)(?: the)?(?: last| closed)? tab$", "reopen tab (Ctrl+Shift+T)", new ushort[] { Ctrl, Shift, 0x54 }),
        (@"^(?:next|right) tab$", "next tab", new ushort[] { Ctrl, 0x09 }),
        (@"^(?:previous|last|left) tab$", "previous tab", new ushort[] { Ctrl, Shift, 0x09 }),
        (@"^(?:refresh|reload)(?: this| the)?(?: page| tab| site)?$", "refresh (F5)", new ushort[] { 0x74 }),
        (@"^(?:go|navigate) back$", "back (Alt+Left)", new ushort[] { Alt, 0x25 }),
        (@"^(?:go|navigate) forward$", "forward (Alt+Right)", new ushort[] { Alt, 0x27 }),
        (@"^copy(?: this| that| it| selection)?$", "copy", new ushort[] { Ctrl, 0x43 }),
        (@"^cut(?: this| that| it)?$", "cut", new ushort[] { Ctrl, 0x58 }),
        (@"^paste(?: it| this| that| here)?$", "paste", new ushort[] { Ctrl, 0x56 }),
        (@"^select all$", "select all", new ushort[] { Ctrl, 0x41 }),
        (@"^undo(?: that| it)?$", "undo", new ushort[] { Ctrl, 0x5A }),
        (@"^redo(?: that| it)?$", "redo", new ushort[] { Ctrl, 0x59 }),
        (@"^save(?: this| it| the file| file)?$", "save (Ctrl+S)", new ushort[] { Ctrl, 0x53 }),
        (@"^(?:full ?screen|exit full ?screen|toggle full ?screen)$", "fullscreen (F11)", new ushort[] { 0x7A }),
        (@"^zoom in$", "zoom in", new ushort[] { Ctrl, 0xBB }),
        (@"^zoom out$", "zoom out", new ushort[] { Ctrl, 0xBD }),
        (@"^(?:reset zoom|actual size|normal zoom)$", "reset zoom", new ushort[] { Ctrl, 0x30 }),
        (@"^scroll down$", "page down", new ushort[] { 0x22 }),
        (@"^scroll up$", "page up", new ushort[] { 0x21 }),
        (@"^(?:scroll to (?:the )?top|go to (?:the )?top|top of (?:the )?page)$", "top (Ctrl+Home)", new ushort[] { Ctrl, 0x24 }),
        (@"^(?:scroll to (?:the )?bottom|go to (?:the )?bottom|bottom of (?:the )?page)$", "bottom (Ctrl+End)", new ushort[] { Ctrl, 0x23 }),
        (@"^new window$", "new window (Ctrl+N)", new ushort[] { Ctrl, 0x4E }),
        (@"^(?:select|focus|click)(?: on)?(?: the)? (?:address bar|url bar|search bar)$", "address bar (Ctrl+L)", new ushort[] { Ctrl, 0x4C }),
        (@"^(?:press |hit )?enter$", "enter", new ushort[] { 0x0D }),
        (@"^(?:press |hit )?escape$", "escape", new ushort[] { 0x1B }),
        (@"^(?:clipboard history|show clipboard|paste history)$", "clipboard history (Win+V)", new ushort[] { Win, 0x56 }),
        (@"^(?:emoji|emoji picker|open emoji(?: picker)?)$", "emoji picker (Win+.)", new ushort[] { Win, 0xBE }),
        (@"^(?:voice typing|windows dictation)$", "voice typing (Win+H)", new ushort[] { Win, 0x48 }),
        (@"^(?:task view|show all windows|window overview|overview)$", "task view (Win+Tab)", new ushort[] { Win, 0x09 }),
        (@"^(?:show )?(?:notifications|notification center)$", "notifications (Win+N)", new ushort[] { Win, 0x4E }),
        (@"^(?:show |open )?quick settings$", "quick settings (Win+A)", new ushort[] { Win, 0x41 }),
        (@"^(?:show |open )?widgets$", "widgets (Win+W)", new ushort[] { Win, 0x57 }),
        (@"^(?:open |show )?(?:the )?start(?: menu)?$", "start menu", new ushort[] { Win }),
        (@"^(?:windows search|search windows|search my pc)$", "search (Win+S)", new ushort[] { Win, 0x53 }),
        (@"^new (?:virtual )?desktop$", "new desktop", new ushort[] { Ctrl, Win, 0x44 }),
        (@"^(?:next|right) desktop$", "next desktop", new ushort[] { Ctrl, Win, 0x27 }),
        (@"^(?:previous|left) desktop$", "previous desktop", new ushort[] { Ctrl, Win, 0x25 }),
        (@"^(?:move|send)(?: this| the)?(?: window)? to (?:the )?(?:next|other) (?:monitor|screen|display)$", "move to other monitor", new ushort[] { Win, Shift, 0x27 }),
        (@"^(?:restore|un-?maximi[sz]e)(?: this| the)?(?: window)?$", "restore window", new ushort[] { Win, 0x28 }),
    };

    static readonly Dictionary<string, string> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["settings"] = "ms-settings:", ["windows settings"] = "ms-settings:", ["wifi"] = "ms-settings:network-wifi", ["wi-fi"] = "ms-settings:network-wifi", ["wi fi"] = "ms-settings:network-wifi",
        ["wireless"] = "ms-settings:network-wifi", ["network"] = "ms-settings:network", ["internet"] = "ms-settings:network", ["bluetooth"] = "ms-settings:bluetooth", ["display"] = "ms-settings:display",
        ["screen"] = "ms-settings:display", ["brightness"] = "ms-settings:display", ["night light"] = "ms-settings:nightlight", ["sound"] = "ms-settings:sound", ["audio"] = "ms-settings:sound",
        ["battery"] = "ms-settings:batterysaver", ["notifications"] = "ms-settings:notifications", ["windows update"] = "ms-settings:windowsupdate", ["updates"] = "ms-settings:windowsupdate",
        ["update"] = "ms-settings:windowsupdate", ["apps"] = "ms-settings:appsfeatures", ["installed apps"] = "ms-settings:appsfeatures", ["uninstall"] = "ms-settings:appsfeatures",
        ["storage"] = "ms-settings:storagesense", ["default apps"] = "ms-settings:defaultapps", ["personalization"] = "ms-settings:personalization", ["wallpaper"] = "ms-settings:personalization-background",
        ["background"] = "ms-settings:personalization-background", ["privacy"] = "ms-settings:privacy", ["mouse"] = "ms-settings:mousetouchpad", ["touchpad"] = "ms-settings:devices-touchpad",
        ["printers"] = "ms-settings:printers", ["vpn"] = "ms-settings:network-vpn", ["airplane mode"] = "ms-settings:network-airplanemode", ["startup apps"] = "ms-settings:startupapps",
        ["startup"] = "ms-settings:startupapps", ["date and time"] = "ms-settings:dateandtime", ["time settings"] = "ms-settings:dateandtime", ["keyboard"] = "ms-settings:typing", ["accounts"] = "ms-settings:yourinfo",
        ["task manager"] = "taskmgr.exe", ["control panel"] = "control.exe", ["device manager"] = "devmgmt.msc", ["recycle bin"] = "shell:RecycleBinFolder", ["trash"] = "shell:RecycleBinFolder",
        ["snipping tool"] = "ms-screenclip:", ["camera"] = "microsoft.windows.camera:", ["microsoft store"] = "ms-windows-store:", ["store"] = "ms-windows-store:", ["clock"] = "ms-clock:",
        ["alarms"] = "ms-clock:", ["file explorer"] = "explorer.exe", ["explorer"] = "explorer.exe", ["this pc"] = "shell:MyComputerFolder", ["calculator"] = "calc.exe", ["notepad"] = "notepad.exe",
        ["paint"] = "mspaint.exe", ["registry editor"] = null, ["disk management"] = "diskmgmt.msc", ["services"] = "services.msc", ["event viewer"] = "eventvwr.msc",
    };

    // "wifi", "bluetooth settings", "task manager"... -> opens it. Returns null if `target` isn't one of these.
    public static string SystemTarget(string target)
    {
        string t = Regex.Replace(target.Trim().ToLowerInvariant(), @"\s+(?:settings|page|menu|options|panel|app|window)$", "").Trim();
        t = Regex.Replace(t, @"^(?:the|my|windows)\s+", "").Trim();
        if (!Pages.TryGetValue(t, out var uri) || uri == null) return null;
        return Act("open " + uri, () => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }));
    }

    // Called from Commands.Try before timers.
    static string More(string l, Settings s)
    {
        Match m;
        foreach (var (pattern, desc, keys) in KeyMap)
            if (M(l, pattern, out _)) return Act(desc, () => Native.Combo(keys));

        // "wifi settings", "bluetooth settings"
        if (M(l, @"^(.+?) settings$", out m) && SystemTarget(m.Groups[1].Value + " settings") is { } sys) return sys;

        // sound / media variants
        if (M(l, @"^(?:max(?:imum)?|full) volume$|^volume (?:max|full|maximum)$|^(?:turn )?(?:the )?volume (?:all the way )?up to (?:max|full)$", out _))
            return Act("volume to 100%", () => Native.Tap(0xAF, 50), "✓ Volume maxed.");
        if (M(l, @"^(lower|raise|increase|decrease|reduce|boost|drop|bump)(?: up| down)? (?:the )?(?:volume|sound)(?: (?<n>a lot|a bit|a little|slightly))?$", out m))
        {
            bool up = m.Groups[1].Value.ToLowerInvariant() is "raise" or "increase" or "boost" or "bump";
            int n = m.Groups["n"].Value.ToLowerInvariant() switch { "a lot" => 10, "a bit" or "a little" or "slightly" => 2, _ => 5 };
            return Act($"volume {(up ? "up" : "down")} x{n}", () => Native.Tap(up ? (ushort)0xAF : (ushort)0xAE, n));
        }
        if (M(l, @"^(?:play |skip to |go to )(?:the )?next (?:song|track|one)$|^skip(?: it| this| song| track)?$", out _)) return Act("next track", () => Native.Tap(0xB0));
        if (M(l, @"^(?:play |go to )?(?:the )?(?:previous|last) (?:song|track|one)$|^go back a (?:song|track)$|^back a (?:song|track)$", out _)) return Act("previous track", () => Native.Tap(0xB1));
        if (M(l, @"^(?:stop|pause|play|resume)(?: playing| playback)?$|^(?:pause|resume) (?:the )?(?:music|song|video|podcast|spotify|youtube)$|^stop (?:the )?(?:music|song|video|podcast)$", out _))
            return Act("play/pause", () => Native.Tap(0xB3));

        // brightness (laptop screens)
        if (M(l, @"^(?:set )?(?:the )?(?:screen |display )?brightness (?:to |at )?(\d{1,3})(?: ?percent|%)?$", out m)) return Brightness(int.Parse(m.Groups[1].Value), absolute: true);
        if (M(l, @"^(?:turn |make )?(?:the )?(?:screen |display )?brightness (up|down)$|^(dim|brighten)(?: the)?(?: screen| display| it)?$|^(?:make (?:it|the screen) )?(brighter|dimmer|darker)$", out m))
        {
            string d = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).ToLowerInvariant();
            return Brightness(d is "up" or "brighten" or "brighter" ? 20 : -20, absolute: false);
        }

        // info (answers appear in the little window)
        if (M(l, @"^(?:what(?:'s| is)(?: the)? )?(?:current )?time(?: is it)?(?: right now| now| in here)?$|^what time is it(?: now| right now)?$|^tell me the time$", out _)) return "✓ " + DateTime.Now.ToString("h:mm tt", CultureInfo.InvariantCulture);
        if (M(l, @"^(?:what(?:'s| is)(?: the)? )?(?:today'?s )?date(?: today)?$|^what day (?:is it|of the week is it)(?: today)?$|^what'?s today$|^what is today'?s date$", out _)) return "✓ " + DateTime.Now.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture);
        if (M(l, @"^(?:what(?:'s| is)(?: my| the)? )?battery(?: level| percentage| percent| life| left| status)?(?: is it| do i have| left)?$|^how much battery(?: do i have| is left| left)?$|^am i charging$", out _)) return Battery();
        if (M(l, @"^how much (?:disk |storage |drive )?(?:space|storage)(?: do i have| is left| left| free| do i have left)?$|^(?:free )?(?:disk |storage |drive )space$", out _)) return Disk();
        if (M(l, @"^how long (?:has|have) (?:my |the )?(?:pc|computer|laptop) been (?:on|running|up)$|^(?:pc |computer )?uptime$", out _))
        {
            var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
            return $"✓ On for {(int)up.TotalHours} h {up.Minutes} min.";
        }
        if (M(l, @"^(?:flip|toss) (?:a )?coin$|^heads or tails$", out _)) return "✓ " + (Random.Shared.Next(2) == 0 ? "Heads." : "Tails.");
        if (M(l, @"^roll (?:a |an |the )?(?:die|dice|d(?<d>\d+)|(?<d2>\d+)[- ]sided die)$", out m))
        {
            int sides = m.Groups["d"].Success ? int.Parse(m.Groups["d"].Value) : m.Groups["d2"].Success ? int.Parse(m.Groups["d2"].Value) : 6;
            return $"✓ {Random.Shared.Next(1, Math.Max(2, sides) + 1)}";
        }
        if (M(l, @"^(?:pick|choose|give me|say)(?: a)?(?: random)? number(?: between (\d+) and (\d+))?$", out m))
        {
            int lo = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 1, hi = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 10;
            if (hi < lo) (lo, hi) = (hi, lo);
            return $"✓ {Random.Shared.Next(lo, hi + 1)}";
        }

        // saved widgets
        if (M(l, @"^(?:open|show) (?:my |the )?(?:saved )?widgets(?: folder)?$", out _))
            return Act("explorer at the Widgets folder", () => Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Luna.WidgetsDir + "\"") { UseShellExecute = false }));
        if (M(l, @"^(?:open|show|reopen|bring back|pull up) (?:the |my )?(?:last|latest|previous|recent) (?:widget|app|tool|interface|thing you made|one you made)$", out _))
        {
            var last = new DirectoryInfo(Luna.WidgetsDir).GetFiles("*.html").OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            if (last == null) return "✓ No widgets saved yet";
            string title = Regex.Replace(Path.GetFileNameWithoutExtension(last.Name), @"^\d{8}-\d{6}-", "").Replace('-', ' ');
            return Act("reopen widget " + last.Name, () => Luna.OpenWidget(last.FullName, title));
        }

        // days until a date
        if (M(l, @"^how (?:many days|long)(?: is it| are there)? (?:until|till|to|before) (?:the )?(.+)$|^(?:days|how long) (?:until|till|to) (?:the )?(.+)$", out m))
        {
            string what = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim().TrimEnd('?');
            DateTime today = DateTime.Today;
            DateTime Next(int mo, int day) { var d = new DateTime(today.Year, mo, day); return d < today ? d.AddYears(1) : d; }
            DateTime? target = Regex.Replace(what.ToLowerInvariant(), @"['’]s?\b|\bday$", "").Trim() switch
            {
                "christmas" => Next(12, 25), "new year" or "new years" or "new year" => Next(1, 1), "halloween" => Next(10, 31),
                "valentine" or "valentines" or "valentine s" => Next(2, 14), "fourth of july" or "july 4th" or "independence" => Next(7, 4), "new years eve" or "new year eve" => Next(12, 31),
                _ => null,
            };
            if (target == null && DateTime.TryParse(what, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                target = parsed < today && !Regex.IsMatch(what, @"\d{4}") ? parsed.AddYears(1) : parsed;
            if (target != null)
            {
                int days = (target.Value - today).Days;
                return $"✓ {days} day{(days == 1 ? "" : "s")} until {what}";
            }
        }

        // calculator
        string calc = Calc(WordsToDigits(l));
        if (calc != null) return calc;

        // type a snippet (or a short literal) into the focused window
        if (M(l, @"^type(?: out| in)? (.+)$", out m))
        {
            string what = m.Groups[1].Value.Trim();
            string key = Regex.Replace(what.ToLowerInvariant(), @"[^a-z0-9 ]", "").Trim();
            string bare = Regex.Replace(key, @"^my\s+", "");
            foreach (var kv in s.Snippets)
                if (Regex.Replace(kv.Key.ToLowerInvariant(), @"[^a-z0-9 ]", "").Trim() is { } sk && (sk == key || sk == bare || sk == "my " + bare))
                    return Act("type snippet \"" + kv.Key + "\"", () => Native.TypeLines(kv.Value));
            if (what.Split(' ').Length <= 10) return Act("type \"" + what + "\"", () => Native.TypeLines(what));
        }
        return null;
    }

    // ---------------- calculator

    static string Calc(string l)
    {
        const string N = @"(-?\d+(?:\.\d+)?)";
        Match m;
        string expr = Regex.Replace(l, @"^(?:what(?:'s| is)|calculate|calc|compute|how much is|whats|work out)\s+", "", RegexOptions.IgnoreCase).Trim();
        double v; string shown;
        if ((m = Regex.Match(expr, @"^" + N + @"\s*(?:percent|%)\s*of\s*" + N + "$", RegexOptions.IgnoreCase)).Success)
        { double a = D(m.Groups[1]), b = D(m.Groups[2]); v = a / 100 * b; shown = $"{Fmt(a)}% of {Fmt(b)}"; }
        else if ((m = Regex.Match(expr, @"^" + N + @"\s*(plus|\+|minus|-|times|x|\*|multiplied by|divided by|over|/|to the power of|\^)\s*" + N + "$", RegexOptions.IgnoreCase)).Success)
        {
            double a = D(m.Groups[1]), b = D(m.Groups[3]);
            string op = m.Groups[2].Value.ToLowerInvariant();
            switch (op)
            {
                case "plus" or "+": v = a + b; break;
                case "minus" or "-": v = a - b; break;
                case "times" or "x" or "*" or "multiplied by": v = a * b; break;
                case "divided by" or "over" or "/": if (b == 0) return "You can't divide by zero."; v = a / b; break;
                default: v = Math.Pow(a, b); break;
            }
            shown = $"{Fmt(a)} {op} {Fmt(b)}";
        }
        else if ((m = Regex.Match(expr, @"^(?:the )?square root of " + N + "$", RegexOptions.IgnoreCase)).Success)
        { double a = D(m.Groups[1]); if (a < 0) return "No real square root."; v = Math.Sqrt(a); shown = "√" + Fmt(a); }
        else if ((m = Regex.Match(expr, @"^" + N + @" (squared|cubed)$", RegexOptions.IgnoreCase)).Success)
        { double a = D(m.Groups[1]); v = m.Groups[2].Value.ToLowerInvariant() == "squared" ? a * a : a * a * a; shown = $"{Fmt(a)} {m.Groups[2].Value.ToLowerInvariant()}"; }
        else return null;
        return $"{shown} = {Fmt(v)}";
    }

    static double D(Group g) => double.Parse(g.Value, CultureInfo.InvariantCulture);
    static string Fmt(double v) => Math.Round(v, 6).ToString("0.######", CultureInfo.InvariantCulture);

    // ---------------- info

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS { public byte AC, Flag, Percent, SaverFlag; public uint LifeTime, FullLifeTime; }
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    internal static string Battery()
    {
        if (!GetSystemPowerStatus(out var st) || st.Percent > 100) return "✓ No battery found (this looks like a desktop).";
        string state = st.AC == 1 ? (st.Percent >= 100 ? "plugged in" : "charging") : "on battery";
        string left = st.AC == 0 && st.LifeTime != 0xFFFFFFFF ? $", about {st.LifeTime / 3600} h {st.LifeTime % 3600 / 60} min left" : "";
        return $"✓ {st.Percent}% ({state}{left})";
    }

    static string Disk()
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
            return $"✓ {d.AvailableFreeSpace / 1_073_741_824.0:0.#} GB free of {d.TotalSize / 1_073_741_824.0:0.#} GB on {d.Name.TrimEnd('\\')}";
        }
        catch (Exception ex) { return "I couldn't read the disk: " + ex.Message; }
    }

    // Laptop panels only (WMI). Reads the current level, applies a change, prints the new level.
    static string Brightness(int value, bool absolute)
    {
        string target = absolute ? Math.Clamp(value, 0, 100).ToString() : $"[Math]::Max(0,[Math]::Min(100,$b+({value})))";
        string script = "$b=(Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightness).CurrentBrightness; $n=" + target +
            "; Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods | Invoke-CimMethod -MethodName WmiSetBrightness -Arguments @{Timeout=1;Brightness=[byte]$n} | Out-Null; $n";
        return Act("brightness " + (absolute ? "to " + value + "%" : (value > 0 ? "up" : "down")), () =>
        {
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"" + script.Replace("\"", "\\\"") + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            Jobs.Add(p);
            string o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10000);
            if (p.ExitCode != 0 || !int.TryParse(o, out int n)) throw new Exception("brightness can only be changed on a laptop screen");
            Luna.Show("Brightness", "✓ Brightness " + n + "%.");
        }, "");
    }
}
