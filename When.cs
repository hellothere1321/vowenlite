using System.Globalization;
using System.Text.RegularExpressions;

namespace VowenLite;

// Pulls a date/time phrase out of free text: "dentist tomorrow at 3pm for an hour", "email sam by friday",
// "call mom on nov 12th at 5:30", "in 2 days", "tonight". Plain rules, no model.
static class When
{
    public sealed class Result
    {
        public DateTime? At;          // the date, plus the time of day when HasTime
        public bool HasTime;
        public bool Relative;         // "in 10 minutes / 2 hours": the old in-memory timers own these
        public TimeSpan? Duration;    // "for 2 hours" (only when asked for)
        public string Rest = "";      // the text with the date/time words removed
    }

    const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    const string Num = @"\d+(?:\.\d+)?|an?|half an?|one|two|three|four|five|six|seven|eight|nine|ten|fifteen|twenty|thirty|forty five";
    const string HourWord = @"\d{1,2}|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve";
    const string MonthRx = @"jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sept?(?:ember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?";
    static readonly string[] DayNames = { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" };
    static readonly Dictionary<string, int> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["fifteen"] = 15, ["twenty"] = 20, ["thirty"] = 30, ["forty five"] = 45,
    };

    static double Amount(string t)
    {
        t = t.Trim().ToLowerInvariant();
        if (t is "a" or "an") return 1;
        if (t.StartsWith("half")) return 0.5;
        if (Words.TryGetValue(t, out int w)) return w;
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 1;
    }

    static int Hour(string t) => int.TryParse(t, out int h) ? h : Words.GetValueOrDefault(t.ToLowerInvariant(), 0);

