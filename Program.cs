using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VowenLite;

// One exe, two modes. "--daemon" is a windowless hotkey listener (no WinForms loaded);
// anything else opens the app window.
static class Program
{
    public const string AppMutex = "Local\\VowenLite.App";
    public const string DaemonMutex = "Local\\VowenLite.Daemon";
    public const string QuitEvent = "Local\\VowenLite.Quit";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--watch")) return Watcher.Run();
        if (args.Contains("--daemon")) return Daemon.Run(args);
        if (args.Contains("--selftest")) return SelfTest.Run();
        if (args.Contains("--plantest")) return PlanTest.Run();
        if (args.Contains("--taptest")) return TapDetector.Test();
        if (args.Contains("--threadtest")) return Threads.Test();
        if (args.Length >= 2 && args[0] == "--planshot") return PlanShot.Run(args);
        if (args.Contains("--plan"))
        {
            // `--plan [today|week|month|day|tasks|upcoming]`; a second launch only brings the open window forward.
            int pi = Array.IndexOf(args, "--plan");
            string planView = pi + 1 < args.Length ? args[pi + 1] : "today";
            using var planMutex = new Mutex(true, "Local\\VowenLite.Plan", out bool firstPlan);
            if (!firstPlan)
            {
                try { File.WriteAllText(PlanForm.ViewFile, planView); using var sig = EventWaitHandle.OpenExisting("Local\\VowenLite.PlanShow"); sig.Set(); } catch { }
                return 0;
            }
            ApplicationConfiguration.Initialize();
            Application.Run(new PlanForm(planView));
            return 0;
        }
        if (args.Length >= 1 && args[0] == "--gauth")
        {
            // `--gauth [client_secret.json]`: import the Google OAuth client (optional) and sign in via the browser.
            try { if (args.Length > 1) GoogleSync.ImportClient(args[1]); } catch (Exception ex) { Console.WriteLine("Could not read " + args[1] + ": " + ex.Message); return 1; }
            Console.WriteLine(GoogleSync.SignInAsync().GetAwaiter().GetResult());
            return 0;
        }
        if (args.Length == 1 && args[0] == "--syncnow") { Console.WriteLine(GoogleSync.SyncNowAsync().GetAwaiter().GetResult()); return 0; }
        if (args.Contains("--overlaytest")) return SelfTest.ShowOverlay();
        if (args.Length == 3 && args[0] == "--keytest") { Console.WriteLine(Lexicon.Key(args[1]) + " / " + Lexicon.Key(args[2]) + " sounds=" + Lexicon.SoundsPublic(args[1], args[2])); return 0; }
        if (args.Length == 2 && args[0] == "--meettest") { Jobs.Init(); var ms = Settings.Load(); var md = Transcriber.RunAsync(args[1], ms, new Progress<string>(_ => { })).GetAwaiter().GetResult(); Console.WriteLine(File.ReadAllText(md)); return 0; }
        if (args.Length == 3 && args[0] == "--learntest") { Jobs.Init(); int n = Lexicon.Learn(File.ReadAllText(args[1]), File.ReadAllText(args[2])); var ld = Lexicon.Learned(); Console.WriteLine(n + " substitutions; terms: " + string.Join(", ", ld.Terms) + " | fixes: " + string.Join(", ", ld.Replacements.Select(kv => kv.Key + ">" + kv.Value)) + " | pending: " + string.Join(", ", ld.Pending.Select(kv => kv.Key + " x" + kv.Value))); return 0; }
        if (args.Length == 2 && args[0] == "--lextest") { var ls = Settings.Load(); var lws = args[1].Split(' ').Select(x => { var q = x.Split('~'); var w = Lexicon.FromText(q[0])[0]; if (q.Length > 1) w.P = double.Parse(q[1], System.Globalization.CultureInfo.InvariantCulture); return w; }).ToList(); int sn = Lexicon.Snap(lws, ls); int ch = Lexicon.Canonicalize(new List<List<Lexicon.W>> { lws }, ls); Console.WriteLine(Lexicon.Join(lws) + "   (snapped " + sn + ", unified " + ch + ")"); return 0; }
        if (args.Length == 2 && args[0] == "--namestest") { Console.WriteLine(string.Join(", ", Lexicon.MeetingNamesCounted(args[1]).Select(x => x.Name + " x" + x.Count))); return 0; }
        if (args.Length == 2 && args[0] == "--acctest") { Jobs.Init(); var acs = Settings.Load(); foreach (var f in Directory.GetFiles(args[1], "*.wav").OrderBy(x => x.Length).ThenBy(x => x)) { var sw = System.Diagnostics.Stopwatch.StartNew(); var tx = Dictation.Transcribe(f, acs); Console.WriteLine(Path.GetFileName(f) + " [" + sw.ElapsedMilliseconds + " ms]: " + tx); } return 0; }
        if (args.Length == 2 && args[0] == "--dicttest") { Jobs.Init(); var t = Dictation.Transcribe(args[1], Settings.Load()); Console.WriteLine(t); Thread.Sleep(1500); Native.TypeText(t + " "); return 0; }
        if (args.Length == 2 && args[0] == "--sumtest") { Jobs.Init(); Summariser.RunAsync(args[1], Settings.Load()).GetAwaiter().GetResult(); Console.WriteLine(File.ReadAllText(Path.Combine(args[1], "summary.md"))); return 0; }
        if (args.Length >= 2 && args[0] == "--widget") { ApplicationConfiguration.Initialize(); Application.Run(new WidgetForm(args[1], args.Length > 2 ? args[2] : "")); return 0; }
        if (args.Length == 1 && args[0] == "--ask") return AskBox.Run();
        if (args.Length == 2 && args[0] == "--answer") { ApplicationConfiguration.Initialize(); Application.Run(new AnswerForm(args[1])); return 0; }
        if (args.Length == 2 && args[0] == "--opentest") { Jobs.Init(); Console.WriteLine(Luna.TestLocal(args[1]).GetAwaiter().GetResult() ?? "(not a local command: would go to Codex)"); return 0; }
        if (args.Length == 2 && args[0] == "--vocabtest") { var vs = Settings.Load(); Console.WriteLine(Vocab.Prompt(vs).Length + " chars: " + Vocab.Prompt(vs)); Console.WriteLine(Vocab.Fix(args[1], vs)); return 0; }
        if (args.Length == 2 && args[0] == "--hooktest") { Jobs.Init(); Console.WriteLine(Luna.TestHooks(args[1].Replace("\\n", "\n"))); return 0; }
        if (args.Length == 2 && args[0] == "--hooklive") { Jobs.Init(); Console.WriteLine(Luna.TestHooks(args[1].Replace("\\n", "\n"), false)); return 0; }
        if (args.Length >= 2 && args[0] == "--shot")
        {
            int mx = Array.IndexOf(args, "--max");
            return Shot.Capture(args[1], args.Contains("--screen"), mx > 0 && mx + 1 < args.Length && int.TryParse(args[mx + 1], out int mv) ? mv : 1800, args.Contains("--meta"));
        }
        if (args.Length == 2 && args[0] == "--routetest") { Console.WriteLine(Luna.RouteTest(args[1])); return 0; }
        if (args.Length == 2 && args[0] == "--humantest") { Console.WriteLine(Humanize.Clean(args[1].Replace("\\n", "\n"), out int hc)); Console.WriteLine("(" + hc + " changes)"); return 0; }
        if (args.Length >= 2 && args[0] == "--pctest")
        {
            // `--pctest look` / `--pctest craft pdf --help`: runs one bridge command in this process (screen control on), prints the result.
            Jobs.Init(); Native.SetProcessDpiAwarenessContext((IntPtr)(-4));
            Console.WriteLine(PcBridge.Test(args.Skip(1).ToList()));
            return 0;
        }
        if (args.Length == 1 && args[0] == "--webhost") return WebHost.Run();
        if (args.Length == 1 && args[0] == "--vault") { ApplicationConfiguration.Initialize(); Application.Run(new VaultForm()); return 0; }
        // `--webtest open example.com` / `--webtest read`: one background-browser command (the host stays up 3 min for the next one). No Jobs.Init on purpose.
        if (args.Length >= 2 && args[0] == "--webtest") { Console.WriteLine(WebBridge.Test(args.Skip(1).ToList())); return 0; }
        if (args.Length >= 1 && args[0] == "--vaulttest") return args.Length > 1 && args[1] == "live" ? WebTest.Live() : Vault.SelfTest();
        if (args.Length == 2 && args[0] == "--projtest") { Jobs.Init(); var ctx = Projects.Context(args[1], Settings.Load()); Console.WriteLine(ctx); Console.WriteLine($"({ctx.Length} chars, ~{ctx.Length / 4} tokens)"); return 0; }
        if (args.Length == 2 && args[0] == "--cmdtest") { Console.WriteLine(Luna.Command(args[1])); return 0; }
        if (args.Length == 2 && args[0] == "--modetest") { Console.WriteLine(Luna.Mode(args[1])); return 0; }
        if (args.Length >= 2 && args[0] == "--parsetest")
        {
            bool isLuna = Luna.TryParse(args[1], out var pr);
            if (!isLuna) pr = args[1];                                   // the Luna key is held: the whole text is the request
            string sw = Luna.SwitchTarget(pr);
            if (sw != null) { Console.WriteLine("SWITCH default model -> " + sw); return 0; }
            string mdl = Luna.PickModel(ref pr, Settings.Load(), out string mname, args.Length > 2 && args[2] == "held");
            Console.WriteLine((isLuna ? "LUNA: " : "(typed normally; with the Luna key:) ") + pr + "   [model: " + mname + " " + (mdl ?? Settings.Load().LunaModel) + "]");
            return 0;
        }
        if (args.Contains("--timertest"))
        {
            // Sets two real timers (a 2 min reminder, a 90 s timer), shows the confirmation pill and the countdown chip for 8 s.
            Native.SetProcessDpiAwarenessContext((IntPtr)(-4));
            Overlay.Create();
            var ts = Settings.Load();
            Commands.Try("remind me in 2 minutes to call mom", ts);
            Commands.Try("timer for 90 seconds", ts);
            Commands.Try("start a stopwatch", ts);
            Commands.Try("pin a note buy milk and eggs", ts);
            Cards.BeginWork("make me an interactive tip calculator");
            Cards.StartRinging("Timer", "Your 1 minute timer is up.");
            Overlay.Notice("Timer set · 1 minute 30 seconds");
            long end = Environment.TickCount64 + 16000;
            while (Environment.TickCount64 < end) { while (Native.PeekMessage(out var tm, IntPtr.Zero, 0, 0, 1)) Native.DispatchMessage(ref tm); Thread.Sleep(15); }
            Commands.Try("clear notes", ts);   // don't leave the demo note pinned
            return 0;
        }
        if (args.Contains("--cardtest"))
        {
            // A working card with hover steps for 6 s, then a finished one that keeps its answer; runs 40 s.
            Native.SetProcessDpiAwarenessContext((IntPtr)(-4));
            Overlay.Create();
            int w1 = Cards.BeginWork("go to my downloads folder and follow the free jam instructions");
            foreach (var raw in new[] {
                """ "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" -Command "Get-ChildItem -LiteralPath 'C:\Users\me\Downloads' | Select Name" """,
                """ "powershell.exe" -Command "Get-Content -LiteralPath 'C:\Users\me\Downloads\MyAppinstructions.txt' -Raw" """,
                """ "powershell.exe" -Command "Expand-Archive -LiteralPath 'C:\Users\me\Downloads\MyApp-Friend.zip' -DestinationPath $env:TEMP\fj -Force" """,
                """ "powershell.exe" -Command "Copy-Item $env:TEMP\fj\myapp.js $env:APPDATA\spicetify\Extensions\myapp.js" """,
                """ "powershell.exe" -Command "(Get-Content config-xpui.ini) -replace 'extensions = .*','extensions = myapp.js' | Set-Content config-xpui.ini" """,
                """ "powershell.exe" -Command "spicetify apply" """,
                """ "powershell.exe" -Command "Get-Process Spotify | Select Id" """,
                """ "powershell.exe" -Command "Something-Odd -Foo 3" """ })
                Cards.Progress(w1, Luna.Command(raw));
            long t0 = Environment.TickCount64;
            bool ended = false;
            while (Environment.TickCount64 < t0 + 40000)
            {
                if (!ended && Environment.TickCount64 > t0 + 8000) { ended = true; Cards.EndWork(w1, "Answer ready", false, "Installed myapp.js and updated config-xpui.ini.\n\nRun `spicetify apply` and restart Spotify to see the MyApp button."); }
                while (Native.PeekMessage(out var tm, IntPtr.Zero, 0, 0, 1)) Native.DispatchMessage(ref tm);
                Thread.Sleep(15);
            }
            return 0;
        }
        if (args.Length == 2 && args[0] == "--luna") { Jobs.Init(); Luna.RunBlocking(args[1], Settings.Load()); return 0; }
        if (args.Length == 3 && args[0] == "--rectest") return SelfTest.Record(int.Parse(args[1]), args[2]);

