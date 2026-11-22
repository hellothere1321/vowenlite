using System.Globalization;

namespace VowenLite;

// One-tap cards for the assistant modules: "Add 'Send Sam the notes' for Fri?", the evening rollover, "Quiz 13.2 is due tonight".
// Click = Click action (then the card goes), x = Dismissed action (then it goes). A key identifies a nudge: showing the same key replaces it.
static class Nudge
{
    sealed class N { public string Key, Primary, Secondary, Detail; public CardIcon Icon; public Action Click, Dismissed; public DateTime Until; public long Order; }
    static readonly List<N> list = new();
    static long order;
    const int MaxShown = 2;   // the stack is shared with timers and Luna's work cards

    public static void Show(string key, string primary, string secondary, Action click = null, Action dismissed = null,
                            string detail = null, TimeSpan? ttl = null, CardIcon icon = CardIcon.Dot)
    {
        var n = new N { Key = key, Primary = primary, Secondary = secondary ?? "", Detail = detail, Icon = icon, Click = click, Dismissed = dismissed,
                        Until = DateTime.Now + (ttl ?? TimeSpan.FromHours(4)), Order = Interlocked.Increment(ref order) };
        lock (list) { list.RemoveAll(x => x.Key == key); list.Add(n); }
        Overlay.ChipRefresh();
    }

    public static void Remove(string key) { lock (list) list.RemoveAll(x => x.Key == key); Overlay.ChipRefresh(); }
    public static bool Has(string key) { lock (list) return list.Any(x => x.Key == key && x.Until > DateTime.Now); }

    public static IEnumerable<Row> Rows()
    {
        List<N> shown;
        lock (list)
        {
            list.RemoveAll(x => x.Until <= DateTime.Now);
            shown = list.OrderByDescending(x => x.Order).Take(MaxShown).ToList();
        }
        foreach (var n in shown) yield return new Row(n.Icon, n.Primary, n.Secondary, 0, "nudge:" + n.Key, n.Detail);
    }

    public static void Activate(string id) => Take(id)?.Click?.Invoke();
    public static void Dismiss(string id) => Take(id)?.Dismissed?.Invoke();

    static N Take(string id)
    {
        string key = id.Substring("nudge:".Length);
        N n;
        lock (list) { n = list.FirstOrDefault(x => x.Key == key); if (n != null) list.Remove(n); }
        return n;
    }

    // Quiet hours ("00:30-08:00", may wrap past midnight; "" = never quiet): no proactive cards, speech or phone nudges.
    public static bool Quiet(Settings s, DateTime now) => InWindow(s.QuietHours, now);

    internal static bool InWindow(string spec, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(spec)) return false;
        var parts = spec.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !TimeSpan.TryParseExact(parts[0], @"hh\:mm", CultureInfo.InvariantCulture, out var a)
            || !TimeSpan.TryParseExact(parts[1], @"hh\:mm", CultureInfo.InvariantCulture, out var b)) return false;
        var t = now.TimeOfDay;
        return a <= b ? t >= a && t < b : t >= a || t < b;
    }

    public static void SelfChecks(Action<bool, string> check)
    {
        var d = new DateTime(2026, 11, 21);
        check(InWindow("00:30-08:00", d.AddHours(3)), "quiet: inside a morning window");
        check(!InWindow("00:30-08:00", d.AddHours(9)), "quiet: outside a morning window");
        check(InWindow("23:00-07:00", d.AddHours(23.5)) && InWindow("23:00-07:00", d.AddHours(2)), "quiet: window past midnight");
        check(!InWindow("23:00-07:00", d.AddHours(12)), "quiet: midday outside a night window");
        check(!InWindow("", d) && !InWindow("junk", d), "quiet: empty or bad spec is never quiet");
    }
}
