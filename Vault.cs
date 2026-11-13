using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VowenLite;

// Saved logins for Luna's background browser (WebHost.cs). The user types a password once in the vault window (VaultForm.cs); it is
// encrypted with DPAPI (CurrentUser scope + per-entry entropy) into vault.json, so only this Windows account can decrypt it: Codex's
// sandbox runs as a different account and can't. Only the web host process decrypts, and only to fill a sign-in form on the entry's own
// site; nothing decrypted is ever returned to Codex, logged, or shown again in the window.
// Hard 7-day cap: an entry dies 7 days after it was saved (no renewal by use). On expiry the encrypted password is deleted and that site's
// cookies/storage in the background profile are queued for clearing, so the session dies with it; the entry stays listed as expired until
// the user re-types the password. Sessions on sites with no entry age out the same way: browser-sites.json stamps the first time a site's
// cookies are seen and the host wipes that site's cookies/storage 7 days later.
static class Vault
{
    public static readonly TimeSpan Life = TimeSpan.FromDays(7);
    public static Func<DateTime> Now = () => DateTime.UtcNow;   // injectable clock for --vaulttest

    public sealed class Entry
    {
        public string Site { get; set; } = "";     // registrable domain (coarse key for cookies/expiry grouping)
        public string Host { get; set; } = "";      // the exact host the user typed (H2: logins only fill on this host + its www/bare pair)
        public string User { get; set; } = "";
        public string Blob { get; set; }           // DPAPI ciphertext (base64) of "v1|<savedAtTicks>|<password>"; null once expired
        public string Salt { get; set; }           // per-entry entropy (base64)
        public DateTime SavedAt { get; set; }      // UTC; display/ordering hint only. Authoritative expiry = the SavedAt sealed INSIDE Blob.
        public bool Expired => Blob == null;
        public double DaysLeft => Expired ? 0 : Math.Max(0, (SavedAt + Life - Now()).TotalDays);
    }

    public sealed class SiteAge
    {
        public DateTime FirstSeen { get; set; }
        public List<string> Origins { get; set; } = new();
    }

    sealed class Book
    {
        public Dictionary<string, SiteAge> Sites { get; set; } = new();
        public List<string> PendingClear { get; set; } = new();
    }

