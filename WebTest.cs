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
        Environment.SetEnvironmentVariable("VOWENLITE_WEB_TESTDIR", dir);   // inherited by the host this process starts
        int fail = 0;
        var outs = new List<string>();
        string Do(params string[] a) { string o = WebBridge.Test(a.ToList()); outs.Add(o); return o; }
        void Check(string name, bool ok, string o = null) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) { fail++; if (o != null) Console.WriteLine("   got: " + o.Replace("\n", "\n   ")); } }
        static string Ref(string read, string pattern) { var m = Regex.Match(read, @"^\[(\d+)\][^\n]*" + pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase); return m.Success ? m.Groups[1].Value : "0"; }

        Vault.Save("vltest.example", User, Pw);
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
        Check("login for vltest.example refused on the look-alike", o.StartsWith("exit 126") && o.Contains("not an https page on vltest.example"), o);
        o = Do("login");
        Check("look-alike has no saved login", o.StartsWith("exit 3") && o.Contains("no saved login for evil.example"), o);

        o = Do("open", "https://login.vltest.example/card.html");
        Check("card field refused", Do("type", Ref(o, "payment-card"), "4111111111111111").Contains("payment-card field"));
        Check("Pay now refused", Do("click", Ref(o, "\"Pay now\"")).Contains("final pay/buy/order"));
        o = Do("type", Ref(o, "\"Name on order\""), "Test Person");
        Check("ordinary type works", o.StartsWith("exit 0") && o.Contains("typed 11"), o);
        o = Do("read");
        Check("typed value shows in read", o.Contains("= \"Test Person\""), o);
        o = Do("click", Ref(o, "\"Next step\""));
        Check("click navigates", o.Contains("welcome.html"), o);

        Check("file: blocked", Do("open", "file:///C:/Windows/win.ini").StartsWith("exit 126"));
        Check("loopback blocked", Do("open", "http://127.0.0.1:9/").StartsWith("exit 126"));
        Check("localhost blocked", Do("open", "https://localhost/").StartsWith("exit 126"));
        Check("private LAN blocked", Do("open", "192.168.1.1").StartsWith("exit 126"));
        o = Do("shot");
        Check("screenshot", o.StartsWith("exit 0") && File.Exists(Path.Combine(PcBridge.LunaDir, "web.png")) && new FileInfo(Path.Combine(PcBridge.LunaDir, "web.png")).Length > 2000, o);

        // expiry: the session cookie set by the welcome page must die with the entry
        o = Do("open", "https://login.vltest.example/welcome.html");
        Check("session cookie present before expiry", o.Contains("cookie: sess=vl"), o);
        var real = Vault.Now; Vault.Now = () => DateTime.UtcNow - TimeSpan.FromDays(8);
        Vault.Save("vltest.example", User, Pw);
        Vault.Now = real;
        Do("open", "https://login.vltest.example/index.html");
        o = Do("login");
        Check("expired entry refused", o.StartsWith("exit 3") && o.Contains("expired"), o);
        o = Do("open", "https://login.vltest.example/welcome.html");
        Check("expired site's cookies cleared", !o.Contains("sess=vl"), o);
        Check("vault entry kept as expired", Vault.List().Any(e => e.Site == "vltest.example" && e.Expired));

        Check("no reply ever contained the test password", outs.All(x => !x.Contains(Pw)));
        Check("daemon.log never contains the test password", !File.ReadAllText(Path.Combine(Paths.DataDir, "daemon.log")).Contains(Pw));
        Console.WriteLine(fail == 0 ? "ALL PASS" : fail + " FAILED");
        return fail == 0 ? 0 : 1;
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
</script>
""");
        File.WriteAllText(Path.Combine(dir, "welcome.html"), """
<!doctype html><meta charset="utf-8"><title>VL test home</title><h1>Welcome back</h1><p>You are signed in.</p>
<p id="c"></p><script>document.getElementById('c').textContent = 'cookie: ' + document.cookie; if (!document.cookie.includes('sess=')) document.cookie = 'sess=vl; max-age=86400; path=/; secure';</script>
""");
        File.WriteAllText(Path.Combine(dir, "mfa.html"), """
<!doctype html><meta charset="utf-8"><title>VL test check</title><h1>Two-step verification</h1><p>Enter the verification code we sent to your phone.</p><input name="code">
""");
        File.WriteAllText(Path.Combine(dir, "card.html"), """
<!doctype html><meta charset="utf-8"><title>VL test checkout</title><h1>Checkout</h1>
<label>Name on order <input id="nm" type="text"></label>
<label>Card number <input id="cc" autocomplete="cc-number"></label>
<button>Pay now</button> <a href="welcome.html">Next step</a>
""");
    }
}
