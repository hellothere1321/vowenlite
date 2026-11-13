using System.Globalization;
using System.Text.RegularExpressions;

namespace VowenLite;

// A repeat rule: the subset of RRULE that the planner, Luna and Google Calendar all understand
// (daily / weekly on chosen days / monthly on a day / yearly, every N, until a date or N times).
// Stored on an Item as the RRULE text ("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE"), which is also what Google gets.
sealed class Rule
{
    public string Freq = "WEEKLY";            // DAILY | WEEKLY | MONTHLY | YEARLY
    public int Interval = 1;
    public List<DayOfWeek> Days = new();      // WEEKLY: which days (empty = the day of the start)
    public int MonthDay;                      // MONTHLY: day of the month (0 = the day of the start)
    public DateTime? Until;                   // last day (inclusive)
    public int Count;                         // 0 = no limit
    internal int SpanN; internal char SpanU;  // spoken "for 6 weeks": turned into Until once the start is known

    static readonly string[] Codes = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };
    static int Mon0(DayOfWeek d) => ((int)d + 6) % 7;   // Monday = 0

    public static Rule Parse(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase)) s = s.Substring(6);
        var r = new Rule(); bool ok = false;
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            string v = kv[1];
            switch (kv[0].ToUpperInvariant())
            {
                case "FREQ": r.Freq = v.ToUpperInvariant(); ok = r.Freq is "DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY"; break;
                case "INTERVAL": r.Interval = Math.Max(1, int.TryParse(v, out int iv) ? iv : 1); break;
                case "COUNT": r.Count = int.TryParse(v, out int c) ? c : 0; break;
                case "UNTIL":
                    if (v.Length >= 8 && DateTime.TryParseExact(v.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    {
                        if (v.Length > 8 && v.EndsWith("Z") && DateTime.TryParseExact(v, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var u))
                            d = u.ToLocalTime();
                        r.Until = d.Date;
                    }
                    break;
                case "BYDAY":
                    foreach (var t in v.Split(','))
                    {
                        int k = Array.IndexOf(Codes, t.Trim().ToUpperInvariant());
                        if (k < 0) return null;                 // "2nd Tuesday" and friends: not supported, keep the event as Google has it
                        r.Days.Add((DayOfWeek)k);
                    }
                    break;
                case "BYMONTHDAY": r.MonthDay = int.TryParse(v, out int md) && md > 0 ? md : 0; if (md <= 0) return null; break;
                case "WKST": break;
                default: return null;
            }
        }
        return ok ? r : null;
    }

    // allDay / timed matters for UNTIL: Google wants a UTC date-time when the event has a time.
    public string ToRrule(bool allDay = true)
    {
        var sb = new System.Text.StringBuilder("FREQ=" + Freq);
        if (Interval > 1) sb.Append(";INTERVAL=" + Interval);
        if (Freq == "WEEKLY" && Days.Count > 0) sb.Append(";BYDAY=" + string.Join(",", Days.OrderBy(Mon0).Select(d => Codes[(int)d])));
        if (Freq == "MONTHLY" && MonthDay > 0) sb.Append(";BYMONTHDAY=" + MonthDay);
        if (Count > 0) sb.Append(";COUNT=" + Count);
        else if (Until != null)
            sb.Append(";UNTIL=" + (allDay ? Until.Value.ToString("yyyyMMdd") : Until.Value.Date.AddDays(1).AddSeconds(-1).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'")));
        return sb.ToString();
    }

    public override string ToString() => ToRrule(true);

    // Occurrence starts (with the time of day of `start`), in order, from `start` up to (not including) `to`.
    public IEnumerable<DateTime> Expand(DateTime start, DateTime to)
    {
        int n = 0;
        foreach (var d in Raw(start))
        {
            if (d >= to) yield break;
            if (Until != null && d.Date > Until.Value.Date) yield break;
            if (Count > 0 && ++n > Count) yield break;
            yield return d;
        }
    }

    IEnumerable<DateTime> Raw(DateTime start)
    {
        var tod = start.TimeOfDay; int step = Math.Max(1, Interval);
        switch (Freq)
        {
            case "DAILY":
                for (int k = 0; k < 20000; k++) yield return start.Date.AddDays((long)k * step).Add(tod);
                break;
            case "WEEKLY":
            {
                var days = (Days.Count > 0 ? Days : new List<DayOfWeek> { start.DayOfWeek }).OrderBy(Mon0).ToList();
                var wk0 = start.Date.AddDays(-Mon0(start.DayOfWeek));
                for (int w = 0; w < 5000; w++)
                {
                    var wk = wk0.AddDays(7L * w * step);
                    foreach (var d in days)
                    {
                        var c = wk.AddDays(Mon0(d)).Add(tod);
                        if (c >= start) yield return c;
                    }
                }
                break;
            }
            case "MONTHLY":
            {
                int md = MonthDay > 0 ? MonthDay : start.Day;
                for (int k = 0; k < 2400; k++)
                {
                    var m0 = new DateTime(start.Year, start.Month, 1).AddMonths(k * step);
                    if (md > DateTime.DaysInMonth(m0.Year, m0.Month)) continue;   // "the 31st" skips short months (same as Google)
                    var c = m0.AddDays(md - 1).Add(tod);
                    if (c >= start) yield return c;
                }
                break;
            }
            case "YEARLY":
                for (int k = 0; k < 400; k++)
                {
                    int y = start.Year + k * step;
                    if (start.Month == 2 && start.Day == 29 && !DateTime.IsLeapYear(y)) continue;
                    yield return new DateTime(y, start.Month, start.Day).Add(tod);
                }
                break;
        }
    }

    static string Ordinal(int n) => n + (n % 100 is >= 11 and <= 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    // "Every 2 weeks on Mon, Wed", "Every weekday", "Every month on the 15th until Dec 20"
    public string Describe(DateTime start)
    {
        string s;
        int n = Math.Max(1, Interval);
        var days = Days.OrderBy(Mon0).ToList();
        switch (Freq)
        {
            case "DAILY": s = n == 1 ? "Every day" : $"Every {n} days"; break;
            case "WEEKLY":
                if (days.Count == 0) days = new List<DayOfWeek> { start.DayOfWeek };
                bool weekdays = days.Count == 5 && days.All(d => d != DayOfWeek.Saturday && d != DayOfWeek.Sunday);
                bool weekend = days.Count == 2 && days.Contains(DayOfWeek.Saturday) && days.Contains(DayOfWeek.Sunday);
                string dn = weekdays ? "weekday" : weekend ? "weekend" : null;
                string list = string.Join(", ", days.Select(d => days.Count == 1 ? d.ToString() : d.ToString().Substring(0, 3)));
                if (n == 1) s = dn != null ? "Every " + dn : days.Count == 1 ? "Every " + list : "Every week on " + list;
                else s = (n == 2 ? "Every other week" : $"Every {n} weeks") + (dn != null ? " on " + dn + "s" : " on " + list);
                break;
            case "MONTHLY":
                s = (n == 1 ? "Every month" : n == 2 ? "Every other month" : $"Every {n} months") + " on the " + Ordinal(MonthDay > 0 ? MonthDay : start.Day);
                break;
            default: s = n == 1 ? "Every year" : $"Every {n} years"; break;
        }
        if (Count > 0) s += $", {Count} times";
        else if (Until != null) s += " until " + Until.Value.ToString("MMM d");
        return s;
    }

    // ---------------- spoken / typed repeat phrases

    const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    const string DayTok = @"(?:tues|tue|wednes|wed|thurs|thur|thu|fri|satur|sat|mon|sun)(?:day)?";
    const string DayPlural = @"(?:mon|tues|wednes|thurs|fri|satur|sun)days";
    const string Month = @"jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sept?(?:ember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?";
    const string NumWord = @"\d+|two|three|four|five|six|seven|eight|nine|ten|twelve";
    static int Num(string t) => int.TryParse(t, out int n) ? n : t.ToLowerInvariant() switch { "two" => 2, "three" => 3, "four" => 4, "five" => 5, "six" => 6, "seven" => 7, "eight" => 8, "nine" => 9, "ten" => 10, "twelve" => 12, _ => 1 };

    static DayOfWeek DayOf(string t)
    {
        t = t.ToLowerInvariant();
        string[] n = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };
        return (DayOfWeek)Math.Max(0, Array.FindIndex(n, x => t.StartsWith(x)));
    }

    static List<DayOfWeek> DaysIn(string list) =>
        Regex.Matches(list, DayTok, IC).Select(m => DayOf(m.Value)).Distinct().ToList();

    static string Cut(string s, Match m, string with = " ") => s.Remove(m.Index, m.Length).Insert(m.Index, with);

    // Finds a repeat phrase ("every other tuesday", "weekly", "every monday and wednesday until dec 20", "daily for 6 weeks")
    // and removes it from the text. Returns null when there is none.
    public static Rule FromText(string text, DateTime now, out string rest)
    {
        rest = text;
        string s = " " + text + " ";
        Match m;
        Rule r = null;

        if ((m = Regex.Match(s, @"\b(?:every|each)\s+other\s+(?:(?<u>day|week|month|year)|(?<dn>" + DayTok + @"))\b", IC)).Success)
        {
            r = new Rule { Interval = 2 };
            if (m.Groups["dn"].Success) { r.Freq = "WEEKLY"; r.Days.Add(DayOf(m.Groups["dn"].Value)); }
            else r.Freq = m.Groups["u"].Value.ToUpperInvariant() switch { "DAY" => "DAILY", "WEEK" => "WEEKLY", "MONTH" => "MONTHLY", _ => "YEARLY" };
        }
        else if ((m = Regex.Match(s, @"\b(?:bi-?weekly|fortnightly|every\s+(?:two|2)\s+weeks)\b", IC)).Success) r = new Rule { Freq = "WEEKLY", Interval = 2 };
        else if ((m = Regex.Match(s, @"\b(?:every|each)\s+(?<n>" + NumWord + @")\s+(?<u>days?|weeks?|months?|years?)\b", IC)).Success)
            r = new Rule { Freq = m.Groups["u"].Value.ToUpperInvariant() switch { var x when x.StartsWith("D") => "DAILY", var x when x.StartsWith("W") => "WEEKLY", var x when x.StartsWith("M") => "MONTHLY", _ => "YEARLY" }, Interval = Math.Max(1, Num(m.Groups["n"].Value)) };
        else if ((m = Regex.Match(s, @"\b(?:every\s+(?:week\s?day|work\s?day)s?|week\s?days|(?:on\s+)?(?:every\s+)?week\s?days)\b", IC)).Success)
            r = new Rule { Freq = "WEEKLY", Days = new() { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday } };
        else if ((m = Regex.Match(s, @"\b(?:every\s+weekend|weekends)\b", IC)).Success)
            r = new Rule { Freq = "WEEKLY", Days = new() { DayOfWeek.Saturday, DayOfWeek.Sunday } };
        else if ((m = Regex.Match(s, @"\b(?:every|each)\s+(?<l>" + DayTok + @"s?(?:\s*(?:,|and|&)\s*(?:and\s+)?" + DayTok + @"s?)*)\b", IC)).Success
                 || (m = Regex.Match(s, @"\b(?<l>" + DayPlural + @"(?:\s*(?:,|and|&)\s*(?:and\s+)?" + DayPlural + @")*)\b", IC)).Success)
            r = new Rule { Freq = "WEEKLY", Days = DaysIn(m.Groups["l"].Value) };
        else if ((m = Regex.Match(s, @"\bevery\s+(?<d>\d{1,2})(?:st|nd|rd|th)\b", IC)).Success)
            r = new Rule { Freq = "MONTHLY", MonthDay = Math.Clamp(int.Parse(m.Groups["d"].Value), 1, 31) };
        else if ((m = Regex.Match(s, @"\b(?:monthly|every\s+month|each\s+month|once\s+a\s+month)\b(?:\s+on\s+the\s+(?<d>\d{1,2})(?:st|nd|rd|th))?", IC)).Success)
            r = new Rule { Freq = "MONTHLY", MonthDay = m.Groups["d"].Success ? Math.Clamp(int.Parse(m.Groups["d"].Value), 1, 31) : 0 };
        else if ((m = Regex.Match(s, @"\b(?:yearly|annually|every\s+year|each\s+year|once\s+a\s+year)\b", IC)).Success) r = new Rule { Freq = "YEARLY" };
        else if ((m = Regex.Match(s, @"\b(?:weekly|every\s+week|each\s+week|once\s+a\s+week)\b", IC)).Success) r = new Rule { Freq = "WEEKLY" };
        else if ((m = Regex.Match(s, @"\bevery\s+(?<w>morning|afternoon|evening|night)\b", IC)).Success)
        {
            r = new Rule { Freq = "DAILY" };
            s = Cut(s, m, " " + (m.Groups["w"].Value.Equals("night", StringComparison.OrdinalIgnoreCase) ? "evening" : m.Groups["w"].Value) + " ");
            m = null;
        }
        else if ((m = Regex.Match(s, @"\b(?:daily|every\s+day|each\s+day|everyday|once\s+a\s+day)\b", IC)).Success) r = new Rule { Freq = "DAILY" };
        if (r == null) return null;
        if (m != null) s = Cut(s, m);

        if ((m = Regex.Match(s, @"\b(?:until|till|through|thru|ending(?:\s+on)?)\s+(?<d>(?:(?:next|this)\s+)?(?:(?:" + Month + @")\.?\s*\d{1,2}(?:st|nd|rd|th)?(?:,?\s*\d{4})?|\d{1,2}/\d{1,2}(?:/\d{2,4})?|\d{4}-\d{2}-\d{2}|" + DayTok + @"|tomorrow|today))", IC)).Success)
        {
            var w = When.Parse(m.Groups["d"].Value, now);
            if (w.At != null) { r.Until = w.At.Value.Date; s = Cut(s, m); }
        }
        else if ((m = Regex.Match(s, @"\bfor\s+(?:the\s+next\s+)?(?<n>" + NumWord + @")\s+(?<u>weeks?|months?|days?|years?|times|occurrences?|sessions?)\b", IC)).Success)
        {
            int n = Num(m.Groups["n"].Value); string u = m.Groups["u"].Value.ToLowerInvariant();
            if (u.StartsWith("t") || u.StartsWith("o") || u.StartsWith("s")) r.Count = n; else { r.SpanN = n; r.SpanU = u[0]; }
            s = Cut(s, m);
        }
        else if ((m = Regex.Match(s, @"\b(?<n>\d+)\s+times\b", IC)).Success) { r.Count = int.Parse(m.Groups["n"].Value); s = Cut(s, m); }

        rest = Regex.Replace(s, @"\s+", " ").Trim();
        return r;
    }

    // Spoken "for 6 weeks" becomes a last day once the first day is known.
    internal void ResolveSpan(DateTime start)
    {
        if (SpanN <= 0 || Until != null || Count > 0) return;
        Until = SpanU switch { 'd' => start.Date.AddDays(SpanN - 1), 'w' => start.Date.AddDays(7 * SpanN - 1), 'm' => start.Date.AddMonths(SpanN).AddDays(-1), _ => start.Date.AddYears(SpanN).AddDays(-1) };
        SpanN = 0;
    }

    // First day on or after `from` that this rule can start on (so "every monday" begun on a Wednesday starts next Monday).
    internal DateTime FirstOn(DateTime from)
    {
        if (Freq == "WEEKLY" && Days.Count > 0)
        {
            for (int i = 0; i < 7; i++) if (Days.Contains(from.Date.AddDays(i).DayOfWeek)) return from.Date.AddDays(i).Add(from.TimeOfDay);
        }
        if (Freq == "MONTHLY" && MonthDay > 0)
        {
            foreach (var d in Expand(from.Date, from.Date.AddYears(3))) return d.Add(from.TimeOfDay);
        }
        return from;
    }
}
