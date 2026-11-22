using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VowenLite;

// Roadmap 1.1: calendar feeds. Settings.Feeds holds .ics links (e.g. a school LMS calendar -> Subscribe) or local .ics paths. Every hour each feed
// is fetched and its events become tasks in the Settings.FeedList planner list: Item.Uid = the VEVENT UID, Item.Source = "feed:<short hash of url>",
// Item.Link = the event URL if any. Re-fetches update title/due of items it made; never touch items the user ticked done or deleted.
// A task only has a due DATE (Google Tasks stores no time), so a timed event keeps its time as "Due 11:59 PM" in the notes.
static class Feeds
{
    sealed class Ev
    {
        public string Uid = "", Summary = "", Desc = "", Url = "", Loc = "";
        public DateTime? Start;               // local time (date only when AllDay)
        public bool AllDay, HasTime, Repeats, Cancelled;
    }

    sealed class FeedState { public DateTime At { get; set; } public string ETag { get; set; } public string Modified { get; set; } }
    sealed class StateFile
    {
        public Dictionary<string, FeedState> Last { get; set; } = new();     // by url hash (the url itself may hold a secret token)
        public Dictionary<string, string> Known { get; set; } = new();       // source|uid -> "title|date" as last seen in the feed: never re-added, only updated when the FEED changes
    }

    const int PastDays = 1, AheadDays = 60, MaxBytes = 5 * 1024 * 1024;
    static string StatePath => Path.Combine(Paths.DataDir, "feeds-state.json");
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static int busy;

    static StateFile LoadState() { try { return JsonSerializer.Deserialize<StateFile>(File.ReadAllText(StatePath)) ?? new(); } catch { return new(); } }
    static void SaveState(StateFile st) { try { File.WriteAllText(StatePath, JsonSerializer.Serialize(st)); } catch { } }

