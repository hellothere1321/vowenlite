using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace VowenLite;

// `VowenLite.exe --webhost`: Luna's background browser (see WebBridge.cs). One WebView2 per tab inside a borderless tool window parked
// off every monitor: never on the taskbar or Alt+Tab, never activated, but "visible" so pages render and screenshots work (Chromium's
// occlusion throttling is switched off). Its own profile in <data>\browser-profile. Commands come one at a time over a named pipe that
// only this Windows account can open; each runs to completion on the UI thread, so a vault login (fill -> submit -> clear) is atomic.
// Element refs live in an isolated script world the page can't see or tamper with. Quits after 3 minutes without a command.
sealed class WebHost : Form
{
    sealed class Tab
    {
        public CoreWebView2Controller C;
        public CoreWebView2 W => C.CoreWebView2;
        public int Ctx, Starts, Ends;
        public string LastError;
    }

    static readonly TimeSpan Idle = TimeSpan.FromMinutes(3);
    const int MaxTabs = 6, TextChunk = 5000, VW = 1280, VH = 900;
    readonly List<Tab> tabs = new();
    readonly List<string> secrets = new();         // vault values used in this process: scrubbed from every reply
    readonly List<string> notes = new();           // dialogs, blocked downloads... reported with the next reply
    CoreWebView2Environment env;
    Tab cur;
    GuardProxy proxy;                              // H1: every request the browser makes is forced through this loopback proxy, which re-resolves and refuses private IPs
    bool loginBusy;                                // M1: a sign-in owns the browser start-to-finish; every other command is refused meanwhile
    DateTime lastCmd = DateTime.UtcNow, lastSweep = DateTime.MinValue;
    // Tests only: https://login.vltest.example etc. served from a folder. Honoured ONLY when the test harness set VOWENLITE_WEBTEST=1,
    // so a stray VOWENLITE_WEB_TESTDIR in a production environment can never redirect the real browser to a local folder.
    static readonly string TestDir = Environment.GetEnvironmentVariable("VOWENLITE_WEBTEST") == "1" ? Environment.GetEnvironmentVariable("VOWENLITE_WEB_TESTDIR") : null;

    public static int Run()
    {
        using var m = new Mutex(true, @"Local\VowenLite.WebHost." + Vault.Hash(Paths.DataDir.ToLowerInvariant()), out bool first);
        if (!first) return 0;
        Paths.SecureKnown();
        ApplicationConfiguration.Initialize();
        Application.Run(new WebHost());
        return 0;
    }

