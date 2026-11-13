using System.Diagnostics;

namespace VowenLite;

// `--watch`: started once at logon. Runs the daemon as a child and restarts it the moment it
// exits (crash or kill). If a daemon is already running on its own, waits for its mutex to be
// abandoned instead. Backs off if the daemon keeps dying straight away.
static class Watcher
{
    const string WatcherMutex = "Local\\VowenLite.Watcher";

    public static int Run()
    {
        using var self = new Mutex(true, WatcherMutex, out bool first);
        if (!first) return 0;
        Log("watcher up");
        Native.TrimSelf();

        var recent = new Queue<long>();
        while (true)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, WorkingDirectory = Paths.AppDir };
            foreach (var a in new[] { "--daemon", "--watcher", Environment.ProcessId.ToString() }) psi.ArgumentList.Add(a);
            int code;
            try
            {
                using var p = Process.Start(psi);
                p.WaitForExit();
                code = p.ExitCode;
            }
            catch (Exception ex) { Log("watcher: start failed: " + ex.Message); code = -1; }

            if (code == Daemon.AlreadyRunning)
            {
                // Someone else's daemon: block until it goes away (its mutex is released or abandoned).
                using var m = new Mutex(false, Program.DaemonMutex);
                try { m.WaitOne(); } catch (AbandonedMutexException) { }
                m.ReleaseMutex();
            }
            else Log("watcher: daemon exited (" + code + "), restarting");

            long now = Environment.TickCount64;
            recent.Enqueue(now);
            while (recent.Count > 0 && now - recent.Peek() > 60_000) recent.Dequeue();
            if (recent.Count >= 5) { Log("watcher: daemon keeps dying, waiting a minute"); Thread.Sleep(60_000); recent.Clear(); }
            else Thread.Sleep(500);
        }
    }

    static void Log(string s)
    {
        try { File.AppendAllText(Path.Combine(Paths.DataDir, "daemon.log"), DateTime.Now.ToString("s") + " " + s + Environment.NewLine); } catch { }
    }
}
