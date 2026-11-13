namespace VowenLite;

// `VowenLite.exe --vault` ("open passwords"): the logins Luna's background browser may use. A password is typed here once and never
// shown again (the list shows site, username and days left); each one dies 7 days after it was saved and only re-typing it revives it.
sealed class VaultForm : Form
{
    readonly RowList list = new();
    readonly TextBox site = Box(false), user = Box(false), pass = Box(true);
    readonly Pill save = new("Save", PillKind.Primary), remove = new("Remove", PillKind.Danger);
    readonly Label title = new() { Text = "Passwords", ForeColor = Theme.Ink, AutoSize = true, BackColor = Color.Transparent },
        sub = new() { Text = "Luna signs in with these in its own background browser. It never sees a password; each one lasts 7 days.", ForeColor = Theme.Soft, AutoSize = false, BackColor = Color.Transparent },
        status = new() { ForeColor = Theme.Soft, AutoSize = false, BackColor = Color.Transparent };
    readonly Panel[] frames = new Panel[3];
    readonly Label[] caps = new Label[3];
    bool armed;

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    static TextBox Box(bool secret) => new()
    {
        BorderStyle = BorderStyle.None, BackColor = Theme.S2, ForeColor = Theme.Ink, Font = Theme.Font(Theme.Body, 10.5f),
        UseSystemPasswordChar = secret, ShortcutsEnabled = !secret,
    };

    public VaultForm()
    {
        Text = "VowenLite passwords";
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        ClientSize = new Size(S(560), S(620));
        MinimumSize = new Size(S(460), S(520));
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Theme.AppIcon(); } catch { }
        title.Font = Theme.Font(Theme.Display, 20f);
        sub.Font = status.Font = Theme.Font(Theme.Body, 9.5f);
        string[] names = { "Sign-in page's site (github.com, accounts.google.com)", "Username or email", "Password" };
        TextBox[] boxes = { site, user, pass };
        for (int i = 0; i < 3; i++)
        {
            caps[i] = new Label { Text = names[i], ForeColor = Theme.Soft, Font = Theme.Font(Theme.Body, 9f), AutoSize = true, BackColor = Color.Transparent };
            var fr = frames[i] = new Panel { BackColor = Theme.S2 };
            fr.Controls.Add(boxes[i]);
            fr.Click += (_, _) => boxes[fr == frames[0] ? 0 : fr == frames[1] ? 1 : 2].Focus();
            Controls.Add(caps[i]); Controls.Add(fr);
        }
        Controls.AddRange(new Control[] { title, sub, list, save, remove, status });
        pass.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Save(); } };
        save.Click += (_, _) => Save();
        remove.Click += (_, _) => Remove();
        list.SelectedIndexChanged += (_, _) =>
        {
            armed = false; remove.Text = "Remove";
            if (list.SelectedRow?.Tag is Vault.Entry e) { site.Text = e.Site; user.Text = e.User; pass.Text = ""; status.Text = e.Expired ? "Expired: type the password again to revive it." : "To change it, type a new password and Save."; }
        };
        Layout += (_, _) => Arrange();
        Refill();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitleBar(Handle); }
    protected override void OnShown(EventArgs e) { base.OnShown(e); site.Focus(); }

    void Arrange()
    {
        int m = S(24), w = ClientSize.Width - m * 2, y = S(18);
        title.Location = new Point(m - S(2), y); y += title.Height + S(4);
        sub.Bounds = new Rectangle(m, y, w, S(40)); y += S(44);
        int formH = 3 * S(62) + S(100);
        list.Bounds = new Rectangle(m - S(8), y, w + S(16), Math.Max(S(80), ClientSize.Height - y - formH));
        y = list.Bottom + S(14);
        for (int i = 0; i < 3; i++)
        {
            caps[i].Location = new Point(m, y); y += caps[i].Height + S(4);
            frames[i].Bounds = new Rectangle(m, y, w, S(34));
            var b = (TextBox)frames[i].Controls[0];
            b.Bounds = new Rectangle(S(12), (frames[i].Height - b.PreferredHeight) / 2, w - S(24), b.PreferredHeight);
            y += S(34) + S(10);
        }
        save.Location = new Point(m, y + S(4));
        remove.Location = new Point(save.Right + S(8), y + S(4));
        status.Bounds = new Rectangle(remove.Right + S(12), y + S(4), Math.Max(S(40), ClientSize.Width - remove.Right - S(12) - m), S(36));
        status.TextAlign = ContentAlignment.MiddleLeft;
    }

    void Refill()
    {
        list.Items.Clear();
        foreach (var e in Vault.List())
            list.Items.Add(new RowList.Row
            {
                Title = e.Site,
                Sub = (e.User.Length > 0 ? e.User : "(no username)") + " · " + (e.Expired ? "Expired: re-enter" : Math.Ceiling(e.DaysLeft) + " day" + (Math.Ceiling(e.DaysLeft) == 1 ? "" : "s") + " left"),
                Dot = e.Expired ? Theme.Err : Theme.Ice,
                Tag = e,
            });
        if (list.Items.Count == 0) status.Text = "No saved logins yet.";
        remove.Enabled = list.Items.Count > 0;
    }

    void Save()
    {
        string err = Vault.Save(site.Text, user.Text.Trim(), pass.Text);
        pass.Text = "";
        if (err.Length > 0) { status.ForeColor = Theme.Err; status.Text = err; return; }
        status.ForeColor = Theme.Soft; status.Text = "Saved for 7 days.";
        Refill();
        site.Text = user.Text = "";
        site.Focus();
    }

    void Remove()
    {
        if (list.SelectedRow?.Tag is not Vault.Entry e) { status.Text = "Pick a login in the list first."; return; }
        if (!armed) { armed = true; remove.Text = "Click again to remove"; return; }
        Vault.Remove(e.Site, e.User);
        armed = false; remove.Text = "Remove";
        status.Text = "Removed (its browser session is cleared too).";
        site.Text = user.Text = pass.Text = "";
        Refill();
    }
}