    WebHost()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        var vs = SystemInformation.VirtualScreen;
        Bounds = new Rectangle(vs.Left - VW - 4000, vs.Top, VW, VH);
        Text = "VowenLite web host";
        Load += async (_, _) => await Start();
        var idle = new System.Windows.Forms.Timer { Interval = 10_000 };
        idle.Tick += (_, _) => { if (DateTime.UtcNow - lastCmd > Idle) Close(); };
        idle.Start();
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x80 | 0x08000000; return p; } }   // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
    protected override void WndProc(ref Message m) { if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } base.WndProc(ref m); }    // WM_MOUSEACTIVATE -> MA_NOACTIVATE

    async Task Start()
    {
        try
        {
            Paths.Secure(Paths.ProfileDir, true);   // C1: lock the profile folder (cookies = sessions) before WebView2 writes anything into it
            string proxyArg = "";
            try { proxy = new GuardProxy(); proxy.Start(); proxyArg = $" --proxy-server=127.0.0.1:{proxy.Port} --proxy-bypass-list=<-loopback>"; }
            catch (Exception ex) { Log("web host: guard proxy failed to start, falling back to name/resolve checks only: " + ex.Message); }
            // H1: with a proxy set, Chromium hands every hostname (pages, redirects, subresources, WebSockets) to the proxy instead of
            // resolving and connecting itself; <-loopback> removes Chromium's default localhost bypass so even loopback goes through it.
            var opts = new CoreWebView2EnvironmentOptions("--disable-gpu --disable-features=CalculateNativeWinOcclusion --disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows" + proxyArg);
            env = await CoreWebView2Environment.CreateAsync(null, Paths.ProfileDir, opts);
            cur = await NewTab();
            await Sweep(true);
            new Thread(PipeLoop) { IsBackground = true, Name = "web pipe" }.Start();
        }
        catch (Exception ex) { Log("web host failed to start: " + ex.Message); Close(); }
    }

    static void Log(string s) => Luna.Log(s);

    async Task<Tab> NewTab()
    {
        var t = new Tab { C = await env.CreateCoreWebView2ControllerAsync(Handle) };
        t.C.Bounds = new Rectangle(0, 0, VW, VH);
        var s = t.W.Settings;
        s.AreDevToolsEnabled = false; s.AreDefaultContextMenusEnabled = false; s.IsStatusBarEnabled = false; s.AreHostObjectsAllowed = false;
        s.IsWebMessageEnabled = false; s.IsPasswordAutosaveEnabled = false; s.IsGeneralAutofillEnabled = false; s.AreDefaultScriptDialogsEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        if (TestDir != null)
            foreach (var h in new[] { "login.vltest.example", "vltest.example.evil.example" }) t.W.SetVirtualHostNameToFolderMapping(h, TestDir, CoreWebView2HostResourceAccessKind.Deny);
        t.W.NavigationStarting += (_, e) =>
        {
            string why = Blocked(e.Uri);
            if (why != null) { e.Cancel = true; t.LastError = why; notes.Add("blocked navigation to " + Trim(e.Uri, 80) + ": " + why); return; }
            t.Starts++;
        };
        t.W.NavigationCompleted += (_, e) =>
        {
            t.Ends = t.Starts;
            if (!e.IsSuccess) t.LastError = e.WebErrorStatus.ToString();
            try { var u = new Uri(t.W.Source); if (u.Scheme == "https" || u.Scheme == "http") Vault.NoteSite(Vault.Site(u.Host), u.GetLeftPart(UriPartial.Authority)); } catch { }
        };
        t.W.ContentLoading += (_, _) => t.Ctx = 0;
        t.W.DownloadStarting += (_, e) => { e.Cancel = true; notes.Add("a download (" + Path.GetFileName(e.ResultFilePath) + ") was blocked: downloads are off"); };
        t.W.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        t.W.BasicAuthenticationRequested += (_, e) => { e.Cancel = true; notes.Add("the site asked for a browser login box; that needs the user"); };
        t.W.ScriptDialogOpening += (_, e) => { notes.Add($"page {e.Kind.ToString().ToLowerInvariant()}: \"{Trim(e.Message, 200)}\"" + (e.Kind == CoreWebView2ScriptDialogKind.Alert ? " (closed)" : " (dismissed)")); if (e.Kind == CoreWebView2ScriptDialogKind.Alert) e.Accept(); };
        t.W.NewWindowRequested += async (_, e) =>
        {
            if (Blocked(e.Uri) is string why) { e.Handled = true; notes.Add("blocked popup: " + why); return; }
            if (tabs.Count >= MaxTabs) { e.Handled = true; t.W.Navigate(e.Uri); return; }
            var def = e.GetDeferral();
            try { var nt = await NewTab(); e.NewWindow = nt.W; e.Handled = true; Activate(nt); notes.Add($"the page opened a new tab (now tab {tabs.IndexOf(nt) + 1})"); }
            finally { def.Complete(); }
        };
        // Sub-requests to this PC / the local network are refused (so no page can reach the bridges or the router); main-frame checks are in Blocked.
        foreach (var pat in PrivatePatterns) t.W.AddWebResourceRequestedFilter(pat, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        t.W.WebResourceRequested += (_, e) => { if (Blocked(e.Request.Uri) != null) e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", ""); };
        await t.W.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", "{\"enabled\":true}");   // pages behave as if focused; the OS focus is never taken
        tabs.Add(t);
        Activate(t);
        return t;
    }

    static readonly string[] PrivatePatterns = new[] { "*://localhost*", "*://*.localhost*", "*://127.*", "*://10.*", "*://192.168.*", "*://169.254.*", "*://0.*", "*://[*" }
        .Concat(Enumerable.Range(16, 16).Select(n => $"*://172.{n}.*")).ToArray();

    void Activate(Tab t) { foreach (var x in tabs) x.C.IsVisible = x == t; cur = t; }

    // ---- what may be opened

    internal static string Blocked(string url)
    {
        if (string.IsNullOrEmpty(url) || url == "about:blank") return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "not a web address";
        if (u.Scheme == "data" || u.Scheme == "blob") return null;   // in-page content (data: pages can't reach the PC either)
        if (u.Scheme != "https" && u.Scheme != "http") return "only http(s) pages can be opened";
        return PrivateHost(u.Host) ? "addresses on this PC or the local network are off limits" : null;
    }

    internal static bool IsPrivateHostForTest(string host) => PrivateHost(host);   // --vaulttest hook

    static bool PrivateHost(string host)
    {
        string h = (host ?? "").Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (h.Length == 0) return false;
        if (h == "localhost" || h.EndsWith(".localhost") || h.EndsWith(".local") || h.EndsWith(".internal") || h.EndsWith(".lan") || h.EndsWith(".home.arpa")) return true;
        if (IPAddress.TryParse(h, out var ip) && PrivateIp(ip)) return true;
        return TryNumericIPv4(h, out var ip2) && PrivateIp(ip2);   // decimal/hex/octal literals a browser would canonicalise to 127.0.0.1 etc.
    }

    // inet_aton-style parse: "2130706433", "0x7f.0.0.1", "0177.0.0.1" and the mixed/short forms browsers accept. Non-numeric hosts -> false.
    static bool TryNumericIPv4(string host, out IPAddress ip)
    {
        ip = null;
        var parts = host.Split('.');
        if (parts.Length == 0 || parts.Length > 4) return false;
        var nums = new ulong[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i];
            if (p.Length == 0) return false;
            try
            {
                if (p.StartsWith("0x")) { if (p.Length == 2) return false; nums[i] = Convert.ToUInt64(p.Substring(2), 16); }
                else if (p[0] == '0' && p.Length > 1) { foreach (char c in p) if (c < '0' || c > '7') return false; nums[i] = Convert.ToUInt64(p, 8); }
                else { foreach (char c in p) if (c < '0' || c > '9') return false; nums[i] = ulong.Parse(p); }
            }
            catch { return false; }
        }
        int n = nums.Length;
        for (int i = 0; i < n - 1; i++) if (nums[i] > 255) return false;
        int remBytes = 4 - (n - 1);
        if (remBytes < 4 && nums[n - 1] >= (1UL << (8 * remBytes))) return false;
        uint addr = (uint)nums[n - 1];
        for (int i = 0; i < n - 1; i++) addr |= (uint)(nums[i] << (8 * (3 - i)));
        ip = new IPAddress(new[] { (byte)(addr >> 24), (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr });
        return true;
    }

    static bool PrivateIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] >= 224;
    }

    // A public-looking name that resolves to this PC / the LAN (e.g. "localtest.me") is refused too.
    static string ResolvesPrivate(string host)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out _) || (TestDir != null && host.EndsWith(".example"))) return null;
        try
        {
            var t = Dns.GetHostAddressesAsync(host);
            if (t.Wait(2500) && t.Result.Any(PrivateIp)) return "that name points at this PC or the local network, which is off limits";
        }
        catch { }
        return null;
    }

    // ---- the pipe

    void PipeLoop()
    {
        while (!IsDisposed)
        {
            try
            {
                using var ps = new NamedPipeServerStream(WebBridge.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                ps.WaitForConnection();
                var rd = new StreamReader(ps, Encoding.UTF8);
                var w = new StreamWriter(ps, new UTF8Encoding(false)) { AutoFlush = true };
                string line = rd.ReadLine();
                if (line == null) continue;
                var done = new TaskCompletionSource<(int, string)>();
                BeginInvoke(new Action(async () =>
                {
                    try { done.SetResult(await RunLine(line)); }
                    catch (Exception ex) { done.TrySetResult((125, "browser error: " + ex.Message)); }
                }));
                var (exit, o) = done.Task.Wait(120_000) ? done.Task.Result : (124, "the browser took too long");
                w.WriteLine(JsonSerializer.Serialize(new { exit, @out = Scrub(o, secrets) }));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { if (IsDisposed) return; Thread.Sleep(100); }
        }
    }

    internal static string Scrub(string s, List<string> secrets)
    {
        if (string.IsNullOrEmpty(s)) return s;
        foreach (var x in secrets)
        {
            if (string.IsNullOrEmpty(x) || x.Length < 3) continue;
            s = s.Replace(x, "[hidden]", StringComparison.OrdinalIgnoreCase);
            // A secret that leaked into a URL (GET form, redirect, error echo) is percent-encoded; strip those forms too (%20 and form '+').
            string enc = Uri.EscapeDataString(x);
            if (!string.Equals(enc, x, StringComparison.Ordinal) && enc.Length >= 3) s = s.Replace(enc, "[hidden]", StringComparison.OrdinalIgnoreCase);
            string form = enc.Replace("%20", "+");
            if (!string.Equals(form, enc, StringComparison.Ordinal) && form.Length >= 3) s = s.Replace(form, "[hidden]", StringComparison.OrdinalIgnoreCase);
        }
        return s;
    }

    async Task<(int, string)> RunLine(string line)
    {
        lastCmd = DateTime.UtcNow;
        using var d = JsonDocument.Parse(line);
        string cmd = d.RootElement.GetProperty("cmd").GetString();
        var a = d.RootElement.GetProperty("args").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        string text = d.RootElement.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString() : null;
        if (DateTime.UtcNow - lastSweep > TimeSpan.FromMinutes(1)) await Sweep(false);
        notes.Clear();
        var (exit, o) = await Do(cmd, a, text);
        lastCmd = DateTime.UtcNow;
        if (notes.Count > 0) o += "\n[" + string.Join("; ", notes.Distinct()) + "]";
        return (exit, o);
    }

    async Task<(int, string)> Do(string cmd, List<string> a, string text)
    {
        // M1: if a login that overran a client timeout is still running, its awaits let the message loop deliver this next command.
        // Refuse everything until the login's atomic fill->submit->clear->cleanup finishes, so nothing can peek at a filled field.
        if (loginBusy && cmd != "login") return (126, "a sign-in is still finishing in the browser; try again in a moment.");
        var t = cur;
        switch (cmd)
        {
            case "open":
            {
                if (a.Count == 0) return (2, "usage: open <url>");
                string url = a[0].Contains("://") ? a[0] : "https://" + a[0];
                if (Blocked(url) is string why) return (126, "refused: " + why);
                var u = new Uri(url);
                if (ResolvesPrivate(u.Host) is string why2) return (126, "refused: " + why2);
                t.LastError = null;
                await Nav(t, () => t.W.Navigate(url), 30_000);
                return await Read(t, 1);
            }
            case "read": return await Read(t, a.Count > 0 && int.TryParse(a[0], out int part) ? Math.Max(1, part) : 1);
            case "click":
            {
                if (a.Count == 0 || !int.TryParse(a[0].Trim('[', ']'), out int r)) return (2, "usage: click <ref>  (a number from the latest read)");
                var el = await Locate(t, r);
                if (el == null) return (1, $"no element [{r}] any more: the page changed. Run read again.");
                var e = el.Value;
                string info = e.GetProperty("info").GetRawText();
                using var id = JsonDocument.Parse(info);
                if (IsCard(id.RootElement)) return (126, "refused: that is a payment-card field. The user fills those in themselves.");
                string name = Str(id.RootElement, "name");
                if (FinalAction.IsMatch(name)) return (126, $"refused: \"{Trim(name, 60)}\" looks like a final pay/buy/order control. Get everything ready and tell the user to press it.");
                string before = t.W.Source;
                double x = e.GetProperty("x").GetDouble(), y = e.GetProperty("y").GetDouble();
                if (e.GetProperty("hit").GetBoolean()) await Mouse(t, x, y);
                else await Eval(t, $"(()=>{{const e=(window.__vl||[])[{r - 1}]; e && e.click(); return '1';}})()");
                await Settle(t);
                return (0, $"clicked [{r}] {Str(id.RootElement, "role")} \"{Trim(name, 60)}\"" + Changed(t, before));
            }
            case "type":
            {
                if (a.Count == 0 || !int.TryParse(a[0].Trim('[', ']'), out int r)) return (2, "usage: type <ref> <text>   or   type <ref> --file F");
                string s = text ?? string.Join(' ', a.Skip(1));
                var el = await Locate(t, r);
                if (el == null) return (1, $"no element [{r}] any more: the page changed. Run read again.");
                using var id = JsonDocument.Parse(el.Value.GetProperty("info").GetRawText());
                if (id.RootElement.GetProperty("secret").GetBoolean() || Str(id.RootElement, "type") == "password") return (126, "refused: that is a password field. Use `login` (the password comes from the user's vault); never type one.");
                if (IsCard(id.RootElement)) return (126, "refused: that is a payment-card field. The user types card details themselves.");
                if (Str(id.RootElement, "tag") == "select")
                {
                    string ok = await Eval(t, $"(()=>{{const e=(window.__vl||[])[{r - 1}]; const want={JsonSerializer.Serialize(s.Trim().ToLowerInvariant())}; const o=[...e.options].find(o=>o.text.trim().toLowerCase()===want)||[...e.options].find(o=>o.text.toLowerCase().includes(want)); if(!o) return 'no'; e.value=o.value; e.dispatchEvent(new Event('input',{{bubbles:true}})); e.dispatchEvent(new Event('change',{{bubbles:true}})); return o.text;}})()");
                    return ok == "no" ? (1, "no option like that in the list") : (0, $"picked \"{ok}\" in [{r}]");
                }
                await Eval(t, $"(()=>{{const e=(window.__vl||[])[{r - 1}]; e.focus(); if (e.select) e.select(); else {{ const g=e.ownerDocument.getSelection(); const rg=e.ownerDocument.createRange(); rg.selectNodeContents(e); g.removeAllRanges(); g.addRange(rg); }} return '1';}})()");
                if (s.Length == 0) await Key(t, "delete"); else await Cdp(t, "Input.insertText", new { text = s.Replace("\r", "") });
                await Task.Delay(150);
                return (0, $"typed {s.Length} characters into [{r}]");
            }
            case "key":
            {
                if (a.Count == 0) return (2, "usage: key enter | tab | escape | ctrl+a ...");
                string before = t.W.Source;
                foreach (var k in a) { string err = await Key(t, k); if (err != null) return (2, err); }
                await Settle(t);
                return (0, "pressed " + string.Join(' ', a) + Changed(t, before));
            }
            case "scroll":
            {
                string dir = a.Count > 0 ? a[0].ToLowerInvariant() : "down";
                double dy = dir == "up" ? -700 : dir == "down" ? 700 : int.TryParse(dir, out int n) ? -n * 120 : 700;
                await Cdp(t, "Input.dispatchMouseEvent", new { type = "mouseWheel", x = VW / 2, y = VH / 2, deltaX = 0, deltaY = dy });
                await Task.Delay(350);
                string pos = await Eval(t, "(()=>{const h=Math.max(1,document.documentElement.scrollHeight-innerHeight); return Math.round(100*scrollY/h)+'%';})()");
                return (0, "scrolled; now " + pos + " down the page. read shows the whole page text anyway.");
            }
            case "back":
            {
                if (!t.W.CanGoBack) return (1, "nothing to go back to");
                await Nav(t, () => t.W.GoBack(), 20_000);
                return (0, "back on: " + Trim(t.W.DocumentTitle, 80) + " — " + t.W.Source);
            }
            case "wait":
            {
                string w = string.Join(' ', a).Trim();
                if (int.TryParse(w, out int ms)) { await Task.Delay(Math.Clamp(ms, 50, 15_000)); return (0, "waited"); }
                if (w.Length == 0) { await Settle(t); return (0, "page settled"); }
                for (int i = 0; i < 60; i++)
                {
                    string got = await Eval(t, $"(()=>(document.body&&document.body.innerText.toLowerCase().includes({JsonSerializer.Serialize(w.ToLowerInvariant())}))?'1':'0')()");
                    if (got == "1") return (0, $"\"{w}\" is on the page");
                    await Task.Delay(250);
                }
                return (1, $"\"{w}\" didn't appear within 15 s");
            }
            case "shot":
            {
                var cap = Cdp(t, "Page.captureScreenshot", new { format = "png" });
                if (await Task.WhenAny(cap, Task.Delay(15_000)) != cap) return (1, "the screenshot timed out");
                using var j = JsonDocument.Parse(await cap);
                string png = Path.Combine(PcBridge.LunaDir, "web.png");
                File.WriteAllBytes(png, Convert.FromBase64String(j.RootElement.GetProperty("data").GetString()));
                return (0, $"screenshot of \"{Trim(t.W.DocumentTitle, 60)}\" ({VW}x{VH}) saved as {png}: open it with your image viewing tool.");
            }
            case "tabs": return (0, string.Join("\n", tabs.Select((x, i) => $"{i + 1}{(x == cur ? "*" : " ")} {Trim(x.W.DocumentTitle, 60)} — {x.W.Source}")));
            case "tab":
            {
                if (a.Count == 0 || !int.TryParse(a[0], out int n) || n < 1 || n > tabs.Count) return (2, $"usage: tab <1-{tabs.Count}>");
                Activate(tabs[n - 1]);
                return (0, $"tab {n}: {Trim(cur.W.DocumentTitle, 80)} — {cur.W.Source}");
            }
            case "close":
            {
                int n = a.Count > 0 && int.TryParse(a[0], out int v) ? v : tabs.IndexOf(cur) + 1;
                if (n < 1 || n > tabs.Count) return (2, "no such tab");
                var x = tabs[n - 1]; tabs.RemoveAt(n - 1); x.C.Close();
                if (tabs.Count == 0) await NewTab(); else Activate(tabs[Math.Min(n - 1, tabs.Count - 1)]);
                return (0, $"closed tab {n}; now on: {Trim(cur.W.DocumentTitle, 60)} — {cur.W.Source}");
            }
            case "login": return await Login(t, a.Count > 0 ? a[0] : null);
        }
        return (2, $"unknown command \"{cmd}\" (open, read, click, type, key, scroll, back, wait, shot, tabs, tab, close, login, logins)");
    }

    static readonly Regex FinalAction = new(@"^\s*(?:pay|buy|place (?:your )?order|purchase|complete (?:purchase|order|payment)|confirm (?:purchase|order|payment)|submit (?:order|payment)|buy now|pay now|checkout now|subscribe now)\b", RegexOptions.IgnoreCase);
    static readonly Regex CardRx = new(@"card.?num|cc.?num|cardnumber|\bcvv|\bcvc|security.?code|expir|exp.?(?:date|month|year)|\biban\b|routing|account.?number", RegexOptions.IgnoreCase);
    static bool IsCard(JsonElement e) => Str(e, "ac").StartsWith("cc-") || CardRx.IsMatch(Str(e, "id") + " " + Str(e, "name"));
    static string Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
    static string Trim(string s, int n) { s = Regex.Replace(s ?? "", @"\s+", " ").Trim(); return s.Length > n ? s.Substring(0, n - 1) + "…" : s; }

    string Changed(Tab t, string before) => t.W.Source != before ? $". Page is now \"{Trim(t.W.DocumentTitle, 70)}\" — {t.W.Source} (read it again: refs changed)" : "";

    // ---- browser plumbing

    async Task<string> Cdp(Tab t, string method, object prm) => await t.W.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(prm));

    async Task<int> Ctx(Tab t)
    {
        if (t.Ctx != 0) return t.Ctx;
        using var tree = JsonDocument.Parse(await Cdp(t, "Page.getFrameTree", new { }));
        string fid = tree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
        using var w = JsonDocument.Parse(await Cdp(t, "Page.createIsolatedWorld", new { frameId = fid, worldName = "vowenlite" }));
        return t.Ctx = w.RootElement.GetProperty("executionContextId").GetInt32();
    }

    // Runs a script in the isolated world (shared DOM, separate JS globals) and returns its string result.
    async Task<string> Eval(Tab t, string js)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                int ctx = await Ctx(t);
                using var r = JsonDocument.Parse(await Cdp(t, "Runtime.evaluate", new { expression = Prelude + js, contextId = ctx, returnByValue = true, awaitPromise = true }));
                if (r.RootElement.TryGetProperty("exceptionDetails", out var ex)) throw new Exception("page script failed: " + Trim(ex.GetRawText(), 200));
                var v = r.RootElement.GetProperty("result");
                return v.TryGetProperty("value", out var val) ? (val.ValueKind == JsonValueKind.String ? val.GetString() : val.GetRawText()) : "";
            }
            catch (Exception ex) when (i < 2 && Regex.IsMatch(ex.Message, "context|Cannot find|not found|destroyed", RegexOptions.IgnoreCase)) { t.Ctx = 0; await Task.Delay(200); }
        }
    }

    async Task<bool> Nav(Tab t, Action go, int ms)
    {
        var tcs = new TaskCompletionSource<bool>();
        EventHandler<CoreWebView2NavigationCompletedEventArgs> h = (_, e) => tcs.TrySetResult(e.IsSuccess);
        t.W.NavigationCompleted += h;
        try { go(); return await Task.WhenAny(tcs.Task, Task.Delay(ms)) == tcs.Task && tcs.Task.Result; }
        finally { t.W.NavigationCompleted -= h; await Task.Delay(400); }
    }

    // After a click / key: give the page time, and wait out a navigation if one started.
    async Task Settle(Tab t)
    {
        await Task.Delay(350);
        var until = DateTime.UtcNow.AddSeconds(15);
        while (t.Starts > t.Ends && DateTime.UtcNow < until) await Task.Delay(100);
        await Task.Delay(t.Starts > 0 ? 400 : 150);
    }

    async Task Mouse(Tab t, double x, double y)
    {
        await Cdp(t, "Input.dispatchMouseEvent", new { type = "mouseMoved", x, y });
        await Cdp(t, "Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 });
        await Cdp(t, "Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 });
    }

    async Task<string> Key(Tab t, string combo)
    {
        int mods = 0; string key = null, code = null; int vk = 0; string txt = null;
        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string p = raw.Trim().ToLowerInvariant();
            switch (p)
            {
                case "ctrl" or "control": mods |= 2; continue;
                case "alt": mods |= 1; continue;
                case "shift": mods |= 8; continue;
                case "meta" or "cmd" or "win": mods |= 4; continue;
            }
            (key, code, vk, txt) = p switch
            {
                "enter" or "return" => ("Enter", "Enter", 13, "\r"), "tab" => ("Tab", "Tab", 9, null), "esc" or "escape" => ("Escape", "Escape", 27, null),
                "space" => (" ", "Space", 32, " "), "backspace" => ("Backspace", "Backspace", 8, null), "delete" or "del" => ("Delete", "Delete", 46, null),
                "up" => ("ArrowUp", "ArrowUp", 38, null), "down" => ("ArrowDown", "ArrowDown", 40, null), "left" => ("ArrowLeft", "ArrowLeft", 37, null), "right" => ("ArrowRight", "ArrowRight", 39, null),
                "home" => ("Home", "Home", 36, null), "end" => ("End", "End", 35, null), "pageup" => ("PageUp", "PageUp", 33, null), "pagedown" => ("PageDown", "PageDown", 34, null),
                _ when p.Length == 1 && char.IsLetter(p[0]) => (p, "Key" + char.ToUpperInvariant(p[0]), char.ToUpperInvariant(p[0]), p),
                _ when p.Length == 1 && char.IsDigit(p[0]) => (p, "Digit" + p, p[0], p),
                _ => (null, null, 0, null),
            };
            if (key == null) return $"unknown key \"{raw}\"";
        }
        if (key == null) return "no key given";
        if ((mods & 7) != 0 && txt != "\r") txt = null;
        await Cdp(t, "Input.dispatchKeyEvent", new { type = txt != null ? "keyDown" : "rawKeyDown", modifiers = mods, key, code, windowsVirtualKeyCode = vk, text = txt ?? "" });
        await Cdp(t, "Input.dispatchKeyEvent", new { type = "keyUp", modifiers = mods, key, code, windowsVirtualKeyCode = vk });
        return null;
    }

    // Shared helpers for the isolated-world scripts. `desc` never carries the value of a password field.
    const string Prelude = @"var __d = (e) => { const tag = e.tagName.toLowerCase(), type = (e.getAttribute('type') || '').toLowerCase(), ac = (e.getAttribute('autocomplete') || '').toLowerCase();
  const role = e.getAttribute('role') || (tag === 'a' ? 'link' : tag === 'input' ? (['checkbox','radio','submit','button','reset','image','range','file'].includes(type) ? (type === 'submit' || type === 'image' || type === 'reset' ? 'button' : type) : 'textbox') : tag === 'textarea' ? 'textbox' : tag === 'select' ? 'select' : tag === 'summary' ? 'button' : tag);
  const secret = type === 'password' || ac.includes('password');
  const lab = (e.labels && e.labels[0] && e.labels[0].innerText) || '';
  let name = e.getAttribute('aria-label') || lab || e.getAttribute('placeholder') || e.getAttribute('title') || e.getAttribute('alt') || '';
  if (!name && tag !== 'input' && tag !== 'textarea' && tag !== 'select') name = e.innerText || e.textContent || '';
  if (!name && tag === 'input' && ['submit','button','reset'].includes(type)) name = e.value || '';
  if (!name) { const img = e.querySelector && e.querySelector('img[alt]'); name = (img && img.alt) || e.getAttribute('name') || ''; }
  name = name.replace(/\s+/g, ' ').trim().slice(0, 80);
  let val = null; if (!secret && ((tag === 'input' && !['checkbox','radio','submit','button','reset','image','hidden','file'].includes(type)) || tag === 'textarea')) val = (e.value || '').slice(0, 80);
  if (tag === 'select') val = e.selectedOptions && e.selectedOptions[0] ? e.selectedOptions[0].text.trim().slice(0, 60) : '';
  if (e.isContentEditable && tag !== 'input' && tag !== 'textarea') val = (e.innerText || '').slice(0, 80);
  return { role, tag, name, type, secret, filled: secret ? !!e.value : null, val, checked: ['checkbox','radio'].includes(type) ? e.checked : null, ac, id: ((e.id || '') + ' ' + (e.getAttribute('name') || '')).trim(), dis: !!e.disabled, href: tag === 'a' ? (e.getAttribute('href') || '').slice(0, 100) : null };
};
var __vis = (e) => { const r = e.getBoundingClientRect(); if (r.width < 2 || r.height < 2) return false; const s = getComputedStyle(e); return s.visibility !== 'hidden' && s.display !== 'none' && +s.opacity > 0.05; };
var __docs = () => { const out = [document]; for (let i = 0; i < out.length && out.length < 12; i++) for (const f of out[i].querySelectorAll('iframe,frame')) { try { if (f.contentDocument && f.contentWindow.location.protocol === location.protocol) out.push(f.contentDocument); } catch (e) {} } return out; };
";

    const string ReadJs = @"(() => {
  const sel = 'a[href],button,input:not([type=hidden]),select,textarea,summary,[role=button],[role=link],[role=checkbox],[role=radio],[role=tab],[role=menuitem],[role=option],[role=switch],[role=combobox],[role=textbox],[role=searchbox],[contenteditable=""""],[contenteditable=true]';
  const els = []; for (const d of __docs()) for (const e of d.querySelectorAll(sel)) { if (els.length >= 250) break; if (__vis(e)) els.push(e); }
  window.__vl = els;
  return JSON.stringify({ title: document.title, url: location.href, text: document.body ? document.body.innerText : '', list: els.map((e, i) => Object.assign({ i: i + 1 }, __d(e))) });
})()";

    async Task<(int, string)> Read(Tab t, int part)
    {
        string raw = await Eval(t, ReadJs);
        using var j = JsonDocument.Parse(raw);
        var r = j.RootElement;
        string text = Regex.Replace(Regex.Replace(Str(r, "text").Replace("\r", ""), @"[ \t]+", " "), @"\n\s*\n+", "\n").Trim();
        int parts = Math.Max(1, (text.Length + TextChunk - 1) / TextChunk);
        part = Math.Min(part, parts);
        string chunk = text.Length == 0 ? "(no visible text)" : text.Substring((part - 1) * TextChunk, Math.Min(TextChunk, text.Length - (part - 1) * TextChunk));
        var sb = new StringBuilder();
        sb.Append("Title: ").Append(Trim(Str(r, "title"), 120)).Append("\nURL: ").Append(Str(r, "url"));
        if (t.LastError != null && Str(r, "url").StartsWith("about:")) sb.Append("\n(navigation failed: ").Append(t.LastError).Append(')');
        if (tabs.Count > 1) sb.Append($"\nTab {tabs.IndexOf(t) + 1} of {tabs.Count}");
        sb.Append($"\n\nText{(parts > 1 ? $" (part {part} of {parts}; `read {Math.Min(part + 1, parts)}` for more)" : "")}:\n").Append(chunk);
        if (part == 1)
        {
            var list = r.GetProperty("list").EnumerateArray().ToList();
            sb.Append("\n\nElements").Append(list.Count >= 250 ? " (first 250)" : "").Append(":\n");
            foreach (var e in list) sb.Append(Describe(e)).Append('\n');
        }
        if (Regex.IsMatch(text, CheckRx, RegexOptions.IgnoreCase)) sb.Append("\n[this page may want a code, CAPTCHA or other check: that needs the user]");
        return (0, sb.ToString().TrimEnd());
    }

    // One element line: [12] textbox "Email" = "a@b.c". Password and card values are never printed, whatever the page script returned.
    internal static string Describe(JsonElement e)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(e.GetProperty("i").GetInt32()).Append("] ").Append(Str(e, "role"));
        string type = Str(e, "type");
        if (type is "email" or "search" or "tel" or "url" or "number" or "date") sb.Append('(').Append(type).Append(')');
        sb.Append(" \"").Append(Str(e, "name")).Append('"');
        bool secret = e.TryGetProperty("secret", out var s) && s.ValueKind == JsonValueKind.True || type == "password";
        if (secret) sb.Append(e.TryGetProperty("filled", out var f) && f.ValueKind == JsonValueKind.True ? " (password field, filled)" : " (password field, empty)");
        else if (IsCard(e)) sb.Append(" (payment-card field)");
        else if (e.TryGetProperty("val", out var v) && v.ValueKind == JsonValueKind.String && v.GetString().Length > 0) sb.Append(" = \"").Append(Trim(v.GetString(), 80)).Append('"');
        if (e.TryGetProperty("checked", out var c) && c.ValueKind is JsonValueKind.True or JsonValueKind.False) sb.Append(c.ValueKind == JsonValueKind.True ? " [x]" : " [ ]");
        if (e.TryGetProperty("dis", out var dd) && dd.ValueKind == JsonValueKind.True) sb.Append(" (disabled)");
        if (Str(e, "href") is { Length: > 0 } h && !h.StartsWith("javascript:") && !h.StartsWith("#")) sb.Append(" -> ").Append(h);
        return sb.ToString();
    }

    // Scrolls element [r] into view; returns its centre in viewport pixels, whether a click there would hit it, and its description.
    async Task<JsonElement?> Locate(Tab t, int r)
    {
        string raw = await Eval(t, $@"(() => {{ const e = (window.__vl || [])[{r - 1}]; if (!e || !e.isConnected) return 'null';
  e.scrollIntoView({{ block: 'center', inline: 'center' }}); const b = e.getBoundingClientRect(); let x = b.left + b.width / 2, y = b.top + b.height / 2;
  let w = e.ownerDocument.defaultView; while (w && w.frameElement) {{ const f = w.frameElement.getBoundingClientRect(); x += f.left; y += f.top; w = w.parent; }}
  let hit = false; if (e.ownerDocument === document) {{ const h = document.elementFromPoint(x, y); hit = !!h && (h === e || e.contains(h) || h.contains(e)); }}
  return JSON.stringify({{ x, y, hit, info: __d(e) }}); }})()");
        if (raw == "null" || raw.Length == 0) return null;
        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    // ---- vault login

    const string CheckRx = @"verification code|two-?factor|2-?step|\b2fa\b|authenticator app|one-?time (?:code|password)|security code|enter the code|we (?:sent|texted|emailed) (?:you )?a code|captcha|i'?m not a robot|verify (?:it'?s you|you'?re human|that you)|confirm (?:it'?s you|your identity)|passkey";

    // Finds the sign-in fields (main page + same-site frames) and stores them for the fill step. Also reports where the form would
    // submit (action URL + method), so the caller can refuse a form that posts off-site or puts the password in the URL (GET).
    const string FindLoginJs = @"(() => {
  const ins = []; for (const d of __docs()) for (const e of d.querySelectorAll('input')) if (__vis(e) && !e.disabled && !e.readOnly) ins.push(e);
  const pass = ins.find(e => (e.type || '').toLowerCase() === 'password' && !/new-password/.test(e.autocomplete || ''));
  const userRx = /user|email|login|account|identifier|phone|mail/i;
  const cands = ins.filter(e => ['text','email','tel',''].includes((e.getAttribute('type') || '').toLowerCase()) && (/username|email/.test(e.autocomplete || '') || (e.type || '') === 'email' || userRx.test((e.id || '') + ' ' + (e.name || '') + ' ' + (e.getAttribute('aria-label') || '') + ' ' + (e.placeholder || ''))));
  let user = null;
  if (pass) { const before = cands.filter(e => e.compareDocumentPosition(pass) & Node.DOCUMENT_POSITION_FOLLOWING); user = before.length ? before[before.length - 1] : null; } else user = cands[0] || null;
  window.__vlLogin = { user, pass };
  const form = (pass && pass.form) || (user && user.form) || null;
  let action = location.href, method = null;
  if (form) { method = (form.getAttribute('method') || '').toLowerCase(); try { action = new URL(form.getAttribute('action') || '', (form.ownerDocument.defaultView || window).location.href).href; } catch (e) { action = ''; } }
  const top = location.href, pf = pass ? pass.ownerDocument.defaultView.location.href : null;
  return JSON.stringify({ top, user: !!user, userVal: user ? user.value : '', pass: !!pass, passFrame: pf, action, method, crossFrame: [...document.querySelectorAll('iframe')].some(f => { try { return !f.contentDocument; } catch (e) { return true; } }) });
})()";

    const string ClearPassJs = @"(() => { let n = 0; for (const d of __docs()) { for (const e of d.querySelectorAll('input[type=password]')) if (e.value) { e.value = ''; e.dispatchEvent(new Event('input', { bubbles: true })); n++; } } if (window.__vlLogin) window.__vlLogin = null; return String(n); })()";

    async Task<(int, string)> Login(Tab host, string siteArg)
    {
        Vault.Sweep();
        await Sweep(false);
        string pageSite = Uri.TryCreate(host.W.Source, UriKind.Absolute, out var pu) ? Vault.Site(pu.Host) : "";
        string site = string.IsNullOrWhiteSpace(siteArg) ? pageSite : Vault.Site(siteArg);
        if (site.Length == 0) return (2, "open the site's sign-in page first, then run `login`.");
        var entry = Vault.Find(site);
        if (entry == null) return (3, $"no saved login for {site}: ask the user to add it (they can say \"open passwords\"). Don't ask them for the password.");
        if (entry.Expired) return (3, $"the saved login for {site} expired (logins last 7 days): ask the user to re-enter it in the vault (\"open passwords\").");
        string eHost = string.IsNullOrEmpty(entry.Host) ? entry.Site : entry.Host;
        if (!Vault.EntryMatches(entry, host.W.Source)) return (126, $"refused: the open page ({Trim(host.W.Source, 80)}) is not an https page on {eHost}. Open {eHost}'s own sign-in page first.");

        // M1/M3: the whole fill runs here, start to finish; a throwaway tab holds the filled page so its history/BFCache and any
        // URL that gained the password (GET form / redirect) are destroyed when we close it, never reachable by a later command.
        loginBusy = true;
        var hardStop = DateTime.UtcNow.AddSeconds(70);
        Tab lt = null; string password = null, text = "", finalUrl = "", finalTitle = "";
        bool passStill = true, success = false;
        (int, string)? refusal = null;
        try
        {
            lt = await NewTab();
            await Nav(lt, () => lt.W.Navigate(host.W.Source), 30_000);
            if (!Vault.EntryMatches(entry, lt.W.Source)) { refusal = (126, $"refused: the page is not on {eHost}."); goto done; }
            for (int step = 0; step < 2 && DateTime.UtcNow < hardStop; step++)
            {
                using var f = JsonDocument.Parse(await Eval(lt, FindLoginJs));
                var r = f.RootElement;
                if (!Vault.EntryMatches(entry, Str(r, "top")) || !Vault.EntryMatches(entry, lt.W.Source)) { refusal = (126, $"refused: the page left {eHost}."); goto done; }
                bool hasPass = r.GetProperty("pass").GetBoolean(), hasUser = r.GetProperty("user").GetBoolean();
                if (hasPass && !Vault.EntryMatches(entry, Str(r, "passFrame"))) { refusal = (126, "refused: the password box is in a frame from another site. That needs the user."); goto done; }
                if (!hasPass && !hasUser)
                {
                    if (step == 0) { refusal = (1, r.GetProperty("crossFrame").GetBoolean() ? "the sign-in form is inside an embedded frame from another site, which the vault won't fill: that needs the user." : "no sign-in form on this page. Click the site's Sign in / Log in link first, then run `login`."); goto done; }
                    break;
                }
                // H2: never let the password be submitted to another host, and never via an explicit GET (which would put it in the URL).
                // A form with no method attribute is nearly always submitted by the page's script, so it is allowed; if it does turn
                // out to be a plain GET, the URL stays on this host, dies with the throwaway tab and is scrubbed from every reply.
                if (hasPass)
                {
                    string action = Str(r, "action"), method = Str(r, "method");
                    if (method == "get") { refusal = (126, "refused: this sign-in form uses GET, which would put the password in the URL. That needs the user."); goto done; }
                    if (!string.IsNullOrEmpty(action) && !Vault.EntryMatches(entry, action)) { refusal = (126, $"refused: the sign-in form submits to {Trim(action, 80)}, not {eHost}. That needs the user."); goto done; }
                }
                if (hasUser && entry.User.Length > 0 && !string.Equals(Str(r, "userVal"), entry.User, StringComparison.OrdinalIgnoreCase))
                    await Fill(lt, "user", entry.User, false);
                if (!secrets.Contains(entry.User) && entry.User.Length > 0) secrets.Add(entry.User);
                if (hasPass)
                {
                    password ??= Vault.Reveal(entry);
                    if (password == null) { refusal = (3, $"the saved login for {eHost} expired: ask the user to re-enter it (\"open passwords\")."); goto done; }
                    if (!secrets.Contains(password)) secrets.Add(password);
                    if (!Vault.EntryMatches(entry, lt.W.Source)) { refusal = (126, $"refused: the page left {eHost}."); goto done; }
                    if (!await Fill(lt, "pass", password, true)) { refusal = (126, "refused: couldn't confirm the focus was on this page's password box; that needs the user."); goto done; }
                    await Key(lt, "enter");
                    await Settle(lt);
                    await Task.Delay(800);
                    break;
                }
                // username-first sign-in (Google, Microsoft...): submit the name, wait for the password page
                await Key(lt, "enter");
                await Settle(lt);
                bool appeared = false;
                for (int i = 0; i < 24 && !appeared && DateTime.UtcNow < hardStop; i++) { await Task.Delay(250); appeared = (await Eval(lt, "(()=>{for(const d of __docs())for(const e of d.querySelectorAll('input[type=password]'))if(__vis(e))return '1';return '0';})()")) == "1"; }
                if (!appeared) break;
            }
            text = await Eval(lt, "(()=>document.body?document.body.innerText.slice(0,4000):'')()");
            passStill = (await Eval(lt, "(()=>{for(const d of __docs())for(const e of d.querySelectorAll('input[type=password]'))if(__vis(e))return '1';return '0';})()")) == "1";
            finalUrl = lt.W.Source; finalTitle = lt.W.DocumentTitle;
            success = !passStill && !Regex.IsMatch(text, CheckRx, RegexOptions.IgnoreCase);
done:;
        }
        finally
        {
            try { if (lt != null) await Eval(lt, ClearPassJs); } catch { }
            if (lt != null) { try { tabs.Remove(lt); lt.C.Close(); } catch { } }
            Activate(host);
            if (password != null) { secrets.Remove(password); password = null; }   // the plaintext leaves memory the moment the fill is done
            loginBusy = false;
        }
        if (refusal is { } rf) return rf;

        // On success, show the model the page the throwaway landed on (cookies are profile-wide, so the host tab is signed in too).
        if (success && Vault.EntryMatches(entry, finalUrl)) { try { await Nav(host, () => host.W.Navigate(finalUrl), 30_000); } catch { } }
        string where = $" (page: \"{Trim(host.W.DocumentTitle, 60)}\" — {host.W.Source})";
        Log($"web host: login on {eHost} -> " + (Regex.IsMatch(text, CheckRx, RegexOptions.IgnoreCase) ? "needs the user" : passStill ? "form still there" : "ok"));
        if (Regex.IsMatch(text, CheckRx, RegexOptions.IgnoreCase))
            return (3, $"needs the user: {eHost} is asking for a verification code / CAPTCHA / extra check after the password. Tell the user; don't try to get past it." + where);
        if (passStill)
        {
            var err = Regex.Match(text, @"[^\n]*(?:incorrect|invalid|wrong|didn'?t match|doesn'?t match|not recognized|try again|locked)[^\n]*", RegexOptions.IgnoreCase);
            return (1, "sign-in didn't go through" + (err.Success ? $": the site says \"{Trim(err.Value, 140)}\"" : "") + ". Don't retry more than once; if it fails again tell the user the saved login may be wrong (they can fix it with \"open passwords\")." + where);
        }
        return (0, $"signed in as {Vault.Mask(entry.User)} on {eHost}" + where);
    }

    // Focuses the stored field and types into it, but only after confirming the focus actually landed on that element (H3) and,
    // for the password, that the focused element really is a password box — so an SPA re-render can't divert the secret elsewhere.
    async Task<bool> Fill(Tab t, string which, string value, bool secret)
    {
        string ok = await Eval(t, $@"(()=>{{const e=window.__vlLogin&&window.__vlLogin.{which}; if(!e||!e.isConnected) return 'gone';
  e.focus(); if (e.select) e.select();
  const d=e.ownerDocument; if (d.activeElement!==e) return 'nofocus';
  const ty=(e.getAttribute('type')||'').toLowerCase();
  if ({(secret ? "true" : "false")} && ty!=='password') return 'notpw';
  return 'ok';}})()");
        if (ok != "ok") return false;
        await Cdp(t, "Input.insertText", new { text = value });
        await Task.Delay(120);
        return true;
    }

    // ---- 7-day sessions: wipe the cookies and storage of expired vault sites and of any site whose cookies were first seen 7+ days ago

    async Task Sweep(bool startup)
    {
        lastSweep = DateTime.UtcNow;
        try
        {
            Vault.Sweep();
            var t = tabs.FirstOrDefault();
            if (t == null) return;
            var cookies = await t.W.CookieManager.GetCookiesAsync(null);
            foreach (var site in cookies.Select(c => Vault.Site(c.Domain.TrimStart('.'))).Where(s => s.Length > 0).Distinct()) if (!Vault.Known(site)) Vault.NoteSite(site);
            var due = Vault.DueClears();
            if (due.Count == 0) return;
            // M4: once ANYTHING is due to expire, wipe the WHOLE profile — every cookie and all storage for every origin we can name.
            // Partitioned/SSO cookies and storage a per-site wipe would miss (e.g. accounts.google.com backing a site you signed into)
            // die too. This is the simplest option that guarantees the expired session cannot survive anywhere in the profile.
            int n = 0;
            foreach (var c in cookies) { try { t.W.CookieManager.DeleteCookie(c); n++; } catch { } }
            var origins = cookies.Select(c => "https://" + c.Domain.TrimStart('.'))
                .Concat(due.SelectMany(d => d.Origins))
                .Concat(due.SelectMany(d => new[] { "https://" + d.Site, "https://www." + d.Site }))
                .Where(o => o.Length > 8).Distinct().ToList();
            foreach (var o in origins) try { await Cdp(t, "Storage.clearDataForOrigin", new { origin = o, storageTypes = "all" }); } catch { }
            try { await Cdp(t, "Network.clearBrowserCache", new { }); } catch { }
            foreach (var (site, _) in due) Vault.Cleared(site);
            Log($"web host: wiped the profile ({n} cookies, {origins.Count} origins) because {due.Count} session(s) expired");
        }
        catch (Exception ex) { Log("web host: sweep failed: " + ex.Message); }
    }

    // ---- H1: in-process forward proxy on loopback. Chromium (started with --proxy-server pointing here) sends every connection here;
    // we re-resolve the host, refuse any that maps to loopback/private/link-local/CGNAT/ULA, and connect to the exact validated IP, so
    // a public URL that redirects to a private one, or a DNS name that rebinds to 127.0.0.1, can never reach the PC or the bridges.
    // The sandbox account can also reach this loopback port, but that gains it nothing: the proxy only ever connects onward to public IPs.
    sealed class GuardProxy
    {
        TcpListener listener;
        public int Port { get; private set; }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(Accept);
        }

        async Task Accept()
        {
            while (true)
            {
                TcpClient c;
                try { c = await listener.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(() => Serve(c));
            }
        }

        async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    var cs = client.GetStream();
                    var (line, head, leftover) = await ReadHead(cs);
                    if (line == null) return;
                    var parts = line.Split(' ');
                    if (parts.Length < 3) { await Deny(cs, 400, "Bad Request"); return; }
                    string method = parts[0], target = parts[1];

                    string host; int port; bool connect = method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase);
                    if (connect)
                    {
                        int ci = target.LastIndexOf(':');
                        host = ci > 0 ? target.Substring(0, ci) : target;
                        port = ci > 0 && int.TryParse(target.Substring(ci + 1), out int pp) ? pp : 443;
                    }
                    else if (Uri.TryCreate(target, UriKind.Absolute, out var au)) { host = au.Host; port = au.Port > 0 ? au.Port : 80; }
                    else { await Deny(cs, 400, "Bad Request"); return; }

                    var ip = Resolve(host);
                    if (ip == null) { await Deny(cs, connect ? 403 : 403, "Forbidden"); Log($"web host: proxy refused {host} (private/unresolvable)"); return; }

                    using var up = new TcpClient();
                    await up.ConnectAsync(ip, port);
                    up.NoDelay = true;
                    var us = up.GetStream();

                    if (connect)
                    {
                        byte[] ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                        await cs.WriteAsync(ok);
                    }
                    else
                    {
                        // Rewrite the absolute-form request line to origin-form and forward the original headers/body bytes.
                        string path = Uri.TryCreate(target, UriKind.Absolute, out var u2) ? u2.PathAndQuery : target;
                        byte[] reqLine = Encoding.ASCII.GetBytes($"{method} {path} {parts[2]}\r\n");
                        await us.WriteAsync(reqLine);
                        await us.WriteAsync(head);        // headers (includes Host) + any body bytes already read
                        if (leftover.Length > 0) await us.WriteAsync(leftover);
                    }

                    await Task.WhenAny(Pump(cs, us), Pump(us, cs));
                }
                catch { }
            }
        }

        // Validate the destination: refuse if the host, or ANY address it resolves to, is private (defeats rebinding). Return the IP to use.
        static IPAddress Resolve(string host)
        {
            string h = (host ?? "").Trim('[', ']');
            if (PrivateHost(h)) return null;
            if (IPAddress.TryParse(h, out var lit)) return PrivateIp(lit) ? null : lit;
            if (TryNumericIPv4(h, out var num)) return PrivateIp(num) ? null : num;
            IPAddress[] addrs;
            try { var t = Dns.GetHostAddressesAsync(h); if (!t.Wait(4000)) return null; addrs = t.Result; } catch { return null; }
            if (addrs.Length == 0 || addrs.Any(PrivateIp)) return null;
            return addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs[0];
        }

        static async Task Deny(NetworkStream cs, int code, string msg)
        {
            try { await cs.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {msg}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")); } catch { }
        }

        static async Task Pump(NetworkStream a, NetworkStream b)
        {
            var buf = new byte[16384];
            try { int n; while ((n = await a.ReadAsync(buf)) > 0) await b.WriteAsync(buf.AsMemory(0, n)); } catch { }
            try { b.Close(); } catch { }
        }

        // Read up to the end of the request headers; returns the request line, the raw header block (with trailing CRLFCRLF) and any extra bytes.
        static async Task<(string Line, byte[] Head, byte[] Leftover)> ReadHead(NetworkStream s)
        {
            var buf = new List<byte>();
            var one = new byte[4096];
            while (buf.Count < 64 * 1024)
            {
                int n = await s.ReadAsync(one);
                if (n <= 0) break;
                buf.AddRange(one.AsSpan(0, n).ToArray());
                int idx = IndexOfCrlfCrlf(buf);
                if (idx >= 0)
                {
                    byte[] all = buf.ToArray();
                    int headEnd = idx + 4;
                    string block = Encoding.ASCII.GetString(all, 0, headEnd);
                    int firstEol = block.IndexOf("\r\n", StringComparison.Ordinal);
                    string line = firstEol > 0 ? block.Substring(0, firstEol) : block.TrimEnd();
                    return (line, all[..headEnd], all[headEnd..]);
                }
            }
            return (null, null, null);
        }

        static int IndexOfCrlfCrlf(List<byte> b)
        {
            for (int i = 0; i + 3 < b.Count; i++) if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
            return -1;
        }
    }
}
