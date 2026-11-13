using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;

namespace VowenLite;

// Push-to-talk, run inside the daemon: hold Right Shift, speak, release. The mic opens only after
// the key has been held alone for a moment (so typing capitals never records), whisper-cli runs
// once per utterance and exits, and the text is typed into the focused window.
sealed class Dictation
{
    const int VK_RSHIFT = 0xA1;
    const int StartDelayMs = 40;   // key held alone this long before the mic opens (short, so the first word isn't clipped)
    const int OverlayDelayMs = 120; // ...and the pill only appears after this much longer, so capital letters (Shift+letter) don't flash it
    const int MinHoldMs = 400;     // shorter holds are treated as a plain Shift press
    const int TailMs = 200;        // keep recording briefly after release to catch the last word

    readonly BlockingCollection<Action> work = new();
    readonly Native.LowLevelKeyboardProc proc; // kept alive: the hook holds only a raw pointer
    IntPtr hook;

    // Key state: touched only on the hook (daemon) thread.
    volatile bool held, otherKey, lunaPress;
    volatile int activeVk;
    volatile int[] lunaVks = { 0xA3, 0xA5 };   // Right Ctrl and Right Alt = hold to talk to Luna (no keyword needed); empty = off. From Settings.LunaHoldVks().
    System.Threading.Timer startTimer, tailTimer, overlayTimer; // fields so they aren't collected before firing
    long downAt;
    int pressId;
    volatile bool f23Down;
    EventWaitHandle askSignal;
    const int VK_RCTRL = 0xA3;
    readonly TapDetector taps = new(VK_RCTRL);   // hook thread only
    [DllImport("user32")] static extern ushort GetAsyncKeyState(int vk);
    static bool MouseDown() => ((GetAsyncKeyState(0x01) | GetAsyncKeyState(0x02) | GetAsyncKeyState(0x04)) & 0x8000) != 0;

    // Typing mode: the box runs as its own process (--ask); on Enter it signals us with the text.
    void OpenAskBox()
    {
        try
        {
            if (!Settings.Load().Luna) return;
            Cancel();
            Process.Start(new ProcessStartInfo(Environment.ProcessPath, "--ask") { UseShellExecute = false });
        }
        catch (Exception ex) { Log("ask box: " + ex.Message); }
    }

    void StartAskListener()
    {
        askSignal = new EventWaitHandle(false, EventResetMode.AutoReset, AskBox.Event);
        new Thread(() =>
        {
            while (askSignal.WaitOne())
            {
                try
                {
                    Thread.Sleep(250);   // let the box close so the previous window is foreground again (screen/clipboard context)
                    string raw = File.Exists(AskBox.File) ? File.ReadAllText(AskBox.File) : "";
                    try { File.Delete(AskBox.File); } catch { }
                    string text = Threads.Parse(raw, out string thread);   // optional first line "thread:<id>" = a reply in that conversation
                    if (text.Length == 0) continue;
                    var s = Settings.Load();
                    if (!s.Luna) continue;
                    Log("typed: " + text + (thread != null ? "  [thread " + thread + "]" : ""));
                    if (!Luna.TryParse(text, out string req)) req = text;
                    Overlay.Set(Overlay.State.Thinking);
                    Luna.Start(req, s, () => work.Add(() => { if (recordingId < 0) Overlay.Set(Overlay.State.Hidden); }), true, thread);
                }
                catch (Exception ex) { Log("ask: " + ex.Message); }
            }
        }) { IsBackground = true, Name = "ask-listener" }.Start();
    }

    // Recording state: touched only on the worker thread.
    Track track;
    MMDeviceEnumerator enumerator;
    string wavPath;
    volatile int recordingId = -1;
    bool recLuna;   // this recording was started with the Luna key

    public Dictation()
    {
        proc = Hook;
        new Thread(() => { foreach (var a in work.GetConsumingEnumerable()) { try { a(); } catch (Exception ex) { Log(ex.Message); } } })
            { IsBackground = true, Name = "dictation" }.Start();
    }