        using var mutex = new Mutex(true, AppMutex, out bool first);
        if (!first) { Native.FocusOther(); return 0; }

        Jobs.Init(); // whisper children die with us, even on a hard kill
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}

static class Daemon
{
    const int HotkeyId = 1;
    const uint MOD_CONTROL = 0x2, MOD_ALT = 0x1, MOD_NOREPEAT = 0x4000;
    const uint VK_OEM_3 = 0xC0; // ` ~
    const uint WM_HOTKEY = 0x0312;
    static Dictation dictation; // lives as long as the message loop

    public const int AlreadyRunning = 2;
    static int watcherPid = -1; // never killed by the Ctrl+` toggle

    public static int Run(string[] args)
    {
        using var mutex = new Mutex(true, Program.DaemonMutex, out bool first);
        if (!first) return AlreadyRunning;
        int w = Array.IndexOf(args, "--watcher");
        if (w >= 0 && w + 1 < args.Length) int.TryParse(args[w + 1], out watcherPid);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("CRASH " + e.ExceptionObject);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Log("daemon exit");

        // Hotkey bound to the thread (hWnd = 0): WM_HOTKEY lands in this thread's queue.
        string label = "Ctrl+`";
        if (!Native.RegisterHotKey(IntPtr.Zero, HotkeyId, MOD_CONTROL | MOD_NOREPEAT, VK_OEM_3))
        {
            label = "Ctrl+Alt+`";
            if (!Native.RegisterHotKey(IntPtr.Zero, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_OEM_3))
            {
                Native.MessageBox(IntPtr.Zero, "Ctrl+` and Ctrl+Alt+` are both taken by another app.\nVowenLite's hotkey is off until you free one and log in again.", "VowenLite", 0x30);
                return 1;
            }
            // Non-blocking notice: the message loop below must run for the hotkey to work.
            new Thread(() => Native.MessageBox(IntPtr.Zero, "Ctrl+` is taken by another app, so VowenLite uses Ctrl+Alt+` instead.", "VowenLite", 0x40)) { IsBackground = true }.Start();
        }
        Jobs.Init();
        Native.SetProcessDpiAwarenessContext((IntPtr)(-4)); // per-monitor v2: crisp overlay
        Overlay.Create();
        Assistant.Restore();   // reminders set before the last restart
        Planner.Start();       // calendar alerts + Google sync loop
        dictation = new Dictation();
        Log("daemon up, hotkey " + label + (dictation.Install() ? ", Right Shift dictation" : ", dictation hook FAILED"));
        Native.TrimSelf();

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_HOTKEY && (int)msg.wParam == HotkeyId) Toggle();
            else Native.DispatchMessage(ref msg); // overlay window messages
        }
        Native.UnregisterHotKey(IntPtr.Zero, HotkeyId);
        return 0;
    }

    static void Toggle()
    {
        var running = AppProcesses();
        if (running.Count == 0)
        {
            var exe = Environment.ProcessPath;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
            Log("launched app");
        }
        else
        {
            // Ask nicely first so an open recording gets its WAV headers written, then force.
            try { if (EventWaitHandle.TryOpenExisting(Program.QuitEvent, out var ev)) using (ev) ev.Set(); } catch { }
            foreach (var p in running)
            {
                try { if (!p.WaitForExit(2500)) p.Kill(true); } catch { }
                p.Dispose();
            }
            Log("killed app");
        }
        Native.TrimSelf();
    }

    static List<Process> AppProcesses()
    {
        int self = Environment.ProcessId;
        var list = new List<Process>();
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Environment.ProcessPath)))
        {
            // The daemon is single-instance (mutex), so apart from the watcher any other VowenLite.exe is the app.
            if (p.Id == self || p.Id == watcherPid) { p.Dispose(); continue; }
            list.Add(p);
        }
        return list;
    }

    static void Log(string s)
    {
        try { File.AppendAllText(Path.Combine(Paths.DataDir, "daemon.log"), DateTime.Now.ToString("s") + " " + s + Environment.NewLine); } catch { }
    }
}

