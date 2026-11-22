using System.Text.Json;

namespace VowenLite;

// One day of time tracking (seconds). Only totals are kept: no window titles, no timeline.
sealed class DayLog
{
    public Dictionary<string, int> Tasks { get; set; } = new();   // planner task Id -> seconds worked while it was the active task
    public Dictionary<string, int> Apps { get; set; } = new();    // process name ("code", "chrome") -> seconds in front
    public int[] Hours { get; set; } = new int[24];               // seconds of activity (user not idle) in each hour of the day
}

// timelog.json: "yyyy-MM-dd" -> DayLog. Written by Focus.Sample (daemon), read by Habits/Coach. Kept 120 days.
static class TimeLog
{
    static string FilePath => Path.Combine(Paths.DataDir, "timelog.json");
    static readonly object gate = new();
    static Dictionary<string, DayLog> data;
    static bool dirty;
    static long flushedAt = Environment.TickCount64;

    public static string DayKey(DateTime d) => d.ToString("yyyy-MM-dd");

    public static Dictionary<string, DayLog> Load()
    {
        lock (gate)
        {
            if (data != null) return data;
            try { data = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, DayLog>>(File.ReadAllText(FilePath)) ?? new() : new(); }
            catch { data = new(); }
            return data;
        }
    }

    public static DayLog Day(DateTime d)
    {
        lock (gate)
        {
            var all = Load();
            if (!all.TryGetValue(DayKey(d), out var day)) all[DayKey(d)] = day = new DayLog();
            return day;
        }
    }

    // One sample: `seconds` of activity at `at`, in app `app` (may be null), on task `taskId` (null = no active task).
    public static void Add(DateTime at, string app, string taskId, int seconds)
    {
        if (seconds <= 0) return;
        lock (gate)
        {
            var day = Day(at);
            day.Hours[at.Hour] += seconds;
            if (!string.IsNullOrEmpty(app)) day.Apps[app] = day.Apps.GetValueOrDefault(app) + seconds;
            if (!string.IsNullOrEmpty(taskId)) day.Tasks[taskId] = day.Tasks.GetValueOrDefault(taskId) + seconds;
            dirty = true;
        }
        if (Environment.TickCount64 - flushedAt > 5 * 60_000) Flush();
    }

    public static void Flush()
    {
        lock (gate)
        {
            flushedAt = Environment.TickCount64;
            if (!dirty || data == null) return;
            string cut = DayKey(DateTime.Today.AddDays(-120));
            foreach (var k in data.Keys.Where(k => string.CompareOrdinal(k, cut) < 0).ToList()) data.Remove(k);
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(data)); dirty = false; } catch { }
        }
    }

    // Tests: forget the in-memory copy so the next Load reads the file again.
    internal static void Reset() { lock (gate) { data = null; dirty = false; } }

    public static void SelfChecks(Action<bool, string> check)
    {
        check(DayKey(new DateTime(2026, 1, 5, 23, 59, 0)) == "2026-01-05", "timelog: day key");
        var d = new DayLog();
        check(d.Hours.Length == 24 && d.Tasks.Count == 0, "timelog: empty day");
    }
}
