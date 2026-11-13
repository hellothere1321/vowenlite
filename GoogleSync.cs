using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VowenLite;

// Two-way sync of planner.json with the user's Google Tasks (default list) and Google Calendar (primary).
// Google Assistant / Gemini and the Google Calendar + Tasks apps on the phone read and write the same data, so this is
// the whole phone link: no IP, no pairing, works on any network. OAuth "desktop" flow with PKCE; the refresh token
// keeps it signed in. Free (Google Tasks / Calendar APIs have no cost at this volume). See PLANNER.md for setup.
static class GoogleSync
{
    const string Scopes = "https://www.googleapis.com/auth/tasks https://www.googleapis.com/auth/calendar.events";
    static readonly string Base = Environment.GetEnvironmentVariable("VOWENLITE_GOOGLE_BASE");   // tests point this at a fake server
    static string TasksApi => Base != null ? Base + "/tasks/v1" : "https://tasks.googleapis.com/tasks/v1";
    static string CalApi => Base != null ? Base + "/calendar/v3" : "https://www.googleapis.com/calendar/v3";
    static string TokenUrl => Base != null ? Base + "/token" : "https://oauth2.googleapis.com/token";

    static string ClientFile => Path.Combine(Paths.DataDir, "google-client.json");
    static string TokenFile => Path.Combine(Paths.DataDir, "google-token.json");
    // IPv4 first: .NET tries the IPv6 address and waits out the whole timeout when IPv6 is black-holed (curl falls back quickly).
    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(8),
        ConnectCallback = async (ctx, ct) =>
        {
            var addrs = await System.Net.Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            Exception last = null;
            foreach (var ip in addrs.OrderBy(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1))
            {
                var sock = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(4000);
                    await sock.ConnectAsync(new System.Net.IPEndPoint(ip, ctx.DnsEndPoint.Port), cts.Token);
                    return new System.Net.Sockets.NetworkStream(sock, true);
                }
                catch (Exception ex) { sock.Dispose(); last = ex; }
            }
            throw last ?? new System.Net.Sockets.SocketException();
        }
    }) { Timeout = TimeSpan.FromSeconds(30) };

    sealed class Tok
    {
        public string RefreshToken { get; set; }
        public string AccessToken { get; set; }
        public DateTime Expires { get; set; }          // UTC
        public DateTime? TasksSince { get; set; }      // UTC: updatedMin for the next incremental task pull
    }
    static Tok tok;

    public static string Status { get; private set; } = "Not connected";
    public static DateTime? LastSync { get; private set; }
    public static bool Connected => File.Exists(TokenFile);
    public static bool HasClient => File.Exists(ClientFile);

    // ---------------- sign in

    sealed class Client { public string Id, Secret; }

    static Client LoadClient()
    {
        try
        {
            if (!File.Exists(ClientFile)) return null;
            var root = JsonNode.Parse(File.ReadAllText(ClientFile));
            var n = root["installed"] ?? root["web"] ?? root;
            string id = (string)n["client_id"], secret = (string)n["client_secret"];
            return string.IsNullOrEmpty(id) ? null : new Client { Id = id, Secret = secret ?? "" };
        }
        catch { return null; }
    }

    // `--gauth <client_secret.json>` copies the downloaded OAuth client in, then signs in.
    public static void ImportClient(string path) { File.Copy(path, ClientFile, true); }

    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static async Task<string> SignInAsync()
    {
        var c = LoadClient();
        if (c == null) return "Missing google-client.json in " + Paths.DataDir + " (see PLANNER.md, step 2).";
        var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start(); int port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
        string redirect = $"http://127.0.0.1:{port}/";
        using var hl = new HttpListener();
        hl.Prefixes.Add(redirect);
        hl.Start();
        string verifier = B64(RandomNumberGenerator.GetBytes(48)), state = B64(RandomNumberGenerator.GetBytes(12));
        string challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string url = "https://accounts.google.com/o/oauth2/v2/auth?response_type=code&access_type=offline&prompt=consent"
            + "&client_id=" + Uri.EscapeDataString(c.Id) + "&redirect_uri=" + Uri.EscapeDataString(redirect) + "&scope=" + Uri.EscapeDataString(Scopes)
            + "&code_challenge=" + challenge + "&code_challenge_method=S256&state=" + state;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        var ctxTask = hl.GetContextAsync();
        if (await Task.WhenAny(ctxTask, Task.Delay(TimeSpan.FromMinutes(3))) != ctxTask) return "Sign-in timed out.";
        var ctx = await ctxTask;
        string code = ctx.Request.QueryString["code"], err = ctx.Request.QueryString["error"], st = ctx.Request.QueryString["state"];
        string page = code != null && st == state
            ? "<html><body style='background:#000;color:#f3f4f6;font:16px Segoe UI;padding:48px'>VowenLite is connected to Google. You can close this tab.</body></html>"
            : "<html><body style='background:#000;color:#ffb4a8;font:16px Segoe UI;padding:48px'>Sign-in failed: " + WebUtility.HtmlEncode(err ?? "bad response") + "</body></html>";
        var bytes = Encoding.UTF8.GetBytes(page);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
        if (code == null || st != state) return "Sign-in failed: " + (err ?? "bad response");

        var r = await PostForm(TokenUrl, new()
        {
            ["code"] = code, ["client_id"] = c.Id, ["client_secret"] = c.Secret, ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code", ["code_verifier"] = verifier
        });
        if (r["error"] != null) return "Sign-in failed: " + ((string)r["error_description"] ?? (string)r["error"]);
        tok = new Tok { RefreshToken = (string)r["refresh_token"], AccessToken = (string)r["access_token"], Expires = DateTime.UtcNow.AddSeconds((int)r["expires_in"] - 30) };
        if (string.IsNullOrEmpty(tok.RefreshToken)) { tok = null; return "Google did not return a refresh token. Remove VowenLite at myaccount.google.com/permissions and try again."; }
        SaveTok();
        Status = "Connected";
        _ = SyncNowAsync();
        return "Connected to Google.";
    }

    public static void Disconnect()
    {
        tok = null;
        try { File.Delete(TokenFile); } catch { }
        Status = "Not connected";
    }

    static async Task<JsonNode> PostForm(string url, Dictionary<string, string> form)
    {
        using var resp = await Http.PostAsync(url, new FormUrlEncodedContent(form));
        string body = await resp.Content.ReadAsStringAsync();
        try { return JsonNode.Parse(body) ?? new JsonObject(); } catch { return new JsonObject { ["error"] = "bad_response", ["error_description"] = body }; }
    }

    static void LoadTok()
    {
        try { tok ??= JsonSerializer.Deserialize<Tok>(File.ReadAllText(TokenFile)); } catch { }
    }
    static void SaveTok() { try { Paths.Secure(TokenFile, false); File.WriteAllText(TokenFile, JsonSerializer.Serialize(tok)); } catch { } }

    static async Task<string> AccessToken()
    {
        LoadTok();
        if (tok == null) throw new InvalidOperationException("Not connected");
        if (tok.Expires > DateTime.UtcNow && !string.IsNullOrEmpty(tok.AccessToken)) return tok.AccessToken;
        var c = LoadClient() ?? throw new InvalidOperationException("google-client.json is missing");
        if (Environment.GetEnvironmentVariable("VOWENLITE_TRACE") != null) Console.WriteLine("refresh token...");
        var r = await PostForm(TokenUrl, new() { ["client_id"] = c.Id, ["client_secret"] = c.Secret, ["refresh_token"] = tok.RefreshToken, ["grant_type"] = "refresh_token" });
        if (r["access_token"] == null)
        {
            string e = (string)r["error"] ?? "unknown";
            if (e == "invalid_grant") { Disconnect(); throw new InvalidOperationException("Google sign-in expired; press Connect Google again"); }
            throw new InvalidOperationException("token refresh failed: " + e);
        }
        tok.AccessToken = (string)r["access_token"];
        tok.Expires = DateTime.UtcNow.AddSeconds((int)r["expires_in"] - 30);
        SaveTok();
        return tok.AccessToken;
    }

    // ---------------- REST helper

    static async Task<JsonNode> Api(HttpMethod method, string url, JsonNode body = null)
    {
        if (Environment.GetEnvironmentVariable("VOWENLITE_TRACE") != null) Console.WriteLine(method + " " + url + " " + body?.ToJsonString());
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await AccessToken());
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req);
        string text = await resp.Content.ReadAsStringAsync();
        if (method == HttpMethod.Delete && (resp.IsSuccessStatusCode || resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)) return null;
        if (!resp.IsSuccessStatusCode)
        {
            string msg = text;
            try { msg = (string)JsonNode.Parse(text)["error"]["message"] ?? text; } catch { }
            throw new HttpRequestException($"{(int)resp.StatusCode} {msg}");
        }
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    static string Q(string s) => Uri.EscapeDataString(s);

    // ---------------- sync

    static FileStream TryLock()
    {
        try { return new FileStream(Path.Combine(Paths.DataDir, "planner.sync.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    static int syncing;

    public static async Task<string> SyncNowAsync()
    {
        if (!Connected) return Status;
        if (Interlocked.Exchange(ref syncing, 1) == 1) return "Already syncing";
        using var lk = TryLock();           // the other VowenLite process may be syncing right now
        try
        {
            if (lk == null) return "Already syncing";
            Status = "Syncing…";
            LoadTok();
            var started = DateTime.UtcNow;
            bool firstTasks = tok?.TasksSince == null;
            var lists = await TaskLists();
            await PushTasks(lists);
            await PullTasks(firstTasks, lists);
            await PushEvents();
            await PullEvents();
            if (tok != null) { tok.TasksSince = started.AddMinutes(-2); SaveTok(); }
            LastSync = DateTime.Now;
            Status = "Synced " + LastSync.Value.ToString("h:mm tt");
        }
        catch (Exception ex) { Status = "Sync failed: " + ex.Message; }
        finally { Interlocked.Exchange(ref syncing, 0); }
        return Status;
    }

    static System.Threading.Timer loop, kick;

    public static void Start()
    {
        if (loop != null) return;
        loop = new System.Threading.Timer(_ => { if (Connected) _ = SyncNowAsync(); }, null, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(5));
    }

    // A local change: push it soon (debounced), if signed in.
    public static void Kick()
    {
        if (!Connected) return;
        kick?.Dispose();
        kick = new System.Threading.Timer(_ => _ = SyncNowAsync(), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    static DateTime ParseUtc(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    // ----- tasks: every Google task list is a planner list (the default list = no list)

    sealed class TList { public string Id, Title; }
    static string defaultListId = "@default";

    static async Task<List<TList>> TaskLists()
    {
        var res = new List<TList>();
        string page = null;
        do
        {
            var r = await Api(HttpMethod.Get, $"{TasksApi}/users/@me/lists?maxResults=100" + (page != null ? "&pageToken=" + Q(page) : ""));
            if (r?["items"] is JsonArray a) foreach (var n in a) res.Add(new TList { Id = (string)n["id"], Title = (string)n["title"] ?? "" });
            page = (string)r?["nextPageToken"];
        } while (page != null);
        // Task-list resources include the authenticated-user segment; only task resources
        // themselves live directly under /lists/{id}. The old URL returned Google's HTML 404.
        var def = await Api(HttpMethod.Get, $"{TasksApi}/users/@me/lists/@default");
        defaultListId = (string)def["id"];
        if (!res.Any(x => x.Id == defaultListId)) res.Add(new TList { Id = defaultListId, Title = (string)def["title"] ?? "My Tasks" });
        return res;
    }

    static async Task<string> ListIdFor(string cat, List<TList> lists)
    {
        if (string.IsNullOrWhiteSpace(cat)) return defaultListId;
        var hit = lists.FirstOrDefault(x => x.Id != defaultListId && string.Equals(x.Title, cat, StringComparison.OrdinalIgnoreCase));
        if (hit != null) return hit.Id;
        var r = await Api(HttpMethod.Post, $"{TasksApi}/users/@me/lists", new JsonObject { ["title"] = cat });
        var made = new TList { Id = (string)r["id"], Title = cat };
        lists.Add(made);
        return made.Id;
    }

    static JsonObject TaskJson(Item it)
    {
        var o = new JsonObject { ["title"] = it.Title, ["notes"] = it.Notes ?? "", ["status"] = it.Done ? "completed" : "needsAction" };
        o["due"] = it.Start != null ? it.Start.Value.Date.ToString("yyyy-MM-dd") + "T00:00:00.000Z" : null;
        if (!it.Done) o["completed"] = null;
        return o;
    }

    static async Task PushTasks(List<TList> lists)
    {
        foreach (var it in Planner.Load().Where(i => i.Kind == "task" && i.Dirty).ToList())
        {
            string cur = it.GList ?? defaultListId;
            try
            {
                if (it.Deleted)
                {
                    if (it.GId != null) await Api(HttpMethod.Delete, $"{TasksApi}/lists/{cur}/tasks/{it.GId}");
                    Planner.Edit(l => l.RemoveAll(x => x.Id == it.Id && x.Updated == it.Updated));
                    continue;
                }
                string want = await ListIdFor(it.Cat, lists);
                string gid = it.GId;
                if (gid != null && cur != want) { await Api(HttpMethod.Delete, $"{TasksApi}/lists/{cur}/tasks/{gid}"); gid = null; }   // moved to another list: recreate there
                var r = gid == null
                    ? await Api(HttpMethod.Post, $"{TasksApi}/lists/{want}/tasks", TaskJson(it))
                    : await Api(HttpMethod.Patch, $"{TasksApi}/lists/{want}/tasks/{gid}", TaskJson(it));
                string newId = (string)r["id"];
                Planner.Edit(l => { var x = l.Find(i => i.Id == it.Id); if (x == null) return; x.GId = newId; x.GList = want; if (x.Updated == it.Updated) x.Dirty = false; });
            }
            catch (HttpRequestException ex) when (ex.Message.StartsWith("404") && it.GId != null)
            {
                Planner.Edit(l => l.RemoveAll(x => x.Id == it.Id && x.Updated == it.Updated));   // deleted on the phone meanwhile
            }
        }
    }

    static async Task PullTasks(bool first, List<TList> lists)
    {
        var remote = new List<(JsonNode node, TList list)>();
        foreach (var tl in lists)
        {
            string page = null;
            do
            {
                string url = $"{TasksApi}/lists/{tl.Id}/tasks?maxResults=100&showCompleted={(first ? "false" : "true")}&showHidden={(first ? "false" : "true")}&showDeleted={(first ? "false" : "true")}";
                if (!first && tok?.TasksSince != null) url += "&updatedMin=" + Q(tok.TasksSince.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
                if (page != null) url += "&pageToken=" + Q(page);
                var r = await Api(HttpMethod.Get, url);
                if (r?["items"] is JsonArray a) foreach (var n in a) remote.Add((n, tl));
                page = (string)r?["nextPageToken"];
            } while (page != null);
        }

        Planner.Edit(list =>
        {
            foreach (var (r, tl) in remote)
            {
                string gid = (string)r["id"];
                var x = list.Find(i => i.Kind == "task" && i.GId == gid);
                if ((bool?)r["deleted"] == true) { if (x != null) list.Remove(x); continue; }
                if (x != null && x.Dirty) continue;     // a local edit not pushed yet wins
                if (x == null) { x = new Item { Kind = "task", GId = gid }; list.Add(x); }
                x.GList = tl.Id;
                string cat = tl.Id == defaultListId ? "" : tl.Title;
                if (!string.Equals(x.Cat ?? "", cat, StringComparison.OrdinalIgnoreCase)) x.Cat = cat;
                x.Title = (string)r["title"] ?? "";
                if (x.Title.Length == 0 && (string)r["status"] != "completed") x.Title = "(untitled)";
                x.Notes = (string)r["notes"] ?? "";
                bool doneRemote = (string)r["status"] == "completed";
                x.Start = r["due"] != null ? ParseUtc((string)r["due"]).Date : null;
                x.Updated = r["updated"] != null ? ParseUtc((string)r["updated"]) : DateTime.UtcNow;
                x.Done = doneRemote;
                x.Dirty = false;
                // a repeating task ticked off on the phone comes round again here (and the new date goes back to Google)
                if (doneRemote && x.Repeat != null && Rule.Parse(x.Repeat) is { } rule && x.Start != null && rule.Count != 1)
                {
                    var after = x.Start.Value.Date > DateTime.Today ? x.Start.Value.Date : DateTime.Today;
                    var next = rule.Expand(x.Start.Value, after.AddYears(8)).Cast<DateTime?>().FirstOrDefault(d => d.Value.Date > after);
                    if (next != null)
                    {
                        if (rule.Count > 1) { rule.Count--; x.Repeat = rule.ToRrule(); }
                        x.Start = next.Value.Date; x.Done = false; x.LastDone = DateTime.Now; x.Dirty = true; x.Updated = DateTime.UtcNow;
                    }
                }
            }
        });
    }

    // ----- calendar

    static string IanaZone()
    {
        var tz = TimeZoneInfo.Local;
        if (tz.HasIanaId) return tz.Id;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var iana) && !string.IsNullOrEmpty(iana)) return iana;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Eastern Standard Time"] = "America/New_York", ["Central Standard Time"] = "America/Chicago", ["Mountain Standard Time"] = "America/Denver",
            ["Pacific Standard Time"] = "America/Los_Angeles", ["US Mountain Standard Time"] = "America/Phoenix", ["Alaskan Standard Time"] = "America/Anchorage",
            ["Hawaiian Standard Time"] = "Pacific/Honolulu", ["Atlantic Standard Time"] = "America/Halifax", ["Newfoundland Standard Time"] = "America/St_Johns",
            ["Canada Central Standard Time"] = "America/Regina", ["Central America Standard Time"] = "America/Guatemala", ["Mexico Standard Time"] = "America/Mexico_City",
            ["GMT Standard Time"] = "Europe/London", ["Greenwich Standard Time"] = "Atlantic/Reykjavik", ["W. Europe Standard Time"] = "Europe/Berlin",
            ["Central Europe Standard Time"] = "Europe/Budapest", ["Romance Standard Time"] = "Europe/Paris", ["Central European Standard Time"] = "Europe/Warsaw",
            ["E. Europe Standard Time"] = "Europe/Chisinau", ["FLE Standard Time"] = "Europe/Kiev", ["GTB Standard Time"] = "Europe/Bucharest", ["Russian Standard Time"] = "Europe/Moscow",
            ["Turkey Standard Time"] = "Europe/Istanbul", ["Israel Standard Time"] = "Asia/Jerusalem", ["Arab Standard Time"] = "Asia/Riyadh", ["Arabian Standard Time"] = "Asia/Dubai",
            ["India Standard Time"] = "Asia/Kolkata", ["Pakistan Standard Time"] = "Asia/Karachi", ["Bangladesh Standard Time"] = "Asia/Dhaka", ["SE Asia Standard Time"] = "Asia/Bangkok",
            ["China Standard Time"] = "Asia/Shanghai", ["Singapore Standard Time"] = "Asia/Singapore", ["Taipei Standard Time"] = "Asia/Taipei", ["Tokyo Standard Time"] = "Asia/Tokyo",
            ["Korea Standard Time"] = "Asia/Seoul", ["W. Australia Standard Time"] = "Australia/Perth", ["AUS Eastern Standard Time"] = "Australia/Sydney",
            ["E. Australia Standard Time"] = "Australia/Brisbane", ["Cen. Australia Standard Time"] = "Australia/Adelaide", ["New Zealand Standard Time"] = "Pacific/Auckland",
            ["South Africa Standard Time"] = "Africa/Johannesburg", ["Egypt Standard Time"] = "Africa/Cairo", ["E. South America Standard Time"] = "America/Sao_Paulo",
            ["Argentina Standard Time"] = "America/Argentina/Buenos_Aires", ["SA Pacific Standard Time"] = "America/Bogota", ["UTC"] = "UTC",
        };
        if (map.TryGetValue(tz.Id, out var z)) return z;
        int h = (int)Math.Round(tz.BaseUtcOffset.TotalHours);
        return h == 0 ? "UTC" : "Etc/GMT" + (h > 0 ? "-" : "+") + Math.Abs(h);   // fixed-offset zone as a last resort
    }

    static JsonObject EventJson(Item it, bool patch)
    {
        var o = new JsonObject { ["summary"] = it.Title, ["description"] = it.Notes ?? "" };
        string tz = IanaZone();
        var rule = Rule.Parse(it.Repeat);
        if (it.AllDay)
        {
            o["start"] = new JsonObject { ["date"] = it.Start.Value.ToString("yyyy-MM-dd") };
            o["end"] = new JsonObject { ["date"] = (it.End ?? it.Start.Value.AddDays(1)).ToString("yyyy-MM-dd") };
            if (patch) { o["start"]["dateTime"] = null; o["start"]["timeZone"] = null; o["end"]["dateTime"] = null; o["end"]["timeZone"] = null; }
        }
        else
        {
            JsonObject Dt(DateTime d) => new JsonObject { ["dateTime"] = new DateTimeOffset(d, TimeZoneInfo.Local.GetUtcOffset(d)).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture), ["timeZone"] = tz };
            o["start"] = Dt(it.Start.Value);
            o["end"] = Dt(it.End ?? it.Start.Value.AddHours(1));
            if (patch) { o["start"]["date"] = null; o["end"]["date"] = null; }   // event was all-day on Google: clear the old date or Google says "Invalid start time"
        }
        if (rule != null)
        {
            var rec = new JsonArray("RRULE:" + rule.ToRrule(it.AllDay));
            foreach (var sk in it.Skip.OrderBy(d => d))
                rec.Add(it.AllDay ? "EXDATE;VALUE=DATE:" + sk.ToString("yyyyMMdd") : $"EXDATE;TZID={tz}:" + sk.ToString("yyyyMMdd'T'HHmmss"));
            o["recurrence"] = rec;
        }
        else if (patch) o["recurrence"] = null;
        o["reminders"] = new JsonObject { ["useDefault"] = false, ["overrides"] = new JsonArray(new JsonObject { ["method"] = "popup", ["minutes"] = it.Alert }) };
        o["extendedProperties"] = new JsonObject { ["private"] = new JsonObject { ["vlCat"] = it.Cat ?? "", ["vlPrio"] = it.Priority.ToString() } };
        return o;
    }

    static async Task PushEvents()
    {
        foreach (var it in Planner.Load().Where(i => i.Kind == "event" && i.Dirty).ToList())
        {
            try
            {
                if (it.Deleted)
                {
                    if (it.GId != null) await Api(HttpMethod.Delete, $"{CalApi}/calendars/primary/events/{it.GId}");
                    Planner.Edit(l => l.RemoveAll(x => x.Id == it.Id && x.Updated == it.Updated));
                    continue;
                }
                var r = it.GId == null
                    ? await Api(HttpMethod.Post, $"{CalApi}/calendars/primary/events", EventJson(it, false))
                    : await Api(HttpMethod.Patch, $"{CalApi}/calendars/primary/events/{it.GId}", EventJson(it, true));
                string gid = (string)r["id"];
                Planner.Edit(l => { var x = l.Find(i => i.Id == it.Id); if (x == null) return; x.GId = gid; if (x.Updated == it.Updated) x.Dirty = false; });
            }
            catch (HttpRequestException ex) when (ex.Message.StartsWith("404") && it.GId != null)
            {
                Planner.Edit(l => l.RemoveAll(x => x.Id == it.Id && x.Updated == it.Updated));
            }
        }
    }

    // Copies a Google event onto a planner item. Returns false when it can't be read.
    static bool ApplyEvent(Item x, JsonNode r, bool master)
    {
        bool allDay = r["start"]?["date"] != null;
        DateTime start, end;
        try
        {
            start = allDay ? DateTime.Parse((string)r["start"]["date"], CultureInfo.InvariantCulture) : DateTimeOffset.Parse((string)r["start"]["dateTime"], CultureInfo.InvariantCulture).LocalDateTime;
            end = allDay ? DateTime.Parse((string)r["end"]["date"], CultureInfo.InvariantCulture) : DateTimeOffset.Parse((string)r["end"]["dateTime"], CultureInfo.InvariantCulture).LocalDateTime;
        }
        catch { return false; }
        int alert = 10;
        if (r["reminders"]?["overrides"] is JsonArray ov && ov.Count > 0) alert = (int?)ov[0]["minutes"] ?? 10;
        if (x.Start != start || x.Alert != alert) { x.Fired = false; x.FiredFor = null; }
        x.Title = (string)r["summary"] ?? "(no title)";
        x.Notes = (string)r["description"] ?? "";
        x.AllDay = allDay; x.Start = start; x.End = end; x.Alert = alert;
        var pp = r["extendedProperties"]?["private"];
        if (pp != null)
        {
            if (pp["vlCat"] != null) x.Cat = (string)pp["vlCat"] ?? "";
            if (int.TryParse((string)pp["vlPrio"], out int pr)) x.Priority = pr;
        }
        if (master && r["recurrence"] is JsonArray rec)
        {
            var line = rec.Select(n => (string)n).FirstOrDefault(s => s != null && s.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase));
            var rule = line != null ? Rule.Parse(line) : null;
            if (rule != null) x.Repeat = rule.ToRrule(allDay);
        }
        x.Updated = r["updated"] != null ? ParseUtc((string)r["updated"]) : DateTime.UtcNow;
        x.Dirty = false;
        return true;
    }

    static async Task PullEvents()
    {
        var from = DateTime.Today.AddDays(-1); var to = DateTime.Today.AddDays(90);
        var remote = new List<JsonNode>();
        string page = null;
        do
        {
            string url = $"{CalApi}/calendars/primary/events?singleEvents=true&orderBy=startTime&maxResults=250&timeMin={Q(new DateTimeOffset(from).ToString("o"))}&timeMax={Q(new DateTimeOffset(to).ToString("o"))}";
            if (page != null) url += "&pageToken=" + Q(page);
            var r = await Api(HttpMethod.Get, url);
            if (r["items"] is JsonArray a) remote.AddRange(a);
            page = (string)r["nextPageToken"];
        } while (page != null);

        // Repeating events made here are expanded here: Google's individual occurrences of them are not stored as separate items.
        var masters = Planner.Load().Where(i => i.Kind == "event" && i.GId != null && i.Repeat != null && !i.Deleted).Select(i => i.GId).ToHashSet();
        var instances = masters.ToDictionary(g => g, _ => new List<DateTime>());

        Planner.Edit(list =>
        {
            var seen = new HashSet<string>();
            foreach (var r in remote)
            {
                string gid = (string)r["id"];
                string rec = (string)r["recurringEventId"];
                if ((string)r["status"] == "cancelled" || (string)r["eventType"] == "workingLocation") continue;
                if (r["attendees"] is JsonArray at && at.Any(a => (bool?)a["self"] == true && (string)a["responseStatus"] == "declined")) continue;
                if (rec != null && masters.Contains(rec))
                {
                    var os = r["originalStartTime"] ?? r["start"];
                    try { instances[rec].Add(os?["date"] != null ? DateTime.Parse((string)os["date"], CultureInfo.InvariantCulture) : DateTimeOffset.Parse((string)os["dateTime"], CultureInfo.InvariantCulture).LocalDateTime); } catch { }
                    continue;
                }
                seen.Add(gid);
                var x = list.Find(i => i.Kind == "event" && i.GId == gid);
                if (x != null && x.Dirty) continue;
                bool isNew = x == null;
                if (isNew) x = new Item { Kind = "event", GId = gid };
                if (!ApplyEvent(x, r, false)) continue;
                if (isNew) list.Add(x);
            }
            // Events inside the window that Google no longer lists were deleted/declined on another device.
            list.RemoveAll(i => i.Kind == "event" && i.GId != null && !i.Dirty && i.Repeat == null && !seen.Contains(i.GId) && i.Start != null && i.Start >= from && i.Start < to);
        });

        // Our repeating events: ask Google about each one (deleted? edited on the phone? single occurrences deleted?).
        foreach (var gid in masters)
        {
            JsonNode r;
            try { r = await Api(HttpMethod.Get, $"{CalApi}/calendars/primary/events/{gid}"); }
            catch (HttpRequestException ex) when (ex.Message.StartsWith("404") || ex.Message.StartsWith("410"))
            { Planner.Edit(l => l.RemoveAll(i => i.GId == gid && !i.Dirty)); continue; }
            if ((string)r["status"] == "cancelled") { Planner.Edit(l => l.RemoveAll(i => i.GId == gid && !i.Dirty)); continue; }
            var seenInst = instances[gid];
            Planner.Edit(l =>
            {
                var x = l.Find(i => i.Kind == "event" && i.GId == gid);
                if (x == null || x.Dirty) return;
                DateTime ru = r["updated"] != null ? ParseUtc((string)r["updated"]) : DateTime.MinValue;
                if (ru > x.Updated.AddSeconds(2)) ApplyEvent(x, r, true);
                // occurrences that exist here but not on Google any more were deleted on the phone
                if (seenInst.Count == 0 || x.Start == null) return;
                var rule = Rule.Parse(x.Repeat);
                if (rule == null) return;
                var dur = (x.End ?? x.Start.Value.AddHours(1)) - x.Start.Value;
                foreach (var occ in rule.Expand(x.Start.Value, to).Where(d => d >= DateTime.Today && d + dur > from))
                {
                    if (x.Skip.Contains(occ)) continue;
                    bool there = seenInst.Any(d => x.AllDay ? d.Date == occ.Date : Math.Abs((d - occ).TotalMinutes) < 1);
                    if (!there) x.Skip.Add(occ);
                }
            });
        }
    }
}