    static string VaultFile => Path.Combine(Paths.DataDir, "vault.json");
    static string BookFile => Path.Combine(Paths.DataDir, "browser-sites.json");
    static readonly string MutexName = @"Local\VowenLite.Vault." + Hash(Paths.DataDir.ToLowerInvariant());
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    internal static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).Substring(0, 12);

    static T Locked<T>(Func<T> f)
    {
        using var m = new Mutex(false, MutexName);
        bool got = false;
        try { try { got = m.WaitOne(5000); } catch (AbandonedMutexException) { got = true; } return f(); }
        finally { if (got) m.ReleaseMutex(); }
    }

    static List<Entry> LoadEntries() { try { return File.Exists(VaultFile) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(VaultFile)) ?? new() : new(); } catch { return new(); } }
    static Book LoadBook() { try { return File.Exists(BookFile) ? JsonSerializer.Deserialize<Book>(File.ReadAllText(BookFile)) ?? new() : new(); } catch { return new(); } }
    static void Atomic(string path, string text) { string tmp = path + ".tmp"; File.WriteAllText(tmp, text); File.Move(tmp, path, true); }
    static void SaveEntries(List<Entry> e) { Atomic(VaultFile, JsonSerializer.Serialize(e, Opts)); Paths.Secure(VaultFile, false); }   // C1: keep the sandbox out
    static void SaveBook(Book b) { Atomic(BookFile, JsonSerializer.Serialize(b, Opts)); Paths.Secure(BookFile, false); }

    // ---- registrable domain ("login.github.com" -> "github.com"; "a.b.co.uk" -> "b.co.uk"; "me.github.io" -> "me.github.io")
    static readonly HashSet<string> PublicSuffix2 = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.io", "gitlab.io", "vercel.app", "netlify.app", "pages.dev", "workers.dev", "web.app", "firebaseapp.com", "herokuapp.com", "appspot.com",
        "blogspot.com", "azurewebsites.net", "cloudfront.net", "onrender.com", "glitch.me", "repl.co", "fly.dev", "ngrok.io", "ngrok-free.app",
    };
    static readonly HashSet<string> Second = new(StringComparer.OrdinalIgnoreCase) { "co", "com", "net", "org", "gov", "edu", "ac", "or", "ne", "go", "gob", "nic", "ltd", "plc", "sch" };

    public static string Site(string hostOrUrl)
    {
        string h = (hostOrUrl ?? "").Trim();
        if (h.Length == 0) return "";
        try
        {
            if (h.Contains("://")) h = new Uri(h).Host;
            else { int cut = h.IndexOfAny(new[] { '/', '?', '#' }); if (cut >= 0) h = h.Substring(0, cut); int at = h.LastIndexOf('@'); if (at >= 0) h = h.Substring(at + 1); int colon = h.LastIndexOf(':'); if (colon > 0 && !h.Contains(']')) h = h.Substring(0, colon); }
            h = h.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
            if (IPAddress.TryParse(h, out _)) return h;
            h = new IdnMapping().GetAscii(h);
        }
        catch { return ""; }
        var l = h.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (l.Length <= 2) return string.Join('.', l);
        string last2 = l[^2] + "." + l[^1];
        if (PublicSuffix2.Contains(last2) || (l[^1].Length == 2 && Second.Contains(l[^2]))) return l[^3] + "." + last2;
        return last2;
    }

    // The page may use a stored login for `site` only when it is https and its registrable domain is exactly that site.
    public static bool PageMatches(string site, string pageUrl)
    {
        if (string.IsNullOrEmpty(site) || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var u) || u.Scheme != "https") return false;
        return Site(u.Host) == site;
    }

    // Exact host of a URL (lowercased, bracket/port/userinfo stripped), or "" if it isn't a parseable https(ish) URL/host.
    public static string HostOf(string urlOrHost)
    {
        string h = (urlOrHost ?? "").Trim();
        if (h.Length == 0) return "";
        try
        {
            if (h.Contains("://")) h = new Uri(h).Host;
            else { int cut = h.IndexOfAny(new[] { '/', '?', '#' }); if (cut >= 0) h = h.Substring(0, cut); int at = h.LastIndexOf('@'); if (at >= 0) h = h.Substring(at + 1); int colon = h.LastIndexOf(':'); if (colon > 0 && !h.Contains(']')) h = h.Substring(0, colon); }
            h = h.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
            if (!IPAddress.TryParse(h, out _)) h = new IdnMapping().GetAscii(h);
        }
        catch { return ""; }
        return h;
    }

    // H2: the exact hosts a saved entry may fill on — the host the user typed, plus its explicit www<->bare counterpart. Nothing else:
    // a "github.com" entry never fills on gist.github.com, user-content subdomains, or look-alikes.
    public static HashSet<string> AllowedHosts(Entry e)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string host = string.IsNullOrEmpty(e.Host) ? e.Site : e.Host;   // old entries predating Host fall back to the registrable domain
        if (host.Length == 0) return set;
        set.Add(host);
        if (host.StartsWith("www.")) set.Add(host.Substring(4)); else set.Add("www." + host);
        return set;
    }

    // The entry may fill on `url` only when it is https and its exact host is one of the entry's allowed hosts.
    public static bool EntryMatches(Entry e, string url)
    {
        if (e == null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https") return false;
        return AllowedHosts(e).Contains(HostOf(u.Host));
    }

    public static string Mask(string user)
    {
        if (string.IsNullOrEmpty(user)) return "(no username)";
        int at = user.IndexOf('@');
        return at > 0 ? user[0] + "***@…" : user[0] + "***";
    }

    // ---- entries

    // Expires what is due: deletes the ciphertext and queues the site's browser data for clearing. Returns the sites that expired now.
    // Expiry is driven by the timestamp sealed inside the ciphertext (Stamp), not the plaintext SavedAt, so editing the file can't extend a login.
    public static List<string> Sweep() => Locked(() =>
    {
        var list = LoadEntries(); var gone = new List<string>();
        foreach (var e in list.Where(e => !e.Expired)) { var st = Stamp(e); if (st == null || Now() >= st.Value + Life) { e.Blob = null; e.Salt = null; if (!gone.Contains(e.Site)) gone.Add(e.Site); } }
        if (gone.Count > 0)
        {
            SaveEntries(list);
            var b = LoadBook(); foreach (var s in gone) if (!b.PendingClear.Contains(s)) b.PendingClear.Add(s); SaveBook(b);
        }
        return gone;
    });

    public static List<Entry> List() { Sweep(); return Locked(LoadEntries).OrderBy(e => e.Site).ToList(); }

    public static string Save(string siteText, string user, string password)
    {
        string site = Site(siteText), host = HostOf(siteText);
        if (site.Length == 0 || !site.Contains('.') || IPAddress.TryParse(site, out _)) return "Give the site as a web address, e.g. github.com.";
        if (host.Length == 0) host = site;
        if (string.IsNullOrEmpty(password)) return "Type the password.";
        DateTime saved = Now();
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] plain = Pack(saved, password);               // the save time is sealed inside the ciphertext, bound to the site via entropy
        string blob;
        try { blob = Convert.ToBase64String(ProtectedData.Protect(plain, Entropy(salt, site), DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Locked(() =>
        {
            var list = LoadEntries();
            list.RemoveAll(e => e.Site == site && string.Equals(e.User, user ?? "", StringComparison.OrdinalIgnoreCase));
            list.Add(new Entry { Site = site, Host = host, User = user ?? "", Blob = blob, Salt = Convert.ToBase64String(salt), SavedAt = saved });
            SaveEntries(list);
            return 0;
        });
        return "";
    }

    // "v1|<savedAtUtcTicks>|<password>" -> bytes, and back. The timestamp travels inside the encrypted blob so it is tamper-evident.
    static byte[] Pack(DateTime savedUtc, string pw) => Encoding.UTF8.GetBytes("v1|" + savedUtc.ToUniversalTime().Ticks + "|" + pw);
    static (DateTime? At, string Pw) Unpack(byte[] plain)
    {
        string s = Encoding.UTF8.GetString(plain);
        var p = s.Split('|', 3);
        if (p.Length == 3 && p[0] == "v1" && long.TryParse(p[1], out long ticks) && ticks > 0 && ticks <= DateTime.MaxValue.Ticks)
            return (new DateTime(ticks, DateTimeKind.Utc), p[2]);
        return (null, null);
    }

    public static void Remove(string site, string user) => Locked(() =>
    {
        var list = LoadEntries();
        if (list.RemoveAll(e => e.Site == site && e.User == user) > 0)
        {
            SaveEntries(list);
            var b = LoadBook(); if (!b.PendingClear.Contains(site)) b.PendingClear.Add(site); SaveBook(b);   // its session goes too
        }
        return 0;
    });

    static byte[] Entropy(byte[] salt, string site) => salt.Concat(Encoding.UTF8.GetBytes("vowenlite-vault:" + site)).ToArray();

    // Host only (WebHost.Login). null entry = nothing saved for that site.
    internal static Entry Find(string site) { Sweep(); return Locked(LoadEntries).Where(e => e.Site == site).OrderBy(e => e.Expired).ThenByDescending(e => e.SavedAt).FirstOrDefault(); }

    internal static string Reveal(Entry e)
    {
        if (e == null || e.Expired) return null;
        byte[] plain;
        try { plain = ProtectedData.Unprotect(Convert.FromBase64String(e.Blob), Entropy(Convert.FromBase64String(e.Salt), e.Site), DataProtectionScope.CurrentUser); }
        catch { return null; }
        try
        {
            var (at, pw) = Unpack(plain);
            if (at == null || Now() >= at.Value + Life) return null;   // missing/invalid sealed timestamp, or past 7 days: treat as expired
            return pw;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    // The sealed save time, or null if the blob is gone/corrupt/not ours. Used by Sweep so expiry can't be pushed out by editing SavedAt.
    internal static DateTime? Stamp(Entry e)
    {
        if (e == null || e.Blob == null || e.Salt == null) return null;
        byte[] plain;
        try { plain = ProtectedData.Unprotect(Convert.FromBase64String(e.Blob), Entropy(Convert.FromBase64String(e.Salt), e.Site), DataProtectionScope.CurrentUser); }
        catch { return null; }
        try { return Unpack(plain).At; } finally { CryptographicOperations.ZeroMemory(plain); }
    }

    // ---- ages of sessions in the background profile

    public static void NoteSite(string site, string origin = null)
    {
        if (string.IsNullOrEmpty(site)) return;
        Locked(() =>
        {
            var b = LoadBook(); bool dirty = false;
            if (!b.Sites.TryGetValue(site, out var a)) { b.Sites[site] = a = new SiteAge { FirstSeen = Now() }; dirty = true; }
            if (origin != null && !a.Origins.Contains(origin) && a.Origins.Count < 40) { a.Origins.Add(origin); dirty = true; }
            if (dirty) SaveBook(b);
            return 0;
        });
    }

    public static bool Known(string site) => Locked(() => LoadBook().Sites.ContainsKey(site));

    // Sites whose cookies/storage must be wiped now: expired or removed vault entries, and any session first seen 7+ days ago.
    public static List<(string Site, List<string> Origins)> DueClears() => Locked(() =>
    {
        var b = LoadBook();
        var due = b.PendingClear.Union(b.Sites.Where(kv => Now() >= kv.Value.FirstSeen + Life).Select(kv => kv.Key)).Distinct().ToList();
        return due.Select(s => (s, b.Sites.TryGetValue(s, out var a) ? a.Origins.ToList() : new List<string>())).ToList();
    });

    public static void Cleared(string site) => Locked(() => { var b = LoadBook(); b.PendingClear.Remove(site); b.Sites.Remove(site); SaveBook(b); return 0; });

    // ---- `--vaulttest`

    internal static int SelfTest()
    {
        if (Environment.GetEnvironmentVariable("VOWENLITE_DATA") == null)   // --vaulttest wipes vault.json/browser-sites.json: never against the real profile
        {
            Console.WriteLine("refusing to run: set VOWENLITE_DATA to a scratch folder first (this test deletes vault.json / browser-sites.json).");
            return 2;
        }
        int fail = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) fail++; }
        var real = Now;
        DateTime t0 = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc), clock = t0;
        Now = () => clock;
        try
        {
            try { File.Delete(VaultFile); File.Delete(BookFile); } catch { }
            const string pw = "Fake-Pass-7731!";
            Check("save", Save("https://login.Example-Test.com/path", "jo.doe@mail.test", pw) == "");
            var e = Find("example-test.com");
            Check("site normalised to registrable domain", e != null && e.Site == "example-test.com");
            Check("ciphertext is not the password", e != null && !File.ReadAllText(VaultFile).Contains(pw) && !Encoding.UTF8.GetString(Convert.FromBase64String(e.Blob)).Contains(pw));
            Check("round trip", Reveal(e) == pw);
            var tampered = new Entry { Site = "other.com", User = e.User, Blob = e.Blob, Salt = e.Salt, SavedAt = e.SavedAt };
            bool bound; try { bound = Reveal(tampered) != pw; } catch { bound = true; }
            Check("blob bound to its site (entropy)", bound);
            Check("mask", Mask("jo.doe@mail.test") == "j***@…" && Mask("jodoe") == "j***");

            foreach (var (host, site) in new[] { ("github.com", "github.com"), ("gist.github.com", "github.com"), ("a.b.github.com", "github.com"), ("github.com.evil.io", "evil.io"),
                         ("evilgithub.com", "evilgithub.com"), ("bbc.co.uk", "bbc.co.uk"), ("www.bbc.co.uk", "bbc.co.uk"), ("me.github.io", "me.github.io"), ("GitHub.COM.", "github.com"), ("user:x@github.com:443", "github.com") })
                Check($"Site({host}) = {site}", Site(host) == site);
            Check("page on subdomain matches", PageMatches("github.com", "https://login.github.com/session"));
            Check("look-alike github.com.evil.io refused", !PageMatches("github.com", "https://github.com.evil.io/login"));
            Check("look-alike evilgithub.com refused", !PageMatches("github.com", "https://evilgithub.com/login"));
            Check("http refused", !PageMatches("github.com", "http://github.com/login"));
            Check("userinfo trick refused", !PageMatches("github.com", "https://github.com@evil.io/login"));

            clock = t0 + TimeSpan.FromDays(6.9);
            Check("alive at 6.9 days", Find("example-test.com") is { Expired: false } && Math.Round(Find("example-test.com").DaysLeft, 1) == 0.1);
            Save("example-test.com", "jo.doe@mail.test", pw);   // re-entering restarts the 7 days ...
            Check("re-entry resets the clock", Math.Round(Find("example-test.com").DaysLeft, 1) == 7);
            clock += TimeSpan.FromDays(6.99);
            Reveal(Find("example-test.com"));                  // ... but using it does not
            clock += TimeSpan.FromDays(0.02);
            var gone = Sweep();
            var ex = Find("example-test.com");
            Check("expired at 7 days", gone.Contains("example-test.com") && ex != null && ex.Expired && ex.Blob == null && Reveal(ex) == null);
            Check("ciphertext deleted from disk", !File.ReadAllText(VaultFile).Contains("\"Blob\": \"") );
            Check("expiry queues the site's browser data for clearing", DueClears().Any(d => d.Site == "example-test.com"));
            Cleared("example-test.com");
            Check("cleared", !DueClears().Any(d => d.Site == "example-test.com"));

            clock = t0; NoteSite("news.test", "https://news.test");
            clock = t0 + TimeSpan.FromDays(6); Check("session younger than 7 days kept", !DueClears().Any(d => d.Site == "news.test"));
            clock = t0 + TimeSpan.FromDays(7); Check("session 7 days old wiped", DueClears().Any(d => d.Site == "news.test" && d.Origins.Contains("https://news.test")));
            Cleared("news.test");

            // read redaction: the formatter never prints a password/card value even if the page script handed one over
            string listing = WebHost.Describe(JsonDocument.Parse("""{"i":3,"role":"textbox","name":"Password","type":"password","secret":true,"filled":true,"val":"Fake-Pass-7731!","ac":"current-password","id":"pw"}""").RootElement);
            string card = WebHost.Describe(JsonDocument.Parse("""{"i":4,"role":"textbox","name":"Card number","type":"text","secret":false,"val":"4111111111111111","ac":"cc-number","id":"cc"}""").RootElement);
            Check("password value never listed: " + listing, !listing.Contains(pw) && listing.Contains("filled"));
            Check("card value never listed: " + card, !card.Contains("4111"));
            Check("scrub removes secrets", !WebHost.Scrub("hi Fake-Pass-7731! there jo.doe@mail.test", new List<string> { pw, "jo.doe@mail.test" }).Contains(pw));

            // M2: a password that leaked into a URL (GET form, redirect) is percent-encoded; the scrubber must catch those forms too.
            string enc = Uri.EscapeDataString(pw);
            string leaked = "URL: https://x.test/login?u=jo.doe%40mail.test&p=" + enc + " done";
            string scrubbed = WebHost.Scrub(leaked, new List<string> { pw, "jo.doe@mail.test" });
            Check("encoded secret scrubbed: " + scrubbed, !scrubbed.Contains(enc) && !scrubbed.Contains(pw));

            // H2: a saved entry fills only on the exact host it was typed for (+ the www/bare pair); not subdomains or look-alikes.
            var gh = new Entry { Site = "github.com", Host = "github.com" };
            Check("exact host matches", Vault.EntryMatches(gh, "https://github.com/login"));
            Check("www pair matches", Vault.EntryMatches(gh, "https://www.github.com/login"));
            Check("subdomain gist.github.com refused", !Vault.EntryMatches(gh, "https://gist.github.com/login"));
            Check("look-alike github.com.evil.io refused", !Vault.EntryMatches(gh, "https://github.com.evil.io/login"));
            Check("userinfo github.com@evil.io refused", !Vault.EntryMatches(gh, "https://github.com@evil.io/login"));
            Check("http refused (entry)", !Vault.EntryMatches(gh, "http://github.com/login"));
            var sub = new Entry { Site = "github.com", Host = "login.github.com" };
            Check("host-specific entry: bare domain refused", !Vault.EntryMatches(sub, "https://github.com/x") && Vault.EntryMatches(sub, "https://login.github.com/x"));

            // C1: the save time is sealed in the ciphertext; editing the plaintext SavedAt can't revive or extend an entry.
            clock = t0; Save("stamp.test", "u", pw);
            var se = Find("stamp.test");
            var forged = new Entry { Site = se.Site, Host = se.Host, User = se.User, Blob = se.Blob, Salt = se.Salt, SavedAt = t0 + TimeSpan.FromDays(100) };
            clock = t0 + TimeSpan.FromDays(8);
            Check("sealed timestamp beats a forged SavedAt", Reveal(forged) == null && Stamp(forged) != null && Now() >= Stamp(forged).Value + Life);
            var corrupt = new Entry { Site = se.Site, Host = se.Host, User = se.User, Blob = Convert.ToBase64String(Encoding.UTF8.GetBytes("not a dpapi blob")), Salt = se.Salt, SavedAt = t0 };
            clock = t0;
            Check("corrupt blob = expired", Reveal(corrupt) == null && Stamp(corrupt) == null);

            // H1: private-address classification covers the forms an attacker would reach for (decimal/hex/IPv6-mapped/CGNAT/ULA).
            foreach (var h in new[] { "127.0.0.1", "10.0.0.1", "192.168.1.1", "169.254.1.1", "172.16.0.1", "100.64.0.1", "2130706433", "0x7f.0.0.1", "0177.0.0.1", "[::1]", "[::ffff:127.0.0.1]", "[fc00::1]", "[fe80::1]", "0.0.0.0", "localhost", "foo.localhost", "box.local" })
                Check($"private host blocked: {h}", WebHost.IsPrivateHostForTest(h));
            foreach (var h in new[] { "example.com", "8.8.8.8", "github.com", "[2606:4700:4700::1111]" })
                Check($"public host allowed: {h}", !WebHost.IsPrivateHostForTest(h));
        }
        finally { Now = real; }
        Console.WriteLine(fail == 0 ? "ALL PASS" : fail + " FAILED");
        return fail == 0 ? 0 : 1;
    }
}