    // Must be called on a thread that pumps messages (the daemon loop).
    public bool Install()
    {
        StartAskListener();
        hook = Native.SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, proc, Native.GetModuleHandle(null), 0);
        return hook != IntPtr.Zero;
    }

    IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            bool injected = (k.flags & 0x10) != 0;
            int msg = (int)wParam;
            bool down = msg == 0x100 || msg == 0x104, up = msg == 0x101 || msg == 0x105;
            // Copilot key = Win+Shift+F23: typing mode for Luna. F23 is swallowed so Copilot/search never opens.
            if (k.vkCode == 0x86 && !injected)
            {
                if (down && !f23Down) { f23Down = true; Log("copilot key -> typing box"); work.Add(OpenAskBox); }
                else if (up) f23Down = false;
                return (IntPtr)1;
            }
            if (!injected)
            {
                // Double-tap Right Ctrl = typing box. Detection only; the key still flows through to hold-to-talk below.
                if (k.vkCode == VK_RCTRL)
                {
                    if (down) { if (MouseDown()) taps.Other(); taps.Down(VK_RCTRL, Environment.TickCount64); }
                    else if (up && taps.Up(VK_RCTRL, Environment.TickCount64))
                        work.Add(() => { if (Settings.Load().AskDoubleTap) { Log("double-tap Right Ctrl -> typing box"); OpenAskBox(); } });
                }
                else if (down) taps.Other();
                bool isLuna = Array.IndexOf(lunaVks, (int)k.vkCode) >= 0;
                if (k.vkCode == VK_RSHIFT || isLuna)
                {
                    if (down && !held) OnDown((int)k.vkCode, isLuna);
                    else if (up && held && (int)k.vkCode == activeVk) OnUp();
                }
                else if (down && held) { otherKey = true; Cancel(); } // Shift+letter: a capital, not dictation
                else if (down && k.vkCode == 0x1B && PcBridge.Enabled) Task.Run(PcBridge.EscPressed);   // Esc: Luna lets go of the mouse and keyboard
            }
        }
        return Native.CallNextHookEx(hook, code, wParam, lParam); // never swallow keys
    }

    void OnDown(int vk, bool luna)
    {
        held = true;
        activeVk = vk;
        lunaPress = luna;
        otherKey = false;
        downAt = Environment.TickCount64;
        int id = ++pressId;
        startTimer?.Dispose();
        startTimer = new System.Threading.Timer(_ => work.Add(() => { if (pressId == id && held && !otherKey) Begin(id); }));
        startTimer.Change(StartDelayMs, Timeout.Infinite);
    }

    void OnUp()
    {
        held = false;
        int id = pressId;
        bool keep = !otherKey && Environment.TickCount64 - downAt >= MinHoldMs;
        if (!keep) { Cancel(); return; }
        tailTimer?.Dispose();
        tailTimer = new System.Threading.Timer(_ => work.Add(() => Finish(id)));
        tailTimer.Change(TailMs, Timeout.Infinite);
    }

    void Cancel()
    {
        int id = pressId;
        work.Add(() => { if (recordingId == id) { StopTrack(); TryDelete(wavPath); } });
    }

    // Worker thread from here on.
    void Begin(int id)
    {
        if (recordingId >= 0) return;
        var s = Settings.Load();
        lunaVks = s.LunaHoldVks();
        bool asLuna = lunaPress;
        if (asLuna ? !s.Luna : !s.Dictation) return;
        recLuna = asLuna;
        enumerator ??= new MMDeviceEnumerator();
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "VowenLite")).FullName;
        wavPath = Path.Combine(dir, "dictation-" + id + ".wav");
        track = new Track("dictation", wavPath, false, s.MicDeviceId, Stopwatch.StartNew(), enumerator);
        track.Start();
        var live = track;
        Overlay.LevelSource = () => live.Level;
        recordingId = id;
        var shown = asLuna ? Overlay.State.Assistant : Overlay.State.Listening;
        overlayTimer?.Dispose();
        overlayTimer = new System.Threading.Timer(_ => work.Add(() => { if (recordingId == id && held && !otherKey) Overlay.Set(shown); }));
        overlayTimer.Change(OverlayDelayMs, Timeout.Infinite);
    }

    void StopTrack()
    {
        try { track?.Stop(); } catch { }
        track = null;
        recordingId = -1;
        Overlay.Set(Overlay.State.Hidden);
    }

    void Finish(int id)
    {
        if (recordingId != id) return;
        string wav = wavPath;
        try { track?.Stop(); } catch { }
        track = null;
        Overlay.Set(Overlay.State.Transcribing);
        bool handedOff = false;
        try
        {
            var s = Settings.Load();
            lunaVks = s.LunaHoldVks();
            var sw = Stopwatch.StartNew();
            string text = Transcribe(wav, s);
            try { Log($"clip {Math.Max(0, new FileInfo(wav).Length - 46) / 32000.0:0.0}s, whisper {sw.Elapsed.TotalSeconds:0.00}s"); } catch { }
            string request = null;
            if (!recLuna && s.PlannerWord && Planner.TryDirect(text, out string planReq))
            {
                // "Planner, ..." with no Luna: the planner handles it itself (free, instant)
                string res = Planner.Direct(planReq);
                Log("planner: " + planReq + " -> " + (res ?? "").Replace("\n", " | "));
                if (!string.IsNullOrWhiteSpace(res)) { if (res.StartsWith("✓")) Overlay.Notice(res); else Luna.Show("Planner", res); }
                return;
            }
            if (!recLuna && s.PlannerWord && Regex.IsMatch(text, @"^\W*(?:plan|plain|blan|plann)", RegexOptions.IgnoreCase)) Log("planner: not matched, heard \"" + (text.Length > 80 ? text[..80] : text) + "\"");
            bool toLuna = s.Luna && (recLuna ? text.Length > 0 : Luna.TryParse(text, out request));
            if (toLuna)
            {
                // Luna key held, or "Luna, ..." said: a request for the assistant, not text to type.
                if (recLuna && !Luna.TryParse(text, out request)) request = text.Trim();
                handedOff = true;
                Overlay.Set(Overlay.State.Thinking);
                // Queued on this worker thread so it runs AFTER Finish() below has cleared recordingId (calling it
                // inline left the "Thinking" pill stuck on screen whenever Luna answered instantly).
                Luna.Start(request, s, () => work.Add(() => { if (recordingId < 0) Overlay.Set(Overlay.State.Hidden); }), recLuna);
            }
            else if (text.Length > 0) Voice.Type(text, s);
        }
        finally
        {
            recordingId = -1;
            if (!handedOff) Overlay.Set(Overlay.State.Hidden);
            TryDelete(wav);
            Native.TrimSelf();
        }
    }

    static readonly Regex Noise = new(@"[\[\(][^\]\)]*[\]\)]");

    public static string Transcribe(string wav, Settings s)
    {
        var psi = new ProcessStartInfo(s.WhisperPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(s.WhisperPath),
        };
        // Speed for short clips: greedy decoding, and an encoder context sized to the clip instead of
        // the fixed 30 s window (1500 frames = 30 s; ~3x faster for a few seconds of speech).
        double secs = Math.Max(0, new FileInfo(wav).Length - 44) / (Recorder.Rate * 2.0);
        int audioCtx = Math.Min(1500, Math.Max(256, (int)Math.Ceiling(secs * 50) + 64));
        foreach (var a in new[] { "-m", s.DictationModel(), "-f", wav, "-l", s.Language, "-t", s.Threads.ToString(), "-nt", "-np",
                                  "-bs", "1", "-bo", "1", "-ac", audioCtx.ToString() })
            psi.ArgumentList.Add(a);
        foreach (var a in s.PromptArgs(300)) psi.ArgumentList.Add(a);   // short glossary: keeps dictation fast
        // Full JSON (per-token confidence) lets Lexicon.Snap fix names whisper was unsure about; stdout stays the fallback.
        string jsonBase = Path.Combine(Path.GetDirectoryName(wav), Path.GetFileNameWithoutExtension(wav) + ".d");
        foreach (var a in new[] { "-ojf", "-of", jsonBase }) psi.ArgumentList.Add(a);
        if (File.Exists(s.VadModelPath))
            foreach (var a in new[] { "--vad", "-vm", s.VadModelPath }) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi);
        Jobs.Add(p);
        p.ErrorDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        string jsonFile = jsonBase + ".json";
        if (p.ExitCode != 0) { Log("whisper exit " + p.ExitCode); TryDelete(jsonFile); return ""; }

        try
        {
            if (File.Exists(jsonFile))
            {
                string snappedText = Lexicon.FromJson(File.ReadAllText(jsonFile), s, out int snapped);
                if (snapped > 0) Log($"snapped {snapped} name(s)");
                return Vocab.Fix(snappedText, s);
            }
        }
        catch (Exception ex) { Log("snap failed: " + ex.Message); }
        finally { TryDelete(jsonFile); }

        var sb = new StringBuilder();
        foreach (var line in output.Split('\n'))
        {
            string t = Noise.Replace(line, "").Trim();
            if (t.Length > 0) sb.Append(sb.Length > 0 ? " " : "").Append(t);
        }
        return Vocab.Fix(sb.ToString(), s);
    }

    static void TryDelete(string path) { try { if (path != null) File.Delete(path); } catch { } }

    static void Log(string s)
    {
        try { File.AppendAllText(Path.Combine(Paths.DataDir, "daemon.log"), DateTime.Now.ToString("s") + " dictation: " + s + Environment.NewLine); } catch { }
    }
}
