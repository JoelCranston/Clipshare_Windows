using Omaclip;

namespace ClipShare;

internal sealed class SettingsForm : Form
{
    private readonly AppModel model;
    private Preferences Preferences => model.Preferences;

    private readonly TableLayoutPanel layout = new();
    private readonly TextBox passphrase = new();
    private readonly Label passphraseMessage = new();
    private readonly TableLayoutPanel status = new();
    private readonly TextBox port = new();
    private readonly TextBox manualPeers = new();
    private readonly CheckBox autoCopy = new(), pasteAfter = new(), launchAtLogin = new();
    private readonly NumericUpDown maxHistory = new(), sharedCount = new();
    private readonly ShortcutRecorder showHistory, pasteLatest;
    private readonly Label shortcutWarning = new();
    private readonly Font headerFont = new("Segoe UI Semibold", 10.5F);
    private readonly Font noteFont = new("Segoe UI", 8.25F);
    private bool loading;

    public SettingsForm(AppModel model)
    {
        this.model = model;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "ClipShare Settings";
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(600, 680);
        MinimumSize = new Size(520, 420);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        using (var stream = typeof(SettingsForm).Assembly.GetManifestResourceStream("ClipShare.ico"))
            if (stream is not null) Icon = new Icon(stream);

        layout.Dock = DockStyle.Fill;
        layout.AutoScroll = true;
        layout.ColumnCount = 2;
        layout.Padding = new Padding(18, 8, 18 + SystemInformation.VerticalScrollBarWidth, 18);
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        // Sync passphrase
        Header("Sync passphrase");
        passphrase.UseSystemPasswordChar = true;
        passphrase.Dock = DockStyle.Fill;
        Row("Passphrase", passphrase);
        var savePassphrase = new Button { Text = "Save Passphrase", AutoSize = true };
        savePassphrase.Click += (_, _) => SavePassphrase(passphrase.Text);
        var buttons = Flow(savePassphrase);
        if (Preferences.OmaclipConfigPassphrase() is { } imported)
        {
            var useOmaclip = new Button { Text = "Use omaclip's Passphrase", AutoSize = true };
            useOmaclip.Click += (_, _) => SavePassphrase(imported);
            buttons.Controls.Add(useOmaclip);
        }
        passphrase.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            SavePassphrase(passphrase.Text);
            e.SuppressKeyPress = true;
        };
        Row("", buttons);
        passphraseMessage.ForeColor = Color.Firebrick;
        passphraseMessage.AutoSize = true;
        passphraseMessage.Visible = false;
        Row("", passphraseMessage);
        Note("Every machine in the mesh must use the same passphrase. It is stored in Windows Credential Manager.");

        // Status
        Header("Status");
        status.ColumnCount = 2;
        status.AutoSize = true;
        status.Dock = DockStyle.Fill;
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(status);
        layout.SetColumnSpan(status, 2);

        // Network
        Header("Network");
        port.Width = 90;
        port.PlaceholderText = "0 = random";
        Row("Sync port", port);
        manualPeers.Dock = DockStyle.Fill;
        manualPeers.PlaceholderText = "omarchy@omarchy.local:36742; laptop@100.101.102.103:36742";
        Row("Manual peers", manualPeers);
        var apply = new Button { Text = "Apply Network Settings", AutoSize = true };
        apply.Click += (_, _) => ApplyNetwork();
        Row("", Flow(apply));
        Note("Leave manual peers empty to discover omaclip on the local network with mDNS. For Tailscale or other VPNs, set a " +
             "fixed port here and on Linux (--sync-server-port), and list each machine as name@host:port, where host is an IP " +
             "address or a host name such as omarchy.local or a Tailscale MagicDNS name.");

        // Behavior
        Header("Behavior");
        autoCopy.Text = "Automatically copy new peer clipboard entries to this PC";
        autoCopy.AutoSize = true;
        autoCopy.CheckedChanged += (_, _) => { if (!loading) Preferences.AutoCopyRemote = autoCopy.Checked; };
        Row("", autoCopy);
        pasteAfter.Text = "Paste into the front app after choosing an entry";
        pasteAfter.AutoSize = true;
        pasteAfter.CheckedChanged += (_, _) => { if (!loading) Preferences.PasteAfterChoosing = pasteAfter.Checked; };
        Row("", pasteAfter);
        maxHistory.Minimum = 5;
        maxHistory.Maximum = 500;
        maxHistory.Increment = 5;
        maxHistory.Width = 80;
        maxHistory.ValueChanged += (_, _) => { if (!loading) Preferences.MaxHistory = (int)maxHistory.Value; };
        Row("Keep local entries", maxHistory);
        sharedCount.Minimum = 0;
        sharedCount.Maximum = 50;
        sharedCount.Width = 80;
        sharedCount.ValueChanged += (_, _) =>
        {
            if (loading) return;
            Preferences.SharedCount = (int)sharedCount.Value;
            model.PublishShared();
        };
        Row("Share newest with peers", sharedCount);
        launchAtLogin.Text = "Launch at login";
        launchAtLogin.AutoSize = true;
        launchAtLogin.CheckedChanged += (_, _) => { if (!loading) Preferences.LaunchAtLogin = launchAtLogin.Checked; };
        Row("", launchAtLogin);
        var clear = new Button { Text = "Clear Local History", AutoSize = true };
        clear.Click += (_, _) => model.ClearLocalHistory();
        Row("", Flow(clear));

