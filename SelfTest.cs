namespace VowenLite;

// `VowenLite.exe --selftest`: checks the transcript parse/merge logic. Exit 0 = pass.
static class SelfTest
{
    public static int Run()
    {
        int fails = 0;
        void Check(bool ok, string what) { if (!ok) { fails++; Console.Error.WriteLine("FAIL " + what); } }

        string mic = """{"transcription":[{"offsets":{"from":0,"to":2000},"text":" Hello there."},{"offsets":{"from":2500,"to":4000},"text":" How are you?"},{"offsets":{"from":9000,"to":9500},"text":" [BLANK_AUDIO]"}]}""";
        string sys = """{"transcription":[{"offsets":{"from":4500,"to":7000},"text":" Fine, thanks."},{"offsets":{"from":3700000,"to":3701000},"text":" Bye."}]}""";
        var segs = Transcriber.Parse(mic, "You");
        Check(segs.Count == 2, "noise segment dropped");
        segs.AddRange(Transcriber.Parse(sys, "Others"));
        string md = Transcriber.Format(@"C:\x\2026-10-23_14-05", segs);
        Check(md.StartsWith("# Meeting 2026-10-23 14:05"), "title from folder name");
        Check(md.Contains("[00:00] You: Hello there. How are you?"), "same speaker joined");
        Check(md.Contains("[00:04] Others: Fine, thanks."), "other speaker line");
        Check(md.Contains("[1:01:40] Others: Bye."), "hour stamp");
        Check(md.IndexOf("You:") < md.IndexOf("Others:"), "sorted by time");
        Check(Transcriber.Format(@"C:\x\y", new()).Contains("No speech"), "empty transcript");

        Console.WriteLine(fails == 0 ? "selftest ok" : fails + " failed");
        return fails == 0 ? 0 : 1;
    }

    // `VowenLite.exe --overlaytest`: shows the dictation pill (Listening 3 s, Transcribing 3 s).
    public static int ShowOverlay()
    {
        Native.SetProcessDpiAwarenessContext((IntPtr)(-4));
        Overlay.Create();
        Overlay.Set(Overlay.State.Listening);
        long t0 = Environment.TickCount64;
        Overlay.LevelSource = () => (float)(0.02 + 0.05 * Math.Abs(Math.Sin((Environment.TickCount64 - t0) / 260.0)) * Math.Abs(Math.Sin((Environment.TickCount64 - t0) / 97.0)));
        var t = new System.Threading.Timer(_ => Overlay.Set(Overlay.State.Transcribing), null, 3000, Timeout.Infinite);
        var t2 = new System.Threading.Timer(_ => Overlay.Set(Overlay.State.Hidden), null, 6000, Timeout.Infinite);
        var end = Environment.TickCount64 + 7000;
        while (Environment.TickCount64 < end)
        {
            while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1)) Native.DispatchMessage(ref msg);
            Thread.Sleep(15);
        }
        GC.KeepAlive(t); GC.KeepAlive(t2);
        return 0;
    }

    // `VowenLite.exe --rectest <seconds> <folder>`: headless record + transcribe, prints the transcript.
    public static int Record(int seconds, string folder)
    {
        Jobs.Init();
        Directory.CreateDirectory(folder);
        var s = Settings.Load();
        using (var r = new Recorder(folder, s.MicDeviceId))
        {
            r.Problem += m => Console.WriteLine("problem: " + m);
            r.Start();
            Thread.Sleep(seconds * 1000);
        }
        foreach (var f in new[] { "mic.wav", "system.wav" })
            Console.WriteLine(f + " " + new FileInfo(Path.Combine(folder, f)).Length + " bytes");
        string md = Transcriber.RunAsync(folder, s, new Progress<string>(Console.WriteLine)).GetAwaiter().GetResult();
        Console.WriteLine(File.ReadAllText(md));
        return 0;
    }
}