    static int MonthNo(string t)
    {
        t = t.ToLowerInvariant();
        string[] m = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };
        return Array.FindIndex(m, x => t.StartsWith(x)) + 1;
    }

    static string Cut(string s, Match m) => s.Remove(m.Index, m.Length).Insert(m.Index, " ");

    public static Result Parse(string text, DateTime now, bool wantDuration = false)
    {
        var r = new Result();
        string s = " " + Regex.Replace(text ?? "", @"\b([ap])\.m\.?", "$1m", IC) + " ";
        s = Regex.Replace(s, @"\bo'?clock\b", " ", IC);
        Match m;

        if (wantDuration && (m = Regex.Match(s, @"\bfor\s+(?<n>" + Num + @")\s*(?<u>hours?|hrs?|minutes?|mins?)\b", IC)).Success)
        {
            double n = Amount(m.Groups["n"].Value);
            r.Duration = m.Groups["u"].Value.StartsWith("h", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromHours(n) : TimeSpan.FromMinutes(n);
            s = Cut(s, m);
        }

        // "830-9pm", "5:30-7:30", "3 to 5pm", "9am-10:30am", "17:00-19:00": a time range = start time + duration
        if (wantDuration && (m = Regex.Match(s, @"(?<![\d:/.])(?<h1>\d{1,2})(?::?(?<m1>\d{2}))?\s*(?<a1>am|pm)?\s*(?:-|\u2013|\u2014|to|until|till)\s*(?<h2>\d{1,2})(?::?(?<m2>\d{2}))?\s*(?<a2>am|pm)?\b(?!:)", IC)).Success)
        {
            int h1 = int.Parse(m.Groups["h1"].Value), h2 = int.Parse(m.Groups["h2"].Value);
            int m1 = m.Groups["m1"].Success ? int.Parse(m.Groups["m1"].Value) : 0, m2 = m.Groups["m2"].Success ? int.Parse(m.Groups["m2"].Value) : 0;
            bool hasAp = m.Groups["a1"].Success || m.Groups["a2"].Success;
            bool hasMin = m.Groups["m1"].Success || m.Groups["m2"].Success;
            bool lead0 = m.Groups["h1"].Value.StartsWith("0") || m.Groups["h2"].Value.StartsWith("0");
            if (m1 < 60 && m2 < 60 && (hasAp || hasMin) && h1 <= 23 && h2 <= 23)
            {
                string startText = null; int start = 0, end = 0;
                if (hasAp)
                {
                    if (h1 is >= 1 and <= 12 && h2 is >= 1 and <= 12)
                    {
                        bool p2 = m.Groups["a2"].Success ? m.Groups["a2"].Value.StartsWith("p", StringComparison.OrdinalIgnoreCase) : m.Groups["a1"].Value.StartsWith("p", StringComparison.OrdinalIgnoreCase);
                        bool p1 = m.Groups["a1"].Success ? m.Groups["a1"].Value.StartsWith("p", StringComparison.OrdinalIgnoreCase) : p2;
                        start = (h1 % 12 + (p1 ? 12 : 0)) * 60 + m1; end = (h2 % 12 + (p2 ? 12 : 0)) * 60 + m2;
                        if (!m.Groups["a1"].Success && start >= end) start -= 12 * 60;   // "11-1pm" starts at 11 am
                        if (start < 0) start += 12 * 60;
                        if (end <= start) end += 24 * 60;
                        startText = $"{(start / 60 % 12 == 0 ? 12 : start / 60 % 12)}:{start % 60:00} {(start / 60 % 24 >= 12 ? "pm" : "am")}";
                    }
                }
                else if (h1 > 12 || h2 > 12 || lead0)
                {
                    start = h1 * 60 + m1; end = h2 * 60 + m2; if (end <= start) end += 24 * 60;   // 24-hour clock
                    startText = $"{h1:00}:{m1:00}";
                }
                else if (h1 >= 1)
                {
                    start = h1 * 60 + m1; end = h2 * 60 + m2; int d = ((end - start) % 720 + 720) % 720;   // "5:30-7:30": am/pm is guessed like a plain "at 5:30"
                    if (d > 0) { end = start + d; startText = $"{h1}:{m1:00}"; }
                }
                if (startText != null)
                {
                    r.Duration = TimeSpan.FromMinutes(end - start);
                    s = s.Remove(m.Index, m.Length).Insert(m.Index, " at " + startText + " ");
                }
            }
        }

        // "in 20 minutes / 2 hours": a plain countdown. "in 3 days / 2 weeks" is a date.
        DateTime? date = null;
        if ((m = Regex.Match(s, @"\bin\s+(?<n>" + Num + @")\s*(?<u>seconds?|secs?|minutes?|mins?|hours?|hrs?|days?|weeks?)\b", IC)).Success)
        {
            double n = Amount(m.Groups["n"].Value);
            char u = char.ToLowerInvariant(m.Groups["u"].Value[0]);
            if (u is 's' or 'm' or 'h')
            {
                r.At = now + (u == 's' ? TimeSpan.FromSeconds(n) : u == 'h' ? TimeSpan.FromHours(n) : TimeSpan.FromMinutes(n));
                r.HasTime = true; r.Relative = true;
                r.Rest = Clean(Cut(s, m));
                return r;
            }
            date = now.Date.AddDays(u == 'w' ? 7 * n : n);
            s = Cut(s, m);
        }

        const string pre = @"(?:\b(?:on|by|due|until|before|for)\s+)?";
        bool tonight = false;
        if (date == null && (m = Regex.Match(s, pre + @"\b(?<w>day after tomorrow|tomorrow|today|tonight|this weekend|next week)\b", IC)).Success)
        {
            switch (m.Groups["w"].Value.ToLowerInvariant())
            {
                case "today": date = now.Date; break;
                case "tonight": date = now.Date; tonight = true; break;
                case "tomorrow": date = now.Date.AddDays(1); break;
                case "day after tomorrow": date = now.Date.AddDays(2); break;
                case "this weekend": date = NextDay(now.Date, DayOfWeek.Saturday, false, true); break;
                case "next week": date = NextDay(now.Date, DayOfWeek.Monday, true, false); break;
            }
            s = Cut(s, m);
        }
        if (date == null && (m = Regex.Match(s, pre + @"\b(?:(?<mod>next|this|coming)\s+)?(?<d>monday|tuesday|wednesday|thursday|friday|saturday|sunday|tues|thurs|weds)\b", IC)).Success)
        {
            string d = m.Groups["d"].Value.ToLowerInvariant();
            int dow = Array.FindIndex(DayNames, x => x.StartsWith(d.Substring(0, 3)));
            string mod = m.Groups["mod"].Value.ToLowerInvariant();
            date = NextDay(now.Date, (DayOfWeek)dow, mod == "next", mod == "this");
            s = Cut(s, m);
        }
        if (date == null && ((m = Regex.Match(s, pre + @"\b(?<y>\d{4})-(?<mo>\d{1,2})-(?<d>\d{1,2})\b", IC)).Success
            || (m = Regex.Match(s, pre + @"\b(?<mo>" + MonthRx + @")\.?\s+(?<d>\d{1,2})(?:st|nd|rd|th)?\b(?:,?\s*(?<y>\d{4}))?", IC)).Success
            || (m = Regex.Match(s, pre + @"\b(?<d>\d{1,2})(?:st|nd|rd|th)?\s+(?:of\s+)?(?<mo>" + MonthRx + @")\b(?:,?\s*(?<y>\d{4}))?", IC)).Success
            || (m = Regex.Match(s, pre + @"\b(?<mo>\d{1,2})/(?<d>\d{1,2})(?:/(?<y>\d{2,4}))?\b", IC)).Success))
        {
            string mo = m.Groups["mo"].Value;
            int month = int.TryParse(mo, out int mi0) ? mi0 : MonthNo(mo), day = int.Parse(m.Groups["d"].Value);
            int year = now.Year;
            bool yearGiven = m.Groups["y"].Success;
            if (yearGiven) { year = int.Parse(m.Groups["y"].Value); if (year < 100) year += 2000; }
            if (month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month))
            {
                var d = new DateTime(year, month, day);
                if (!yearGiven && d < now.Date) d = d.AddYears(1);
                date = d;
                s = Cut(s, m);
            }
        }

        // time of day
        const string at = @"(?:\b(?:at|around|by|@)\s*)?";
        int hour = -1, minute = 0; bool ambiguous = false;
        if ((m = Regex.Match(s, at + @"\b(?<h>" + HourWord + @")(?::(?<mi>\d{2}))?\s*(?<ap>am|pm)\b", IC)).Success)
        {
            int h = Hour(m.Groups["h"].Value);
            if (h is >= 1 and <= 12)
            {
                hour = h % 12 + (m.Groups["ap"].Value.StartsWith("p", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
                minute = m.Groups["mi"].Success ? int.Parse(m.Groups["mi"].Value) : 0;
                s = Cut(s, m);
            }
        }
        else if ((m = Regex.Match(s, at + @"\b(?<h>\d{1,2}):(?<mi>\d{2})\b", IC)).Success && int.Parse(m.Groups["h"].Value) < 24 && int.Parse(m.Groups["mi"].Value) < 60)
        {
            hour = int.Parse(m.Groups["h"].Value); minute = int.Parse(m.Groups["mi"].Value);
            ambiguous = hour is >= 1 and <= 12 && !m.Value.TrimStart().StartsWith("0");
            s = Cut(s, m);
        }
        else if ((m = Regex.Match(s, @"\b(?:at|around|@)\s*(?<h>" + HourWord + @")\b(?!\s*(?:/|%|st|nd|rd|th|minutes?|mins?|hours?|hrs?|days?|people|items?))", IC)).Success
                 && Hour(m.Groups["h"].Value) is >= 1 and <= 12)
        {
            hour = Hour(m.Groups["h"].Value) % 12; ambiguous = true;   // "at 5": am or pm decided below
            s = Cut(s, m);
        }
        else if ((m = Regex.Match(s, @"\b(?:at\s+)?(?<w>noon|midnight)\b", IC)).Success)
        {
            hour = m.Groups["w"].Value.Equals("noon", StringComparison.OrdinalIgnoreCase) ? 12 : 0;
            s = Cut(s, m);
        }

        string part = null;
        if ((m = Regex.Match(s, @"\b(?:in the |this |at )?(?<w>morning|afternoon|evening|tonight)\b", IC)).Success)
        {
            part = m.Groups["w"].Value.ToLowerInvariant();
            if (part == "tonight") { tonight = true; part = "evening"; }
            s = Cut(s, m);
        }
        if (tonight && date == null) date = now.Date;

        if (hour < 0 && (part != null || tonight)) { hour = part == "morning" ? 9 : part == "afternoon" ? 15 : 19; }
        else if (hour >= 0 && ambiguous)
        {
            if (part == "morning") { }
            else if (part is "afternoon" or "evening") hour = hour % 12 + 12;
            else
            {
                int h12 = hour == 0 ? 12 : hour;
                bool today = date == null || date.Value.Date == now.Date;
                int am = h12 % 12, pm = h12 % 12 + 12;
                if (today) hour = now.Date.AddHours(am).AddMinutes(minute) > now ? am : pm;
                else hour = h12 is >= 7 and <= 11 ? am : pm;
            }
        }

        if (hour >= 0)
        {
            r.HasTime = true;
            var day = date ?? now.Date;
            var at0 = day.AddHours(hour).AddMinutes(minute);
            if (date == null && at0 <= now) at0 = at0.AddDays(1);
            r.At = at0;
        }
        else if (date != null) r.At = date.Value.Date;

        r.Rest = Clean(s);
        return r;
    }

    // The next <dow> after `from`. "this X" may be today; "next X" is that weekday next calendar week (Monday-first).
    static DateTime NextDay(DateTime from, DayOfWeek dow, bool next, bool thisOne)
    {
        int ahead = ((int)dow - (int)from.DayOfWeek + 7) % 7;
        if (next)
        {
            int toMonday = ((int)DayOfWeek.Monday - (int)from.DayOfWeek + 7) % 7; if (toMonday == 0) toMonday = 7;
            int offset = ((int)dow + 6) % 7;   // Monday = 0
            return from.AddDays(toMonday + offset);
        }
        if (ahead == 0 && !thisOne) ahead = 7;
        return from.AddDays(ahead);
    }

    static string Clean(string s)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        s = Regex.Replace(s, @"^(?:(?:on|at|by|due|until|before|for|from)\s+)+", "", IC);
        s = Regex.Replace(s, @"(?:\s+(?:on|at|by|due|until|before|for|from|in|and))+$", "", IC);
        return s.Trim(' ', ',', ';', ':', '.', '-');
    }

    public static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}
