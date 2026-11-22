using System.Text.RegularExpressions;

namespace VowenLite;

// `--vaulttest live` (needs VOWENLITE_DATA pointing at a scratch folder): drives the real background browser against made-up sign-in
// pages served as https://login.vltest.example (and the look-alike https://vltest.example.evil.example) from a temp folder, with a
// made-up vault entry. Checks fill + submit + clear, that nothing returned ever contains the test password, the refusals, and expiry.
static class WebTest
{
    const string User = "tester@vltest.example", Pw = "Fake-Pw-ZQ93-test";

    public static int Live()
    {
        if (Environment.GetEnvironmentVariable("VOWENLITE_DATA") == null) { Console.WriteLine("set VOWENLITE_DATA to a scratch folder first"); return 2; }
        string dir = Directory.CreateDirectory(Path.Combine(Paths.DataDir, "webtest-site")).FullName;
        WritePages(dir);
        Environment.SetEnvironmentVariable("VOWENLITE_WEBTEST", "1");       // arms the test-folder host mapping (off in production)
        Environment.SetEnvironmentVariable("VOWENLITE_WEB_TESTDIR", dir);   // inherited by the host this process starts
        int fail = 0;
        var outs = new List<string>();
        string Do(params string[] a) { string o = WebBridge.Test(a.ToList()); outs.Add(o); return o; }
        void Check(string name, bool ok, string o = null) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) { fail++; if (o != null) Console.WriteLine("   got: " + o.Replace("\n", "\n   ")); } }
        static string Ref(string read, string pattern) { var m = Regex.Match(read, @"^\[(\d+)\][^\n]*" + pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase); return m.Success ? m.Groups[1].Value : "0"; }

        Vault.Save("login.vltest.example", User, Pw);
        string o = Do("open", "https://login.vltest.example/index.html");
        Check("open + read the sign-in page", o.StartsWith("exit 0") && o.Contains("Sign in") && o.Contains("(password field, empty)"), o);
        Check("type into a password field is refused", Do("type", Ref(o, "password field"), "hunter2").Contains("Use `login`"));
        o = Do("login");
        Check("login fills, submits, lands on the welcome page", o.StartsWith("exit 0") && o.Contains("signed in as t***@…") && o.Contains("welcome.html"), o);
        o = Do("read");
        Check("signed-in page read", o.Contains("Welcome back"), o);

        Do("open", "https://login.vltest.example/index.html?expect=other");
        o = Do("login");
        Check("wrong password: reported, not signed in", o.StartsWith("exit 1") && o.Contains("Incorrect password"), o);
        o = Do("read");
        Check("password box cleared after a failed submit", o.Contains("(password field, empty)") && !o.Contains("(password field, filled)"), o);

        Do("open", "https://login.vltest.example/index.html?next=mfa.html");
        o = Do("login");
        Check("2FA page -> needs the user", o.StartsWith("exit 3") && o.Contains("needs the user"), o);

        o = Do("open", "https://vltest.example.evil.example/index.html");
        Check("look-alike page opens", o.StartsWith("exit 0"), o);
        o = Do("login", "vltest.example");
        Check("login for vltest.example refused on the look-alike", o.StartsWith("exit 126") && o.Contains("not an https page on login.vltest.example"), o);
        o = Do("login");
        Check("look-alike has no saved login", o.StartsWith("exit 3") && o.Contains("no saved login for evil.example"), o);

        o = Do("open", "https://login.vltest.example/card.html");
        Check("card field refused", Do("type", Ref(o, "payment-card"), "4111111111111111").Contains("payment-card field"));
        Check("Pay now refused", Do("click", Ref(o, "\"Pay now\"")).Contains("final pay/buy/order"));
        o = Do("type", Ref(o, "\"Name on order\""), "Test Person");
        Check("ordinary type works", o.StartsWith("exit 0") && o.Contains("typed 11"), o);
        o = Do("read");
        Check("typed value shows in read", o.Contains("= \"Test Person\""), o);
        o = Do("click", Ref(o, "\"Next step\""), "--read");
        Check("click can navigate and read the resulting page", o.Contains("welcome.html") && o.Contains("Welcome back"), o);

        o = Do("open", "https://login.vltest.example/components.html");
        Check("reads nested web component links and text", o.Contains("Course readings") && Ref(o, "Module 13") != "0", o);
        o = Do("click", Ref(o, "Module 13"), "--read");
        Check("opens a link inside nested web components", o.Contains("welcome.html") && o.Contains("Welcome back"), o);

        Check("file: blocked", Do("open", "file:///C:/Windows/win.ini").StartsWith("exit 126"));
        Check("loopback blocked", Do("open", "http://127.0.0.1:9/").StartsWith("exit 126"));
        Check("localhost blocked", Do("open", "https://localhost/").StartsWith("exit 126"));
        Check("private LAN blocked", Do("open", "192.168.1.1").StartsWith("exit 126"));
        o = Do("shot");
        Check("screenshot", o.StartsWith("exit 0") && File.Exists(Path.Combine(PcBridge.LunaDir, "web.png")) && new FileInfo(Path.Combine(PcBridge.LunaDir, "web.png")).Length > 2000, o);

        // a pop-up that closes itself hands the view back to the page that opened it (it used to stay on top, blank)
        o = Do("open", "https://login.vltest.example/popup.html");
        o = Do("click", Ref(o, "\"Open pop-up\""));
        Check("pop-up opened as a tab", o.Contains("opened a new tab"), o);
        Thread.Sleep(2000);
        o = Do("tabs");
        Check("pop-up that closed itself is gone, opener is current", !o.Contains("popdone") && o.Contains("* VL test popup"), o);

        // "leave this page?" no longer cancels the navigation
        o = Do("open", "https://login.vltest.example/leave.html");
        Do("click", Ref(o, "\"Make a change\""));
        o = Do("open", "https://login.vltest.example/welcome.html");
        Check("navigating away from a page with a leave prompt works", o.Contains("Welcome back"), o);

        // a sign-in form embedded from another site: `read` points at the frame, `login` opens the sign-in window (kept off-screen in tests)
        o = Do("open", "https://vltest.example.evil.example/embed.html");
        Check("read lists the cross-site frame", o.Contains("Frames from other sites") && o.Contains("login.vltest.example/index.html"), o);
        o = WebBridge.Test(new() { "login" }, keepOpen: true); outs.Add(o);
        Check("login on an embedded sign-in opens the sign-in window", o.StartsWith("exit 0") && o.Contains("embedded from login.vltest.example") && WebBridge.HandoffPending, o);
        o = WebBridge.Test(new() { "read" }, keepOpen: true); outs.Add(o);
        Check("commands wait while the user signs in", o.StartsWith("exit 125") && o.Contains("Sign-in is in progress"), o);
        WebBridge.End();
        Check("cancelling the hand-off frees the browser", !WebBridge.HandoffPending && Do("read").StartsWith("exit 0"));

        // hand-off: the user signs in (the page moves on by itself here) and the app continues without anyone pressing the button
        o = WebBridge.Test(new() { "handoff", "https://login.vltest.example/index.html?auto=https://vltest.example.evil.example/welcome.html" }, keepOpen: true); outs.Add(o);
        Check("handoff opens the sign-in window", o.StartsWith("exit 0") && WebBridge.HandoffPending, o);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool back = WebBridge.WaitForHandoff(() => clock.Elapsed > TimeSpan.FromSeconds(40)).Result;
        WebBridge.End();
        Check($"sign-in detected and handed back ({clock.Elapsed.TotalSeconds:0.0} s)", back && clock.Elapsed < TimeSpan.FromSeconds(20));
        o = Do("read");
        Check("continues on the signed-in page", o.Contains("Welcome back") && o.Contains("vltest.example.evil.example"), o);

        // expiry: the session cookie set by the welcome page must die with the entry
        o = Do("open", "https://login.vltest.example/welcome.html");
        Check("session cookie present before expiry", o.Contains("cookie: sess=vl"), o);
        var real = Vault.Now; Vault.Now = () => DateTime.UtcNow - TimeSpan.FromDays(8);
        Vault.Save("login.vltest.example", User, Pw);
        Vault.Now = real;
        Do("open", "https://login.vltest.example/index.html");
        o = Do("login");
        Check("expired entry refused", o.StartsWith("exit 3") && o.Contains("expired"), o);
        o = Do("open", "https://login.vltest.example/welcome.html");
        Check("expired site's cookies cleared", !o.Contains("sess=vl"), o);
        Check("vault entry kept as expired", Vault.List().Any(e => e.Site == "vltest.example" && e.Expired));

        // free local commands ("close your tabs") against the running browser
        Do("open", "https://login.vltest.example/welcome.html");
        Do("open", "https://login.vltest.example/popup.html");
        o = Do("click", Ref(Do("read"), "\"Open a page\""));
        var s = Settings.Load();
        o = WebBridge.TryLocal("what tabs do you have open", s);
        Check("list tabs", o != null && o.Contains("2 tabs open") && o.Contains("VL test popup"), o);
        o = WebBridge.TryLocal("close your tabs", s);
        Check("close your tabs", o != null && o.StartsWith("✓ Closed 2 tabs"), o);
        o = WebBridge.TryLocal("what tabs do you have open", s);
        Check("no tabs after closing", o == "✓ No tabs open in my browser", o);

        Check("no reply ever contained the test password", outs.All(x => !x.Contains(Pw)));
        Check("daemon.log never contains the test password", !File.ReadAllText(Path.Combine(Paths.DataDir, "daemon.log")).Contains(Pw));
        WebBridge.Send("quit", new(), null, start: false);   // the host would otherwise stay up for its idle time
        Console.WriteLine(fail == 0 ? "ALL PASS" : fail + " FAILED");
        return fail == 0 ? 0 : 1;
    }

    // `--webtest selftest`: pure checks of the sign-in detection, the refusal reasons and the local command patterns (no browser, no files).
    internal static string Pure()
    {
        var fails = new List<string>();
        void Check(bool ok, string name) { if (!ok) fails.Add(name); }
        static System.Net.IPAddress A(string s) => System.Net.IPAddress.Parse(s);
        Check(WebHost.RefuseReason("www.youtube.com", new[] { A("::"), A("0.0.0.0") })?.StartsWith("www.youtube.com is blocked on this PC (it resolves to 0.0.0.0/::") == true, "hosts-file block named as such");
        Check(WebHost.RefuseReason("ads.example", new[] { A("0.0.0.0") })?.Contains("(it resolves to 0.0.0.0;") == true, "0.0.0.0 alone");
        Check(WebHost.RefuseReason("localtest.me", new[] { A("127.0.0.1") })?.Contains("points at this PC") == true, "loopback name still refused");
        Check(WebHost.RefuseReason("intranet.example", new[] { A("93.184.216.34"), A("10.0.0.5") }) != null, "any private address refuses");
        Check(WebHost.RefuseReason("example.com", new[] { A("93.184.216.34") }) == null, "public name allowed");
        Check(WebHost.RefuseReason("nothing.example", Array.Empty<System.Net.IPAddress>()) != null, "no addresses refused");

        foreach (var u in new[] { "https://login.microsoftonline.com/common/oauth2/v2.0/authorize?x=1", "https://accounts.google.com/v3/signin/identifier", "https://idp.school.example/idp/profile/SAML2/Redirect/SSO",
                                  "https://school.example/cas/login?service=x", "https://portal.school.example/psp/CS/?cmd=login", "https://api-1a2b.duosecurity.com/frame/v4/auth/prompt", "https://example.com/users/sign_in", "https://acme.okta.com/app/x" })
            Check(WebHost.SigninUrl(u), "sign-in address: " + u);
        foreach (var u in new[] { "https://school.brightspace.com/d2l/home", "https://example.com/dashboard", "https://author.example.com/books", "https://www.youtube.com/watch?v=x", "about:blank" })
            Check(!WebHost.SigninUrl(u), "not a sign-in address: " + u);
        var none = Array.Empty<(string, string)>();
        Check(WebHost.Signals("https://example.com/home", true, "", none), "password box = sign-in");
        Check(WebHost.Signals("https://example.com/home", false, "Enter the verification code we sent", none), "code page = sign-in");
        Check(WebHost.Signals("https://example.com/home", false, "", new[] { ("https://sso.school.example/login", "") }), "embedded sign-in frame = sign-in");
        Check(!WebHost.Signals("https://school.brightspace.com/d2l/home", false, "My courses", new[] { ("https://ads.example.net/slot", "Advertisement"), ("https://cdn.example.com/w", "Author bio") }), "signed-in page with ad frames");

        foreach (var p in new[] { "close your tabs", "close the browser tabs", "close all your tabs", "close all of your tabs", "close your browser tabs", "close all the tabs in your browser", "Close Luna's tabs" })
            Check(CloseRx(p), "close phrase: " + p);
        foreach (var p in new[] { "what tabs do you have open", "which tabs do you have open in your browser", "what tabs have you got open", "what tabs do you have", "list your tabs", "what's open in your browser" })
            Check(ListRx(p), "list phrase: " + p);
        foreach (var p in new[] { "close the tab", "close the tabs", "close all tabs", "close tab", "what tabs are open", "what's my day", "open youtube", "start a pomodoro", "close the browser" })
            Check(!CloseRx(p) && !ListRx(p), "not a tab command: " + p);
        bool dry = Luna.DryRun; Luna.DryRun = true;
        try { Check(WebBridge.TryLocal("close your tabs", null)?.StartsWith("DRY RUN: close") == true && WebBridge.TryLocal("what tabs do you have open", null)?.StartsWith("DRY RUN: list") == true, "dry run does nothing"); }
        finally { Luna.DryRun = dry; }
        return fails.Count == 0 ? "webtest ok" : "webtest FAILED:\n  " + string.Join("\n  ", fails);

        static bool CloseRx(string p) => WebBridge.TryLocalKind(p) == "close";
        static bool ListRx(string p) => WebBridge.TryLocalKind(p) == "list";
    }

    static void WritePages(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "index.html"), $$"""
<!doctype html><meta charset="utf-8"><title>VL test sign in</title><h1>Sign in</h1>
<form id="f"><label>Email <input id="email" name="email" type="email" autocomplete="username"></label>
<label>Password <input id="pw" name="password" type="password" autocomplete="current-password"></label>
<button type="submit">Sign in</button></form><p id="msg"></p>
<script>
const q = new URLSearchParams(location.search);
f.onsubmit = (e) => { e.preventDefault();
  const want = q.get('expect') === 'other' ? 'something-else' : '{{Pw}}';
  if (email.value === '{{User}}' && pw.value === want) setTimeout(() => location.href = q.get('next') || 'welcome.html', 100);
  else msg.textContent = 'Incorrect password, try again.'; };
if (q.get('auto')) setTimeout(() => location.href = q.get('auto'), 1500);   // hand-off test: "the user signs in" and the site moves on
</script>
""");
        File.WriteAllText(Path.Combine(dir, "popup.html"), """
<!doctype html><meta charset="utf-8"><title>VL test popup</title><h1>Opener</h1>
<button onclick="window.open('popdone.html', '_blank')">Open pop-up</button> <a href="welcome.html" target="_blank">Open a page</a>
""");
        File.WriteAllText(Path.Combine(dir, "popdone.html"), """
<!doctype html><meta charset="utf-8"><title>VL test popdone</title><p>Done, closing.</p><script>setTimeout(() => window.close(), 600);</script>
""");
        File.WriteAllText(Path.Combine(dir, "leave.html"), """
<!doctype html><meta charset="utf-8"><title>VL test leave</title><h1>Unsaved form</h1><input id="t"> <button onclick="t.value = 'changed'">Make a change</button>
<script>addEventListener('beforeunload', (e) => { e.preventDefault(); e.returnValue = 'Unsaved changes'; });</script>
""");
        File.WriteAllText(Path.Combine(dir, "embed.html"), """
<!doctype html><meta charset="utf-8"><title>VL test portal</title><h1>Portal</h1><p>Sign in below.</p>
<iframe src="https://login.vltest.example/index.html" width="420" height="320" title="Sign in"></iframe>
""");
        File.WriteAllText(Path.Combine(dir, "welcome.html"), """
<!doctype html><meta charset="utf-8"><title>VL test home</title><h1>Welcome back</h1><p>You are signed in.</p>
<p id="c"></p><script>document.getElementById('c').textContent = 'cookie: ' + document.cookie; if (!document.cookie.includes('sess=')) document.cookie = 'sess=vl; max-age=86400; path=/; secure';</script>
""");
        File.WriteAllText(Path.Combine(dir, "mfa.html"), """
<!doctype html><meta charset="utf-8"><title>VL test check</title><h1>Two-step verification</h1><p>Enter the verification code we sent to your phone.</p><input name="code">
""");
        File.WriteAllText(Path.Combine(dir, "components.html"), """
<!doctype html><meta charset="utf-8"><title>Course</title>
<div id="course"></div>
<script>
const courseRoot = document.getElementById('course').attachShadow({mode:'open'});
courseRoot.innerHTML = '<h1>Course readings</h1><div id="modules"></div>';
courseRoot.getElementById('modules').attachShadow({mode:'open'}).innerHTML =
  '<a href="welcome.html">Module 13</a>';
</script>
""");
        File.WriteAllText(Path.Combine(dir, "card.html"), """
<!doctype html><meta charset="utf-8"><title>VL test checkout</title><h1>Checkout</h1>
<label>Name on order <input id="nm" type="text"></label>
<label>Card number <input id="cc" autocomplete="cc-number"></label>
<button>Pay now</button> <a href="welcome.html">Next step</a>
""");
    }
}