        // Shortcuts
        Header("Shortcuts");
        showHistory = new ShortcutRecorder(Shortcut.ShowHistoryDefault, () => Preferences.ShowHistoryShortcut,
            value => Preferences.ShowHistoryShortcut = value);
        Row("Show clipboard history", showHistory.Panel);
        pasteLatest = new ShortcutRecorder(Shortcut.PasteLatestDefault, () => Preferences.PasteLatestShortcut,
            value => Preferences.PasteLatestShortcut = value);
        Row("Paste latest peer entry", pasteLatest.Panel);
        shortcutWarning.ForeColor = Color.Firebrick;
        shortcutWarning.AutoSize = true;
        Row("", shortcutWarning);
        Note("Click a shortcut, then press the new keys. Esc cancels, Backspace restores the default. Shortcuts need Win, Ctrl " +
             "or Alt (F-keys can be used alone). Letters follow your keyboard layout.");

        layout.RowCount = layout.Controls.Count;

        model.Changed += OnModelChanged;
        Preferences.Changed += OnModelChanged;
        HotKeyCenter.Shared.Changed += OnModelChanged;
        Load += (_, _) => LoadValues();
        Activated += (_, _) => RefreshStatus();
    }

    // MARK: Layout helpers

    private void Header(string text)
    {
        var label = new Label { Text = text, Font = headerFont, AutoSize = true, Margin = new Padding(0, 16, 0, 6) };
        layout.Controls.Add(label);
        layout.SetColumnSpan(label, 2);
    }

    private void Row(string caption, Control control)
    {
        layout.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) });
        control.Margin = new Padding(0, 3, 0, 3);
        layout.Controls.Add(control);
    }

    private void Note(string text)
    {
        var label = new Label { Text = text, Font = noteFont, ForeColor = SystemColors.GrayText, AutoSize = true, Margin = new Padding(0, 2, 0, 4) };
        label.MaximumSize = new Size(LogicalToDeviceUnits(540), 0);
        layout.Controls.Add(label);
        layout.SetColumnSpan(label, 2);
    }

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        flow.Controls.AddRange(controls);
        return flow;
    }

    // MARK: Values

    private void LoadValues()
    {
        loading = true;
        passphrase.PlaceholderText = Preferences.HasPassphrase ? "•••••••• (saved)" : "Same as on your Linux machines";
        port.Text = Preferences.SyncPort.ToString();
        manualPeers.Text = Preferences.ManualPeers;
        autoCopy.Checked = Preferences.AutoCopyRemote;
        pasteAfter.Checked = Preferences.PasteAfterChoosing;
        maxHistory.Value = Math.Clamp(Preferences.MaxHistory, 5, 500);
        sharedCount.Value = Preferences.SharedCount;
        launchAtLogin.Checked = Preferences.LaunchAtLogin;
        loading = false;
        RefreshStatus();
    }

    private void OnModelChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        loading = true;
        autoCopy.Checked = Preferences.AutoCopyRemote;
        loading = false;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        status.SuspendLayout();
        foreach (Control control in status.Controls) control.Dispose();
        status.Controls.Clear();
        void Line(string caption, string value, Color? color = null)
        {
            status.Controls.Add(new Label { Text = caption, AutoSize = true, Margin = new Padding(0, 4, 8, 4) });
            var text = new TextBox
            {
                Text = value, ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, TabStop = false,
                BackColor = BackColor, Margin = new Padding(0, 4, 0, 4),
            };
            if (color is { } c) text.ForeColor = c;
            status.Controls.Add(text);
        }
        Line("Status", model.NetworkStatus);
        if (model.ListeningPort is { } listening)
        {
            var addresses = IPv4.LocalAddresses().Where(a => !System.Net.IPAddress.IsLoopback(a)).Select(a => $"{a}:{listening}");
            Line("This PC", string.Join("   ", addresses));
        }
        if (model.KeyFingerprint is { } fingerprint) Line("Key fingerprint", fingerprint);
        foreach (var peer in model.PeerStatuses)
        {
            var address = $"{peer.Peer.Host}:{peer.Peer.Port}";
            var state = peer.LastError is { } error ? $"{address} — {error}"
                : peer.LastSync is { } date ? $"{address} — synced {date:T}"
                : $"{address} — connecting…";
            Line(peer.Peer.DisplayName, state, peer.LastError is null ? null : Color.Firebrick);
        }
        status.ResumeLayout();

        showHistory.Refresh();
        pasteLatest.Refresh();
        var conflicts = new List<string>();
        if (!HotKeyCenter.Shared.IsRegistered("showHistory")) conflicts.Add(Preferences.ShowHistoryShortcut.DisplayString);
        if (!HotKeyCenter.Shared.IsRegistered("pasteLatest")) conflicts.Add(Preferences.PasteLatestShortcut.DisplayString);
        shortcutWarning.Text = conflicts.Count == 0 ? "" : $"{string.Join(" and ", conflicts)} could not be registered; another app may be using it.";
    }

    private void SavePassphrase(string value)
    {
        try
        {
            SyncKey.Validate(value);
            Preferences.SetPassphrase(value);
            passphrase.Text = "";
            passphrase.PlaceholderText = "•••••••• (saved)";
            passphraseMessage.Visible = false;
            model.RestartNetworking();
        }
        catch (PassphraseException ex)
        {
            passphraseMessage.Text = ex.Message;
            passphraseMessage.Visible = true;
        }
    }

    private void ApplyNetwork()
    {
        var value = int.TryParse(port.Text.Trim(), out var parsed) && parsed is >= 0 and <= 65535 ? parsed : 0;
        Preferences.SyncPort = value;
        port.Text = value.ToString();
        Preferences.ManualPeers = manualPeers.Text.Trim();
        model.RestartNetworking();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            model.Changed -= OnModelChanged;
            Preferences.Changed -= OnModelChanged;
            HotKeyCenter.Shared.Changed -= OnModelChanged;
            headerFont.Dispose();
            noteFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>A button that records a new shortcut when clicked, plus a reset button. Esc cancels; Backspace restores the default.</summary>
internal sealed class ShortcutRecorder
{
    private readonly Shortcut defaultShortcut;
    private readonly Func<Shortcut> get;
    private readonly Action<Shortcut> set;
    private readonly RecorderButton button = new();
    private readonly Button reset = new() { Text = "↺", Width = 32, FlatStyle = FlatStyle.Flat };

    public FlowLayoutPanel Panel { get; } = new() { AutoSize = true, WrapContents = false, Margin = Padding.Empty };

    public ShortcutRecorder(Shortcut defaultShortcut, Func<Shortcut> get, Action<Shortcut> set)
    {
        this.defaultShortcut = defaultShortcut;
        this.get = get;
        this.set = set;
        button.MinimumSize = new Size(170, 0);
        button.AutoSize = true;
        button.Click += (_, _) => { if (button.Recording) Stop(); else Start(); };
        button.KeyPressed += OnKey;
        button.LostFocus += (_, _) => Stop();
        reset.FlatAppearance.BorderSize = 0;
        reset.Click += (_, _) => set(defaultShortcut);
        new ToolTip().SetToolTip(reset, "Reset to " + defaultShortcut.DisplayString);
        Panel.Controls.Add(button);
        Panel.Controls.Add(reset);
        Refresh();
    }

    public void Refresh()
    {
        button.Text = button.Recording ? "Type shortcut…" : get().DisplayString;
        reset.Visible = !button.Recording && get() != defaultShortcut;
    }

    private void Start()
    {
        button.Recording = true;
        // Let the current shortcuts reach the recorder instead of firing.
        HotKeyCenter.Shared.Suspend();
        Refresh();
    }

    private void Stop()
    {
        if (!button.Recording) return;
        button.Recording = false;
        HotKeyCenter.Shared.Resume();
        Refresh();
    }

    private void OnKey(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if (key == Keys.Escape)
        {
            Stop();
            return;
        }
        if (key is Keys.Back or Keys.Delete && (keyData & (Keys.Control | Keys.Alt)) == 0 && !Shortcut.WinHeld)
        {
            Stop();
            set(defaultShortcut);
            return;
        }
        if (Shortcut.FromKeyPress(keyData) is { } recorded)
        {
            Stop();
            set(recorded);
        }
        else if (key is not (Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin))
        {
            System.Media.SystemSounds.Beep.Play();
        }
    }

    private sealed class RecorderButton : Button
    {
        public bool Recording;
        public event Action<Keys>? KeyPressed;

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!Recording) return base.ProcessCmdKey(ref msg, keyData);
            KeyPressed?.Invoke(keyData);
            return true;
        }

        protected override bool IsInputKey(Keys keyData) => Recording || base.IsInputKey(keyData);
    }
}
