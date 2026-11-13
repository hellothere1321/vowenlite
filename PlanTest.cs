using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace VowenLite;

// `VowenLite.exe --plantest | Out-Host`: date parsing, repeat rules, voice commands and the whole Google sync against an
// in-process fake Google (task lists, calendar with repeating events, token endpoint). Uses a temp data dir, never touches
// your real planner or tokens.
static class PlanTest
{
    static int fails;
    static void Check(bool ok, string what, string detail = "") { Console.WriteLine((ok ? "PASS " : "FAIL ") + what + (ok || detail == "" ? "" : "  -> " + detail)); if (!ok) fails++; }

    sealed class Fake
    {
        public readonly Dictionary<string, (string Title, List<JsonObject> Items)> Lists = new() { ["def"] = ("My Tasks", new()) };
        public readonly List<JsonObject> Events = new();
        public readonly HashSet<string> GoneInstances = new();   // "<master>_<yyyyMMdd>" occurrences deleted on the "phone"
        public List<JsonObject> Tasks => Lists["def"].Items;
        public int Port; HttpListener hl;
        int ids;
        public Fake()
        {
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); tcp.Start(); Port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
            hl = new HttpListener(); hl.Prefixes.Add($"http://127.0.0.1:{Port}/"); hl.Start();
            new Thread(Loop) { IsBackground = true }.Start();
        }
        void Loop()
        {
            while (true)
            {
                HttpListenerContext c;
                try { c = hl.GetContext(); } catch { return; }
                string path = c.Request.Url.AbsolutePath, body = new StreamReader(c.Request.InputStream).ReadToEnd();
                string method = c.Request.HttpMethod, query = c.Request.Url.Query;
                JsonNode reply = null; int code = 200;
                lock (this)
                {
                    try
                    {
                        if (path == "/token") reply = new JsonObject { ["access_token"] = "fake", ["expires_in"] = 3600 };
                        else if (path == "/tasks/v1/users/@me/lists")
                        {
                            if (method == "POST") { string id = "L" + (++ids); Lists[id] = ((string)JsonNode.Parse(body)["title"], new()); reply = new JsonObject { ["id"] = id, ["title"] = Lists[id].Title }; }
                            else reply = new JsonObject { ["items"] = new JsonArray(Lists.Select(kv => (JsonNode)new JsonObject { ["id"] = kv.Key, ["title"] = kv.Value.Title }).ToArray()) };
                        }
                        else if (path == "/tasks/v1/users/@me/lists/@default") reply = new JsonObject { ["id"] = "def", ["title"] = "My Tasks" };
                        else if (path.StartsWith("/tasks/v1/lists/"))
                        {
                            var parts = path.Substring("/tasks/v1/lists/".Length).Split('/');
                            string lid = parts[0] == "@default" ? "def" : parts[0];
                            if (!Lists.ContainsKey(lid)) code = 404;
                            else reply = Crud(Lists[lid].Items, parts.Length > 2 ? parts[2] : "", method, body, "tasks", query, ref code);
                        }
                        else if (path.StartsWith("/calendar/v3/calendars/primary/events"))
                            reply = Crud(Events, path.Substring("/calendar/v3/calendars/primary/events".Length).Trim('/'), method, body, "events", query, ref code);
                        else code = 404;
                    }
                    catch (Exception ex) { code = 500; reply = new JsonObject { ["error"] = new JsonObject { ["message"] = ex.Message } }; }
                }
                c.Response.StatusCode = code;
                var bytes = Encoding.UTF8.GetBytes(reply?.ToJsonString() ?? "");
                c.Response.ContentType = "application/json";
                c.Response.OutputStream.Write(bytes); c.Response.Close();
            }
        }
        JsonNode Crud(List<JsonObject> store, string id, string method, string body, string kind, string query, ref int code)
        {
            if (method == "GET" && id == "")
            {
                var items = new List<JsonNode>();
                foreach (var o in store)
                {
                    if (kind == "events" && (string)o["status"] == "cancelled") continue;
                    if (kind == "events" && o["recurrence"] != null && query.Contains("singleEvents=true")) { items.AddRange(Instances(o)); continue; }
                    items.Add(JsonNode.Parse(o.ToJsonString()));
                }
                return new JsonObject { ["items"] = new JsonArray(items.ToArray()) };
            }
            if (method == "POST") { var o = JsonNode.Parse(body).AsObject(); o["id"] = (kind == "tasks" ? "t" : "e") + (++ids); o["updated"] = DateTime.UtcNow.ToString("o"); store.Add(o); return JsonNode.Parse(o.ToJsonString()); }
            var x = store.FirstOrDefault(o => (string)o["id"] == id);
            if (x == null) { code = 404; return new JsonObject { ["error"] = new JsonObject { ["message"] = "not found" } }; }
            if (method == "GET") return JsonNode.Parse(x.ToJsonString());
            if (method == "PATCH") { foreach (var kv in JsonNode.Parse(body).AsObject().ToList()) x[kv.Key] = kv.Value?.DeepClone(); x["updated"] = DateTime.UtcNow.ToString("o"); return JsonNode.Parse(x.ToJsonString()); }
            if (method == "DELETE") { store.Remove(x); return null; }
            code = 405; return null;
        }
        // what Google returns for singleEvents=true: one entry per occurrence in the next 90 days
        IEnumerable<JsonNode> Instances(JsonObject master)
        {
            string rline = ((JsonArray)master["recurrence"]).Select(n => (string)n).First(s => s.StartsWith("RRULE:"));
            var rule = Rule.Parse(rline);
            var start = DateTimeOffset.Parse((string)master["start"]["dateTime"]);
            var end = DateTimeOffset.Parse((string)master["end"]["dateTime"]);
            var ex = ((JsonArray)master["recurrence"]).Select(n => (string)n).Where(s => s.StartsWith("EXDATE")).Select(s => s.Substring(s.LastIndexOf(':') + 1)).ToHashSet();
            foreach (var st in rule.Expand(start.LocalDateTime, DateTime.Today.AddDays(90)))
            {
                string key = st.ToString("yyyyMMdd'T'HHmmss");
                if (ex.Contains(key) || GoneInstances.Contains((string)master["id"] + "_" + st.ToString("yyyyMMdd"))) continue;
                var s = new DateTimeOffset(st, start.Offset); var e = s + (end - start);
                yield return new JsonObject
                {
                    ["id"] = (string)master["id"] + "_" + st.ToString("yyyyMMdd"), ["recurringEventId"] = (string)master["id"], ["summary"] = (string)master["summary"], ["status"] = "confirmed",
                    ["updated"] = (string)master["updated"], ["start"] = new JsonObject { ["dateTime"] = s.ToString("o") }, ["end"] = new JsonObject { ["dateTime"] = e.ToString("o") },
                    ["originalStartTime"] = new JsonObject { ["dateTime"] = s.ToString("o") }
                };
            }
        }
    }

    public static int Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vowenlite-plantest-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(dir);
        var fake = new Fake();
        Environment.SetEnvironmentVariable("VOWENLITE_DATA", dir);
        Environment.SetEnvironmentVariable("VOWENLITE_GOOGLE_BASE", $"http://127.0.0.1:{fake.Port}");
        File.WriteAllText(Path.Combine(dir, "google-client.json"), "{\"installed\":{\"client_id\":\"id\",\"client_secret\":\"s\"}}");
        File.WriteAllText(Path.Combine(dir, "google-token.json"), "{\"RefreshToken\":\"r\",\"AccessToken\":\"\",\"Expires\":\"2000-01-01T00:00:00Z\"}");

        // ---- date parsing (now = Wed 2026-11-04 10:00)
        var now = new DateTime(2026, 11, 4, 10, 0, 0);
        When.Result P(string t, bool dur = false) => When.Parse(t, now, dur);
        var r = P("dentist tomorrow at 3pm for an hour", true);
        Check(r.At == new DateTime(2026, 11, 5, 15, 0, 0) && r.Rest == "dentist" && r.Duration == TimeSpan.FromHours(1), "tomorrow 3pm + duration", r.At + " | " + r.Rest + " | " + r.Duration);
        r = P("Sponsorship calls 830-9pm Sunday Biweekly", true); Check(r.At != null && r.At.Value.TimeOfDay == new TimeSpan(20, 30, 0) && r.Duration == TimeSpan.FromMinutes(30) && r.Rest.StartsWith("Sponsorship calls"), "time range 830-9pm", r.At + " | " + r.Rest + " | " + r.Duration);
        r = P("class 11-1pm tomorrow", true); Check(r.At != null && r.At.Value.TimeOfDay == new TimeSpan(11, 0, 0) && r.Duration == TimeSpan.FromHours(2), "time range 11-1pm", r.At + " | " + r.Duration);
        r = P("gym 6 to 7:30pm", true); Check(r.At != null && r.At.Value.Hour == 18 && r.Duration == TimeSpan.FromMinutes(90), "time range 6 to 7:30pm", r.At + " | " + r.Duration);
        r = P("study group 5:30-7:30 tomorrow", true); Check(r.At != null && r.At.Value.TimeOfDay == new TimeSpan(17, 30, 0) && r.Duration == TimeSpan.FromHours(2), "time range 5:30-7:30", r.At + " | " + r.Rest + " | " + r.Duration);
        r = P("lab 17:00-19:30 friday", true); Check(r.At != null && r.At.Value.Hour == 17 && r.Duration == TimeSpan.FromMinutes(150), "time range 24h", r.At + " | " + r.Duration);
        r = P("call 9-10:30am monday", true); Check(r.At != null && r.At.Value.Hour == 9 && r.Duration == TimeSpan.FromMinutes(90), "time range 9-10:30am", r.At + " | " + r.Duration);
        r = P("email Sam by friday"); Check(r.At == new DateTime(2026, 11, 6) && !r.HasTime && r.Rest == "email Sam", "by friday", r.At + " | " + r.Rest);
        r = P("call mom on nov 12th at 5:30 pm"); Check(r.At == new DateTime(2026, 11, 12, 17, 30, 0) && r.Rest == "call mom", "date + 5:30 pm", r.At + " | " + r.Rest);
        r = P("stretch in 20 minutes"); Check(r.Relative && r.At == now.AddMinutes(20) && r.Rest == "stretch", "relative minutes", r.At + " | " + r.Rest);
        r = P("pay rent in 3 days"); Check(r.At == new DateTime(2026, 11, 7) && !r.Relative, "in 3 days", r.At + "");
        r = P("gym at 5"); Check(r.At == new DateTime(2026, 11, 4, 17, 0, 0) && r.Rest == "gym", "at 5 (pm, later today)", r.At + "");
        r = P("run tonight"); Check(r.At == new DateTime(2026, 11, 4, 19, 0, 0), "tonight = 7 pm", r.At + "");
        r = P("standup next monday at 9am"); Check(r.At == new DateTime(2026, 11, 9, 9, 0, 0), "next monday 9am", r.At + "");
        r = P("buy milk"); Check(r.At == null && r.Rest == "buy milk", "no date");
        r = P("dentist at 9 am"); Check(r.At == new DateTime(2026, 11, 5, 9, 0, 0), "9 am already passed -> tomorrow", r.At + "");
        r = P("exam on 2026-12-03 at 14:30"); Check(r.At == new DateTime(2026, 12, 3, 14, 30, 0) && r.Rest == "exam", "ISO date + 24 h time", r.At + " | " + r.Rest);

        // ---- repeat rules
        var rule = Rule.FromText("gym every monday and thursday at 6pm", now, out string rest);
        Check(rule != null && rule.Freq == "WEEKLY" && rule.Days.Count == 2 && rule.Days.Contains(DayOfWeek.Monday) && rule.Days.Contains(DayOfWeek.Thursday) && rest.StartsWith("gym") && !rest.Contains("every"), "rule: every monday and thursday", rule?.ToRrule() + " | " + rest);
        rule = Rule.FromText("standup every weekday", now, out rest); Check(rule != null && rule.Days.Count == 5 && rest == "standup", "rule: every weekday", rule?.ToRrule() + " | " + rest);
        rule = Rule.FromText("call grandma every other sunday", now, out rest); Check(rule != null && rule.Interval == 2 && rule.Days.Single() == DayOfWeek.Sunday, "rule: every other sunday", rule?.ToRrule());
        rule = Rule.FromText("water plants biweekly", now, out rest); Check(rule != null && rule.Freq == "WEEKLY" && rule.Interval == 2, "rule: biweekly", rule?.ToRrule());
        rule = Rule.FromText("pay rent monthly on the 1st", now, out rest); Check(rule != null && rule.Freq == "MONTHLY" && rule.MonthDay == 1 && rest == "pay rent", "rule: monthly on the 1st", rule?.ToRrule() + " | " + rest);
        rule = Rule.FromText("run every 3 days for 5 times", now, out rest); Check(rule != null && rule.Interval == 3 && rule.Count == 5, "rule: every 3 days, 5 times", rule?.ToRrule());
        rule = Rule.FromText("class every tuesday until dec 15", now, out rest); Check(rule != null && rule.Until == new DateTime(2026, 12, 15), "rule: until date", rule?.ToRrule());
        rule = Rule.FromText("buy milk", now, out rest); Check(rule == null, "rule: none in plain text");
        var wk = Rule.Parse("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=4");
        var occ = wk.Expand(new DateTime(2026, 11, 2, 9, 0, 0), new DateTime(2027, 1, 1)).ToList();
        Check(occ.Count == 4 && occ[0].Day == 2 && occ[1].Day == 4 && occ[2].Day == 16 && occ[3].Day == 18, "rule: expand biweekly Mon/Wed x4", string.Join(",", occ.Select(o => o.ToString("MMdd"))));
        Check(wk.Describe(new DateTime(2026, 11, 2)) == "Every other week on Mon, Wed, 4 times", "rule: describe", wk.Describe(new DateTime(2026, 11, 2)));
        Check(Rule.Parse("FREQ=MONTHLY;BYDAY=2TU") == null, "rule: unsupported rrule is refused");
        var p = Planner.Parse("dentist every other friday at 3pm for 1 hour in my health list !!", now);
        Check(p.Kind == "event" && p.Cat == "Health" && p.Prio == 2 && p.Repeat?.Interval == 2 && p.Start == new DateTime(2026, 11, 6, 15, 0, 0) && p.End == new DateTime(2026, 11, 6, 16, 0, 0) && p.Title == "Dentist", "parse: full phrase", $"{p.Kind} {p.Cat} {p.Prio} {p.Start} {p.End} {p.Title}");
        p = Planner.Parse("lab report friday #school", now); Check(p.Kind == "task" && p.Cat == "School" && p.Start == new DateTime(2026, 11, 6), "parse: task with #list", $"{p.Kind} {p.Cat} {p.Start}");
        p = Planner.Parse("water plants every sunday", now); Check(p.Kind == "task" && p.Start == new DateTime(2026, 11, 8) && p.Repeat != null, "parse: repeating task starts on the first matching day", $"{p.Start}");

        // ---- voice
        Luna.DryRun = false;
        string v;
        v = Planner.TryLocal("add task buy milk tomorrow"); Check(v != null && v.StartsWith("✓ Task added · Buy milk"), "voice: add task", v);
        v = Planner.TryLocal("add eggs to my to do list"); Check(v != null && v.Contains("Eggs"), "voice: add X to my list", v);
        v = Planner.TryLocal("what are my tasks"); Check(v != null && v.Contains("Buy milk") && v.Contains("Eggs"), "voice: list tasks", v);
        v = Planner.TryLocal("add event dentist friday at 3pm for 2 hours"); Check(v != null && v.StartsWith("✓ Event added · Dentist"), "voice: add event", v);
        v = Planner.TryLocal("what's on my calendar this week"); Check(v != null && v.Contains("Dentist"), "voice: calendar", v);
        v = Planner.TryLocal("remind me friday at 8am to call grandma"); Check(v != null && v.StartsWith("✓ Reminder set"), "voice: timed reminder", v);
        v = Planner.TryLocal("remind me in 10 minutes to stretch"); Check(v == null, "voice: relative reminder left to timers", v);
        v = Planner.TryLocal("remind me every 20 minutes to stretch"); Check(v == null, "voice: every N minutes left to timers", v);
        v = Planner.TryLocal("mark eggs as done"); Check(v != null && v.StartsWith("✓ Done · Eggs"), "voice: complete", v);
        v = Planner.TryLocal("complete this sentence for me"); Check(v == null, "voice: unrelated 'complete' passes through", v);
        v = Planner.TryLocal("what's next"); Check(v != null, "voice: what's next", v);
        v = Planner.TryLocal("schedule piano every tuesday and thursday at 5pm for 45 minutes"); Check(v != null && v.StartsWith("✓ Event added · Piano") && v.Contains("Every"), "voice: repeating event", v);
        v = Planner.TryLocal("add weekly event team sync monday at 9am"); Check(v != null && v.Contains("Every Monday"), "voice: 'weekly event'", v);
        v = Planner.TryLocal("add task water the plants every sunday"); Check(v != null && v.Contains("Every Sunday"), "voice: repeating task", v);
        v = Planner.TryLocal("add task finish lab report friday in my school list high priority"); Check(v != null && v.Contains("School") && v.Contains("high priority"), "voice: task with list + priority", v);
        v = Planner.TryLocal("add milk to my shopping list"); Check(v != null && v.Contains("Shopping"), "voice: add to a named list", v);
        v = Planner.TryLocal("what's on my school list"); Check(v != null && v.Contains("lab report", StringComparison.OrdinalIgnoreCase), "voice: show a list", v);
        v = Planner.TryLocal("move dentist to saturday at 4pm"); Check(v != null && v.StartsWith("✓ Moved · Dentist"), "voice: reschedule", v);
        v = Planner.TryLocal("push the lab report to tomorrow"); Check(v != null && v.StartsWith("✓ Moved · Finish lab report"), "voice: push a task", v);
        v = Planner.TryLocal("make the lab report medium priority"); Check(v != null && v.Contains("medium priority"), "voice: priority", v);
        v = Planner.TryLocal("when is my dentist appointment"); Check(v != null && v.Contains("Dentist"), "voice: when is X", v);
        v = Planner.TryLocal("skip piano"); Check(v != null && v.StartsWith("✓ Skipped Piano"), "voice: skip next occurrence", v);
        v = Planner.TryLocal("what's my day"); Check(v != null, "voice: my day", v);
        v = Planner.TryLocal("open the planner"); Check(v == "" || v != null, "voice: open planner handled", v);
        v = Planner.TryLocal("move this window to the other monitor"); Check(v == null, "voice: window move is not claimed", v);
        v = Planner.TryLocal("skip this song"); Check(v == null, "voice: skip song is not claimed", v);
        // repeating task: ticking it off brings it back on its next date
        var water = Planner.Live().First(i => i.Title.StartsWith("Water the plants"));
        var due0 = water.Start;
        Planner.SetDone(water.Id, true);
        var water2 = Planner.Live().First(i => i.Id == water.Id);
        Check(!water2.Done && water2.Start > due0, "repeating task comes back on its next date", due0 + " -> " + water2.Start);
        Check(Planner.TryLocal("delete the task water the plants")?.Contains("all of them") == true, "voice: delete repeating task");
        Check(Planner.Occurrences(DateTime.Today, DateTime.Today.AddDays(21)).Count(o => o.Item.Title == "Piano") >= 3, "repeating event expands", "");   // 21 days: late on a Tuesday the first one is already past and one more is skipped above

        // ---- "planner" without Luna
        Check(Planner.TryDirect("Planner.", out string pr) && pr == "", "direct: bare word opens", pr);
        Check(Planner.TryDirect("Planner, add dentist friday at 3", out pr) && pr == "add dentist friday at 3", "direct: planner, ...", pr);
        Check(Planner.TryDirect("Hey planner what's on tomorrow", out pr) && pr == "what's on tomorrow", "direct: lead word without comma", pr);
        Check(!Planner.TryDirect("Planner is a nice word for this", out pr), "direct: ordinary sentence still typed");
        Check(!Planner.TryDirect("the planner was late", out pr), "direct: sentence with 'the planner' still typed");
        Check(Planner.TryDirect("Planter, add eggs", out pr), "direct: mis-heard 'planter,' with a comma");
        Check(!Planner.TryDirect("Planter", out pr), "direct: bare 'planter' is typed");
        Check(Planner.TryDirect("Plan her, add eggs", out pr) && pr == "add eggs", "direct: 'plan her,'", pr);
        Check(Planner.TryDirect("Blanner add dentist friday", out pr) && pr == "add dentist friday", "direct: 'blanner' + command word", pr);
        Check(Planner.TryDirect("Plana, open my week", out pr), "direct: 'plana,'", pr);
        Check(Planner.TryDirect("Plannar open", out pr) && pr == "open", "direct: 'plannar open'", pr);
        Check(Planner.TryDirect("Planners show tasks", out pr), "direct: 'planners show tasks'", pr);
        Check(!Planner.TryDirect("Plant, water the garden", out pr), "direct: 'plant,' is typed");
        Check(!Planner.TryDirect("Planet add nothing", out pr), "direct: 'planet' is typed");
        Check(!Planner.TryDirect("Planning this week is hard", out pr), "direct: 'planning ...' is typed");
        bool dry = Luna.DryRun; Luna.DryRun = true;
        v = Planner.TryLocal("open planer"); Check(v != null && v.StartsWith("DRY RUN: open the Plan window"), "voice: 'open planer'", v);
        v = Planner.TryLocal("open plan her"); Check(v != null && v.StartsWith("DRY RUN: open the Plan window"), "voice: 'open plan her'", v);
        Luna.DryRun = dry;
        v = Planner.Direct("call mom sunday at 5"); Check(v != null && v.StartsWith("✓ Event added · Call mom"), "direct: bare text with a time becomes an event", v);
        v = Planner.Direct("buy stamps"); Check(v != null && v.StartsWith("✓ Task added · Buy stamps"), "direct: bare text becomes a task", v);
        v = Planner.Hook("add task hook task 2026-12-01 in school list", out string prob); Check(prob == null && v != null && v.Contains("Hook task"), "PLAN hook", v + prob);
        v = Planner.Hook("flibbertigibbet the zorp", out prob); Check(prob != null, "PLAN hook: nonsense is reported");
        Check(Planner.PromptBlock("what should I do first in my schedule").Contains("task"), "prompt block lists tasks for plan questions");
        Check(Planner.PromptBlock("tell me a joke") == "", "prompt block skipped for other requests");

        // ---- sync (fake Google already has a task and an event made "on the phone", plus a phone-made list)
        fake.Tasks.Add(new JsonObject { ["id"] = "pt1", ["title"] = "Phone task", ["status"] = "needsAction", ["due"] = DateTime.Today.AddDays(2).ToString("yyyy-MM-dd") + "T00:00:00.000Z", ["updated"] = DateTime.UtcNow.ToString("o") });
        lock (fake) fake.Lists["PL"] = ("Gym stuff", new() { new JsonObject { ["id"] = "pt2", ["title"] = "Buy shaker", ["status"] = "needsAction", ["updated"] = DateTime.UtcNow.ToString("o") } });
        fake.Events.Add(new JsonObject
        {
            ["id"] = "pe1", ["summary"] = "Phone meeting", ["status"] = "confirmed", ["updated"] = DateTime.UtcNow.ToString("o"),
            ["start"] = new JsonObject { ["dateTime"] = new DateTimeOffset(DateTime.Today.AddDays(2).AddHours(10)).ToString("o") },
            ["end"] = new JsonObject { ["dateTime"] = new DateTimeOffset(DateTime.Today.AddDays(2).AddHours(11)).ToString("o") },
            ["reminders"] = new JsonObject { ["useDefault"] = false, ["overrides"] = new JsonArray(new JsonObject { ["method"] = "popup", ["minutes"] = 30 }) }
        });
        string st = GoogleSync.SyncNowAsync().GetAwaiter().GetResult();
        Check(st.StartsWith("Synced"), "sync ran", st);
        lock (fake)
        {
            Check(fake.Tasks.Any(t => (string)t["title"] == "Buy milk" && (string)t["due"] == DateTime.Today.AddDays(1).ToString("yyyy-MM-dd") + "T00:00:00.000Z"), "task pushed to Google with due date");
            Check(fake.Tasks.Any(t => (string)t["title"] == "Eggs" && (string)t["status"] == "completed"), "completed task pushed");
            var school = fake.Lists.Values.FirstOrDefault(l => l.Title == "School");
            Check(school.Items != null && school.Items.Any(t => ((string)t["title"]).StartsWith("Finish lab report")), "task in a planner list went to a Google task list of that name");
            Check(fake.Lists.Values.Any(l => l.Title == "Shopping" && l.Items.Any(t => (string)t["title"] == "Milk")), "new list created on Google for 'Shopping'");
            Check(fake.Events.Any(e => (string)e["summary"] == "Dentist" && e["start"]["dateTime"] != null), "event pushed to Google");
            Check(fake.Events.Any(e => (string)e["summary"] == "Call grandma" && (int)e["reminders"]["overrides"][0]["minutes"] == 0), "reminder event pushed with 0 min popup");
            var piano = fake.Events.FirstOrDefault(e => (string)e["summary"] == "Piano");
            Check(piano != null && piano["recurrence"] is JsonArray ra && ((string)ra[0]).StartsWith("RRULE:FREQ=WEEKLY") && ((string)ra[0]).Contains("BYDAY=TU,TH") && piano["start"]["timeZone"] != null, "repeating event pushed as RRULE with a time zone", piano?["recurrence"]?.ToJsonString());
            Check(piano != null && piano["recurrence"].AsArray().Any(n => ((string)n).StartsWith("EXDATE")), "skipped occurrence pushed as EXDATE");
            Check(fake.Events.Where(e => (string)e["id"] != "pe1").All(e => e["extendedProperties"]?["private"]?["vlCat"] != null), "list + priority kept in extended properties");
        }
        var all = Planner.Live();
        Check(all.Any(i => i.Title == "Phone task" && !i.Dirty && i.Cat == ""), "phone task pulled into the default list");
        Check(all.Any(i => i.Title == "Buy shaker" && i.Cat == "Gym stuff"), "phone list pulled as a planner list", string.Join(",", all.Where(i => i.Title == "Buy shaker").Select(i => i.Cat)));
        var pm = all.FirstOrDefault(i => i.Title == "Phone meeting");
        Check(pm != null && pm.Alert == 30 && pm.Start == DateTime.Today.AddDays(2).AddHours(10), "phone event pulled with its alert", pm?.Start + " alert " + pm?.Alert);
        Check(all.Count(i => i.Title == "Piano") == 1, "our repeating event is not duplicated as phone occurrences", all.Count(i => i.Title == "Piano") + "");
        Check(all.All(i => !i.Dirty && i.GId != null), "nothing left dirty", string.Join(",", all.Where(i => i.Dirty || i.GId == null).Select(i => i.Title)));

        // delete + complete on the PC, delete on the phone, one occurrence deleted on the phone, move a task between lists
        Planner.Remove(all.First(i => i.Title == "Dentist").Id);
        Planner.SetDone(all.First(i => i.Title == "Phone task").Id, true);
        Planner.Update(all.First(i => i.Title == "Eggs").Id, x => x.Cat = "School");
        lock (fake) fake.Events.RemoveAll(e => (string)e["summary"] == "Phone meeting");
        lock (fake) fake.Tasks.First(t => (string)t["title"] == "Buy milk")["deleted"] = true;
        var pianoId = lock2(fake, () => (string)fake.Events.First(e => (string)e["summary"] == "Piano")["id"]);
        var nextPiano = Planner.Occurrences(DateTime.Now, DateTime.Today.AddDays(30)).First(o => o.Item.Title == "Piano").Start;
        lock (fake) fake.GoneInstances.Add(pianoId + "_" + nextPiano.ToString("yyyyMMdd"));
        Thread.Sleep(2500);   // the Kick() debounce may be mid-sync; wait it out, then sync again
        GoogleSync.SyncNowAsync().GetAwaiter().GetResult();
        lock (fake)
        {
            Check(!fake.Events.Any(e => (string)e["summary"] == "Dentist"), "PC delete reached Google");
            Check(fake.Tasks.Any(t => (string)t["title"] == "Phone task" && (string)t["status"] == "completed"), "PC completion reached Google");
            Check(!fake.Tasks.Any(t => (string)t["title"] == "Eggs") && fake.Lists.Values.First(l => l.Title == "School").Items.Any(t => (string)t["title"] == "Eggs"), "task moved to another list on Google");
        }
        all = Planner.Live();
        Check(!all.Any(i => i.Title == "Phone meeting"), "phone-side event delete reached the PC");
        Check(!all.Any(i => i.Title == "Buy milk"), "phone-side task delete reached the PC");
        var pianoItem = all.First(i => i.Title == "Piano");
        Check(pianoItem.Skip.Contains(nextPiano), "an occurrence deleted on the phone is skipped on the PC", string.Join(",", pianoItem.Skip));
        Check(Planner.Occurrences(nextPiano.Date, nextPiano.Date.AddDays(1)).All(o => o.Item.Title != "Piano"), "…and no longer shows");
        lock (fake) fake.Events.RemoveAll(e => (string)e["summary"] == "Piano");
        GoogleSync.SyncNowAsync().GetAwaiter().GetResult();
        Check(!Planner.Live().Any(i => i.Title == "Piano"), "deleting a repeating event on the phone removes it here");

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        try { Directory.Delete(dir, true); } catch { }
        return fails == 0 ? 0 : 1;
    }

    static T lock2<T>(object o, Func<T> f) { lock (o) return f(); }
}