// Kill-on-close job object: whisper-cli children are assigned to it.
static class Jobs
{
    static IntPtr job;

    public static void Init()
    {
        job = Native.CreateJobObject(IntPtr.Zero, null);
        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        int len = Marshal.SizeOf(info);
        IntPtr ptr = Marshal.AllocHGlobal(len);
        Marshal.StructureToPtr(info, ptr, false);
        Native.SetInformationJobObject(job, 9, ptr, (uint)len);
        Marshal.FreeHGlobal(ptr);
    }

    public static void Add(Process p)
    {
        if (job != IntPtr.Zero) Native.AssignProcessToJobObject(job, p.Handle);
    }
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateJobObject(IntPtr attrs, string name);
    [DllImport("kernel32.dll")] public static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint len);
    [DllImport("kernel32.dll")] public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    public delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);

    // x64 INPUT: type, then the union (MOUSEINPUT is the largest member, 32 bytes) at offset 8.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public ushort wVk;
        [FieldOffset(10)] public ushort wScan;
        [FieldOffset(12)] public uint dwFlags;
        [FieldOffset(16)] public uint time;
        [FieldOffset(24)] public IntPtr extra;
    }

    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);

    // Types text into whatever has focus, as Unicode key events (clipboard untouched).
    public static void TypeText(string text)
    {
        const uint KEYBOARD = 1, UNICODE = 0x4, KEYUP = 0x2;
        var inputs = new INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[2 * i] = new INPUT { type = KEYBOARD, wScan = text[i], dwFlags = UNICODE };
            inputs[2 * i + 1] = new INPUT { type = KEYBOARD, wScan = text[i], dwFlags = UNICODE | KEYUP };
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    // Types text where "\n" becomes an Enter key press.
    public static void TypeLines(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > 0) TypeText(lines[i]);
            if (i < lines.Length - 1) Tap(0x0D);
        }
    }

    public static void Backspace(int n) => Tap(0x08, n);

    // Presses and releases a virtual key `times` times.
    public static void Tap(ushort vk, int times = 1)
    {
        const uint KEYBOARD = 1, KEYUP = 0x2;
        var inputs = new INPUT[times * 2];
        for (int i = 0; i < times; i++)
        {
            inputs[2 * i] = new INPUT { type = KEYBOARD, wVk = vk, dwFlags = Ext(vk) };
            inputs[2 * i + 1] = new INPUT { type = KEYBOARD, wVk = vk, dwFlags = Ext(vk) | KEYUP };
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    // Holds the keys in order, then releases them in reverse (e.g. Win+Shift+S).
    public static void Combo(params ushort[] vks)
    {
        const uint KEYBOARD = 1, KEYUP = 0x2;
        var inputs = new INPUT[vks.Length * 2];
        for (int i = 0; i < vks.Length; i++)
        {
            inputs[i] = new INPUT { type = KEYBOARD, wVk = vks[i], dwFlags = Ext(vks[i]) };
            inputs[vks.Length * 2 - 1 - i] = new INPUT { type = KEYBOARD, wVk = vks[i], dwFlags = Ext(vks[i]) | KEYUP };
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    static uint Ext(ushort vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E ? 1u : 0u; // arrows, nav keys need the extended flag

    public static void TrimSelf()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        EmptyWorkingSet(Process.GetCurrentProcess().Handle);
    }

    public static void FocusOther()
    {
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Environment.ProcessPath)))
        {
            if (p.Id != self && p.MainWindowHandle != IntPtr.Zero)
            {
                ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                SetForegroundWindow(p.MainWindowHandle);
            }
            p.Dispose();
        }
    }
}