    static string Norm(string url)
    {
        url = (url ?? "").Trim();
        return url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase) ? "https://" + url.Substring(9) : url;
    }
    static string Hash(string url) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Norm(url)))).Substring(0, 8).ToLowerInvariant();
    static string Src(string url) => "feed:" + Hash(url);
    static string Shown(string url)       // never print the whole link: it usually carries a private token
    {
        url = Norm(url);
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http")) return u.Host;
        return Path.GetFileName(url);
    }

    // ---------------- parsing (RFC 5545)

    static List<string> Unfold(string ics)
    {
        ics = Regex.Replace(ics.Replace("\r\n", "\n").Replace('\r', '\n'), @"\n[ \t]", "");
        return ics.Split('\n').Select(x => x.TrimEnd()).Where(x => x.Length > 0).ToList();
    }

    static string Unescape(string v)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] == '\\' && i + 1 < v.Length)
            {
                char c = v[++i];
                sb.Append(c is 'n' or 'N' ? '\n' : c);
            }
            else sb.Append(v[i]);
        }
        return sb.ToString();
    }

    // "NAME;P=v;P2=\"x:y\":value" -> name, params, value. The first colon outside quotes ends the name part.
    static bool Split(string line, out string name, out Dictionary<string, string> pars, out string value)
    {
        name = value = ""; pars = new(StringComparer.OrdinalIgnoreCase);
        bool q = false; int colon = -1;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') q = !q;
            else if (line[i] == ':' && !q) { colon = i; break; }
        }
        if (colon < 0) return false;
        value = line.Substring(colon + 1);
        var parts = new List<string>(); var cur = new StringBuilder(); q = false;
        foreach (char c in line.Substring(0, colon))
        {
            if (c == '"') q = !q;
            if (c == ';' && !q) { parts.Add(cur.ToString()); cur.Clear(); } else cur.Append(c);
        }
        parts.Add(cur.ToString());
        name = parts[0].ToUpperInvariant();
        foreach (var p in parts.Skip(1)) { int e = p.IndexOf('='); if (e > 0) pars[p.Substring(0, e)] = p.Substring(e + 1).Trim('"'); }
        return true;
    }

    // DTSTART value in all its forms -> local DateTime. VALUE=DATE / 8 digits = all day; "...Z" = UTC; TZID=... = that zone; otherwise floating (already local).
    static bool ParseDate(string v, Dictionary<string, string> pars, out DateTime local, out bool allDay)
    {
        local = default; allDay = false;
        v = v.Trim();
        var m = Regex.Match(v, @"^(\d{4})(\d{2})(\d{2})(?:T(\d{2})(\d{2})(\d{2})?(Z)?)?$", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        int H(int g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value) : 0;
        DateTime d;
        try { d = new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), H(4), H(5), H(6)); }
        catch { return false; }
        if (!m.Groups[4].Success || (pars.TryGetValue("VALUE", out var vv) && vv.Equals("DATE", StringComparison.OrdinalIgnoreCase))) { local = d.Date; allDay = true; return true; }
        if (m.Groups[7].Success) { local = DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime(); return true; }
        if (pars.TryGetValue("TZID", out var tz) && tz.Length > 0)
        {
            try { local = TimeZoneInfo.ConvertTime(TimeZoneInfo.ConvertTimeToUtc(d, TimeZoneInfo.FindSystemTimeZoneById(tz)), TimeZoneInfo.Local); return true; }
            catch { }                       // unknown zone name: treat as floating
        }
        local = d;
        return true;
    }

    static List<Ev> Parse(string ics)
    {
        var res = new List<Ev>();
        Ev cur = null; int depth = 0;       // depth: nested components inside the VEVENT (VALARM) are skipped
        foreach (var line in Unfold(ics ?? ""))
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) { cur = new Ev(); depth = 0; continue; }
            if (cur == null) continue;
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase)) { depth++; continue; }
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase)) { if (cur.Start != null && cur.Summary.Length > 0) res.Add(cur); cur = null; continue; }
            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase)) { depth--; continue; }
            if (depth > 0 || !Split(line, out string n, out var p, out string v)) continue;
            switch (n)
            {
                case "UID": cur.Uid = v.Trim(); break;
                case "SUMMARY": cur.Summary = Regex.Replace(Unescape(v), @"\s+", " ").Trim(); break;
                case "DESCRIPTION": cur.Desc = Unescape(v).Trim(); break;
                case "LOCATION": cur.Loc = Unescape(v).Trim(); break;
                case "URL": cur.Url = v.Trim(); break;
                case "STATUS": cur.Cancelled = v.Trim().Equals("CANCELLED", StringComparison.OrdinalIgnoreCase); break;
                case "RRULE": cur.Repeats = true; break;
                case "DTSTART": if (ParseDate(v, p, out var d, out bool ad)) { cur.Start = d; cur.AllDay = ad; cur.HasTime = !ad; } break;
            }
        }
        return res;
    }

    static string LinkOf(Ev e)
    {
        if (Regex.IsMatch(e.Url, @"^https?://", RegexOptions.IgnoreCase)) return e.Url;
        var m = Regex.Match(e.Desc, @"https?://[^\s<>""]+", RegexOptions.IgnoreCase);
        return m.Success ? m.Value.TrimEnd('.', ',', ';', ')', ']') : "";
    }

    // ---------------- merge into the planner

    sealed class Counts { public int Events, New, Updated, Recurring; }

    static string Fp(string title, DateTime date) => title + "|" + date.ToString("yyyy-MM-dd");

    static Counts Merge(string url, List<Ev> events, string list, StateFile st, DateTime now)
    {
        var c = new Counts();
        string src = Src(url);
        DateTime lo = now.Date.AddDays(-PastDays), hi = now.Date.AddDays(AheadDays);
        var batch = new List<(Ev e, DateTime due)>();
        var seenUid = new HashSet<string>();
        foreach (var e in events)
        {
            if (e.Cancelled) continue;
            if (e.Repeats) c.Recurring++;      // a repeating event is imported once (its first occurrence), never expanded
            var due = e.Start.Value.Date;
            if (due < lo || due > hi) continue;
            if (e.Uid.Length == 0) e.Uid = "nouid-" + Hash(e.Summary + "|" + e.Start.Value.ToString("s"));
            if (!seenUid.Add(e.Uid)) continue;
            batch.Add((e, due));
        }
        c.Events = batch.Count;
        if (batch.Count == 0) return c;
        bool touched = false;
        Planner.Edit(items =>
        {
            foreach (var (e, due) in batch)
            {
                string key = src + "|" + e.Uid, title = When.Cap(e.Summary), fp = Fp(title, due);
                var ex = items.Find(i => i.Kind == "task" && i.Source == src && i.Uid == e.Uid);
                if (ex != null)
                {
                    st.Known.TryGetValue(key, out string was);
                    st.Known[key] = fp;
                    if (ex.Done || ex.Deleted) continue;
                    // changed in the feed since we last looked (or no memory of it: compare with the task itself)
                    bool changed = was != null ? was != fp : Fp(ex.Title, ex.Start?.Date ?? DateTime.MinValue) != fp;
                    if (!changed) continue;
                    ex.Title = title; ex.Start = due; ex.Dirty = true; ex.Updated = DateTime.UtcNow;
                    c.Updated++; touched = true;
                    continue;
                }
                if (st.Known.ContainsKey(key)) continue;           // we made it once and it's gone (deleted): never resurrect
                st.Known[key] = fp;
                string k2 = Inbox.Key(title, due);
                if (items.Any(i => i.Kind == "task" && !i.Deleted && Inbox.Key(i.Title, i.Start) == k2)) continue;   // typed by hand already
                var notes = new StringBuilder();
                if (e.HasTime) notes.Append("Due ").Append(e.Start.Value.ToString("h:mm tt"));
                if (e.Loc.Length > 0) notes.Append(notes.Length > 0 ? " · " : "").Append(e.Loc);
                if (e.Desc.Length > 0) { if (notes.Length > 0) notes.Append('\n'); notes.Append(e.Desc.Length > 400 ? e.Desc.Substring(0, 400) + "…" : e.Desc); }
                if (e.Repeats) notes.Append(notes.Length > 0 ? "\n" : "").Append("(repeats; only the first date is imported)");
                items.Add(new Item
                {
                    Kind = "task", Title = title, Start = due, Notes = notes.ToString(), Cat = list ?? "", Source = src, Uid = e.Uid, Link = LinkOf(e),
                    Est = Habits.DefaultMinutes(Habits.Kind(title)), Dirty = true,
                });
                c.New++; touched = true;
            }
        });
        if (touched) GoogleSync.Kick();
        return c;
    }

    // ---------------- fetching

    static async Task<string> Fetch(string url, FeedState fs, bool conditional)
    {
        url = Norm(url);
        if (!Regex.IsMatch(url, @"^https?://", RegexOptions.IgnoreCase))
        {
            var fi = new FileInfo(url);
            if (!fi.Exists) throw new FileNotFoundException("no such file");
            if (fi.Length > MaxBytes) throw new IOException("feed is over 5 MB");
            return await File.ReadAllTextAsync(url);
        }
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (conditional && !string.IsNullOrEmpty(fs.ETag)) req.Headers.TryAddWithoutValidation("If-None-Match", fs.ETag);
        if (conditional && !string.IsNullOrEmpty(fs.Modified)) req.Headers.TryAddWithoutValidation("If-Modified-Since", fs.Modified);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotModified) return null;
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > MaxBytes) throw new IOException("feed is over 5 MB");
        fs.ETag = resp.Headers.ETag?.ToString();
        fs.Modified = resp.Content.Headers.LastModified?.ToString("R");
        using var s = await resp.Content.ReadAsStreamAsync();
        using var ms = new MemoryStream();
        var buf = new byte[81920]; int n;
        while ((n = await s.ReadAsync(buf)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxBytes) throw new IOException("feed is over 5 MB");
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // Fetches every feed that is due (all of them when force) and merges. One run at a time. Returns a one-line summary.
    static async Task<string> Refresh(Settings s, bool force)
    {
        if (s.Feeds.Count == 0) return "No calendar feeds yet";
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return "Feeds are already refreshing";
        var total = new Counts(); int failed = 0;
        try
        {
            var st = LoadState();
            foreach (var url in s.Feeds.ToList())
            {
                string h = Hash(url);
                if (!st.Last.TryGetValue(h, out var fs)) st.Last[h] = fs = new FeedState();
                if (!force && DateTime.Now - fs.At < TimeSpan.FromMinutes(55)) continue;
                try
                {
                    string text = await Fetch(url, fs, !force);
                    fs.At = DateTime.Now;
                    if (text == null) continue;                                    // 304: nothing changed
                    var c = Merge(url, Parse(text), s.FeedList, st, DateTime.Now);
                    total.Events += c.Events; total.New += c.New; total.Updated += c.Updated; total.Recurring += c.Recurring;
                }
                catch (Exception ex) { failed++; fs.At = DateTime.Now; Luna.Log("feeds: " + Shown(url) + " failed: " + ex.GetType().Name + " " + ex.Message); }
            }
            SaveState(st);
        }
        finally { Interlocked.Exchange(ref busy, 0); }
        string msg = $"{total.Events} events, {total.New} new, {total.Updated} updated" + (total.Recurring > 0 ? $", {total.Recurring} repeating (first date only)" : "") + (failed > 0 ? $", {failed} failed" : "");
        Luna.Log("feeds: " + msg);
        return msg;
    }

    public static void Tick(Settings s, DateTime now)
    {
        if (s.Feeds.Count == 0 || busy != 0) return;
        var st = LoadState();
        if (!s.Feeds.Any(u => !st.Last.TryGetValue(Hash(u), out var f) || now - f.At >= TimeSpan.FromMinutes(55))) return;
        _ = Task.Run(async () => { try { await Refresh(s, false); } catch (Exception ex) { Luna.Log("feeds: " + ex.Message); } });
    }

    // ---------------- voice / typed

    static readonly Regex AddRx = new(@"^(?:add|subscribe to|import)\s+(?:my |a |the )?(?:calendar |school |lms |ical |ics )*feed\s*(?:from |url |at |link |: )?\s*(?<u>(?:https?|webcal)://\S+|\S+\.ics)$", RegexOptions.IgnoreCase);
    static readonly Regex RefreshRx = new(@"^(?:refresh|sync|update|reload|re-?sync)(?: all)?(?: my| the)?(?: calendar| school)? feeds?$", RegexOptions.IgnoreCase);
    static readonly Regex ListRx = new(@"^(?:(?:what|which) (?:calendar )?feeds (?:do i have|am i subscribed to|are there|have i added)|(?:list|show)(?: me)?(?: my| the)?(?: calendar)? feeds)$", RegexOptions.IgnoreCase);
    static readonly Regex RemoveRx = new(@"^(?:remove|delete|unsubscribe from|drop) (?:calendar )?feed (?:number |no\.? |#)?(?<n>\d+)$", RegexOptions.IgnoreCase);

    static string RefreshWithCard(Settings s, string what)
    {
        int w = Cards.BeginWork(what);
        _ = Task.Run(async () =>
        {
            try { Cards.EndWork(w, "Feeds: " + await Refresh(s, true)); }
            catch (Exception ex) { Cards.EndWork(w, "Feeds failed: " + ex.Message, true); }
        });
        return "✓ Working on it…";
    }

    public static string TryLocal(string l, Settings s)
    {
        var m = AddRx.Match(l);
        if (m.Success)
        {
            string url = m.Groups["u"].Value;
            if (Luna.DryRun) return "DRY RUN: add calendar feed " + Shown(url);
            if (!Regex.IsMatch(url, @"^(?:https?|webcal)://", RegexOptions.IgnoreCase) && !File.Exists(url)) return "I can't find that file: " + url;
            if (s.Feeds.Any(f => Hash(f) == Hash(url))) return "✓ That feed is already added";
            s.Feeds.Add(url); s.Save();
            return RefreshWithCard(s, "add feed");
        }
        if (RefreshRx.IsMatch(l))
        {
            if (s.Feeds.Count == 0) return "You have no calendar feeds yet. Say \"add calendar feed\" and the link.";
            if (Luna.DryRun) return "DRY RUN: refresh " + s.Feeds.Count + " calendar feed(s)";
            return RefreshWithCard(s, "refresh feeds");
        }
        if (ListRx.IsMatch(l))
        {
            if (Luna.DryRun) return "DRY RUN: list " + s.Feeds.Count + " feed(s)";
            if (s.Feeds.Count == 0) return "You have no calendar feeds yet. Say \"add calendar feed\" and the link.";
            var st = LoadState();
            var sb = new StringBuilder("**Calendar feeds** (tasks go to the " + s.FeedList + " list)\n\n");
            for (int i = 0; i < s.Feeds.Count; i++)
            {
                string src = Src(s.Feeds[i]);
                int n = Planner.Live().Count(t => t.Source == src && !t.Done);
                string when = st.Last.TryGetValue(Hash(s.Feeds[i]), out var f) && f.At > DateTime.MinValue ? "checked " + f.At.ToString("MMM d h:mm tt") : "not fetched yet";
                sb.Append($"{i + 1}. {Shown(s.Feeds[i])} · {n} open · {when}\n");
            }
            return sb.ToString();
        }
        m = RemoveRx.Match(l);
        if (m.Success)
        {
            int n = int.Parse(m.Groups["n"].Value);
            if (n < 1 || n > s.Feeds.Count) return s.Feeds.Count == 0 ? "You have no calendar feeds." : $"There is no feed {n}. You have {s.Feeds.Count}.";
            if (Luna.DryRun) return "DRY RUN: remove feed " + n + " (" + Shown(s.Feeds[n - 1]) + ")";
            string gone = Shown(s.Feeds[n - 1]);
            s.Feeds.RemoveAt(n - 1); s.Save();
            return "✓ Removed feed " + n + " (" + gone + "). Tasks it already made stay in your planner.";
        }
        return null;
    }

    // ---------------- tests

    public static void SelfChecks(Action<bool, string> check)
    {
        string ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n"
            + "BEGIN:VEVENT\r\nUID:a1\r\nSUMMARY:Quiz 1\\, part 2\\; with a very long title that gets fo\r\n lded\r\nDESCRIPTION:Line one\\nLine two https://example.com/q1.\r\nDTSTART:20260105T235900Z\r\n"
            + "BEGIN:VALARM\r\nTRIGGER:-PT15M\r\nDESCRIPTION:alarm\r\nEND:VALARM\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:a2\r\nSUMMARY:Essay\r\nDTSTART;VALUE=DATE:20260110\r\nURL:https://example.com/e\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:a3\r\nSUMMARY:Lab\r\nDTSTART;TZID=UTC:20260111T120000\r\nRRULE:FREQ=WEEKLY\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:a4\r\nSUMMARY:Floating\r\nDTSTART:20260112T090000\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:a5\r\nSUMMARY:No start\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var ev = Parse(ics);
        check(ev.Count == 4, "feeds: five VEVENTs, one without a start is dropped (got " + ev.Count + ")");
        if (ev.Count != 4) return;
        check(ev[0].Summary == "Quiz 1, part 2; with a very long title that gets folded", "feeds: line unfolding and text escapes (" + ev[0].Summary + ")");
        check(ev[0].Desc == "Line one\nLine two https://example.com/q1.", "feeds: \\n in a description, alarm description ignored");
        check(LinkOf(ev[0]) == "https://example.com/q1", "feeds: first https link of the description");
        check(ev[0].HasTime && ev[0].Start == new DateTime(2026, 1, 5, 23, 59, 0, DateTimeKind.Utc).ToLocalTime(), "feeds: UTC time converted to local");
        check(ev[1].AllDay && ev[1].Start == new DateTime(2026, 1, 10) && LinkOf(ev[1]) == "https://example.com/e", "feeds: VALUE=DATE is all day, URL is the link");
        check(ev[2].Start == new DateTime(2026, 1, 11, 12, 0, 0, DateTimeKind.Utc).ToLocalTime() && ev[2].Repeats, "feeds: TZID zone converted, RRULE flagged");
        check(ev[3].Start == new DateTime(2026, 1, 12, 9, 0, 0) && ev[3].HasTime, "feeds: floating time stays as written");
        check(Hash("webcal://x.test/a.ics") == Hash("https://x.test/a.ics"), "feeds: webcal and https are the same feed");
    }

    public static int Test(string[] args)
    {
        int fails = 0;
        void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL " + what); } }
        string file = Path.Combine(Paths.DataDir, "feedtest.ics");
        string D(int days) => DateTime.Today.AddDays(days).ToString("yyyyMMdd");
        string Ics(string quizTitle) => "BEGIN:VCALENDAR\r\n"
            + $"BEGIN:VEVENT\r\nUID:ft-1\r\nSUMMARY:{quizTitle}\r\nDTSTART;VALUE=DATE:{D(3)}\r\nURL:https://example.com/quiz\r\nEND:VEVENT\r\n"
            + $"BEGIN:VEVENT\r\nUID:ft-2\r\nSUMMARY:Art 101 essay\r\nDTSTART:{D(5)}T235900\r\nEND:VEVENT\r\n"
            + $"BEGIN:VEVENT\r\nUID:ft-3\r\nSUMMARY:Typed already\r\nDTSTART;VALUE=DATE:{D(4)}\r\nEND:VEVENT\r\n"
            + $"BEGIN:VEVENT\r\nUID:ft-4\r\nSUMMARY:Far away\r\nDTSTART;VALUE=DATE:{D(200)}\r\nEND:VEVENT\r\n"
            + $"BEGIN:VEVENT\r\nUID:ft-5\r\nSUMMARY:Weekly lab\r\nDTSTART;VALUE=DATE:{D(2)}\r\nRRULE:FREQ=WEEKLY\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        File.WriteAllText(file, Ics("Quiz 1"));
        Planner.AddTask("Typed already", DateTime.Today.AddDays(4));
        var s = Settings.Load();
        s.Feeds = new() { file }; s.FeedList = "FeedTest"; s.Save();
        string Run() => Refresh(Settings.Load(), true).GetAwaiter().GetResult();
        List<Item> Mine() => Planner.Load().Where(i => i.Source == Src(file)).ToList();

        string r = Run();
        Check(r.StartsWith("4 events, 3 new, 0 updated"), "first import (" + r + ")");
        var mine = Mine();
        Check(mine.Count == 3 && mine.All(i => i.Cat == "FeedTest" && i.Kind == "task" && i.Est > 0), "three tasks in the feed list");
        var quiz = mine.FirstOrDefault(i => i.Uid == "ft-1");
        Check(quiz != null && quiz.Title == "Quiz 1" && quiz.Start == DateTime.Today.AddDays(3) && quiz.Link == "https://example.com/quiz", "quiz task: title, due, link");
        Check(mine.Any(i => i.Uid == "ft-2" && i.Notes.StartsWith("Due 11:59 PM")), "timed event keeps its time in the notes");
        Check(mine.Any(i => i.Uid == "ft-5" && i.Notes.Contains("repeats")), "repeating event imported once and noted");
        Check(!mine.Any(i => i.Uid == "ft-3"), "no duplicate of a task typed by hand");

        r = Run();
        Check(r.StartsWith("4 events, 0 new, 0 updated") && Mine().Count == 3, "re-import changes nothing (" + r + ")");

        File.WriteAllText(file, Ics("Quiz 1 (moved online)"));
        r = Run();
        Check(r.Contains("1 updated") && Mine().First(i => i.Uid == "ft-1").Title == "Quiz 1 (moved online)", "changed SUMMARY updates the task (" + r + ")");

        Planner.SetDone(quiz.Id, true);
        File.WriteAllText(file, Ics("Quiz 1 final"));
        r = Run();
        var q2 = Mine().First(i => i.Uid == "ft-1");
        Check(q2.Done && q2.Title == "Quiz 1 (moved online)" && Mine().Count == 3, "a done task stays done and untouched (" + r + ")");

        var essay = Mine().First(i => i.Uid == "ft-2");
        Planner.Remove(essay.Id);
        Planner.Edit(l => l.RemoveAll(i => i.Id == essay.Id));      // even once the tombstone is gone it must not come back
        Run();
        Check(!Mine().Any(i => i.Uid == "ft-2"), "a deleted task is not resurrected");

        Luna.DryRun = true;
        Check(TryLocal("add calendar feed https://example.com/cal.ics", Settings.Load())?.StartsWith("DRY RUN") == true, "add feed phrase (dry)");
        Check(TryLocal("add feed " + file, Settings.Load())?.StartsWith("DRY RUN") == true, "add feed with a file path (dry)");
        Check(TryLocal("refresh my feeds", Settings.Load())?.StartsWith("DRY RUN") == true, "refresh phrase");
        Check(TryLocal("what feeds do I have", Settings.Load())?.StartsWith("DRY RUN") == true, "list phrase");
        Check(TryLocal("remove feed 1", Settings.Load())?.StartsWith("DRY RUN") == true, "remove phrase");
        Check(TryLocal("add task email Sam friday", Settings.Load()) == null && TryLocal("open youtube", Settings.Load()) == null, "other commands are left alone");

        Planner.Edit(l => l.RemoveAll(i => i.Source == Src(file) || i.Title == "Typed already"));
        Console.WriteLine(fails == 0 ? "feedtest ok" : fails + " failed");
        return fails == 0 ? 0 : 1;
    }
}
