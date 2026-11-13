using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace VowenLite;

// Shows a model-written interactive page (calculator, chart, quiz, mini-game...) in its own window:
// `VowenLite.exe --widget <file.html> [title]`. Its own short-lived process, like the answer window.
// The page is sandboxed: a Content-Security-Policy blocks all network access, it can't navigate away
// or open windows, and it gets no access to files, so generated code can't send anything anywhere.
sealed class WidgetForm : Form
{
    const string Csp = "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; " +
        "img-src data: blob:; font-src data:; media-src data: blob:; connect-src 'none'; form-action 'none'; base-uri 'none'\">";

    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Bg };
    readonly string file, html;

    public WidgetForm(string file, string title)
    {
        this.file = file;
        html = File.ReadAllText(file);
        Text = string.IsNullOrWhiteSpace(title) ? "Luna" : title;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        ClientSize = new Size((int)Math.Min(wa.Width * 0.62, 1100), (int)Math.Min(wa.Height * 0.72, 760));
        MinimumSize = new Size(420, 320);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
        try { Icon = Theme.AppIcon(); } catch { }
        Controls.Add(web);
        Load += async (_, _) => await Start();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitleBar(Handle); }

    async Task Start()
    {
        try
        {
            string data = Path.Combine(Paths.DataDir, "webview");
            var env = await CoreWebView2Environment.CreateAsync(null, data);
            await web.EnsureCoreWebView2Async(env);
            var s = web.CoreWebView2.Settings;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            web.CoreWebView2.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("data:") && !e.Uri.StartsWith("about:")) e.Cancel = true; };
            web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            web.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            web.NavigateToString(WithCsp(html));
        }
        catch (Exception ex)
        {
            // No WebView2 runtime (or it failed to start): open the file in the default browser instead.
            try { File.AppendAllText(Path.Combine(Paths.DataDir, "daemon.log"), DateTime.Now.ToString("s") + " widget: webview failed: " + ex.Message + Environment.NewLine); } catch { }
            try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch { }
            Close();
        }
    }

    static string WithCsp(string page)
    {
        int head = page.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        if (head >= 0)
        {
            int close = page.IndexOf('>', head);
            if (close >= 0) return page.Insert(close + 1, Csp);
        }
        return Csp + page;
    }
}
