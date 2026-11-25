using System.Diagnostics;
using static LaunchHere.Native;

namespace LaunchHere;

/// <summary>The settings window: allow/block list, options and timings. Edits a copy of the
/// config; OK/Apply hand the copy to the tray app, which saves and applies it live.</summary>
internal sealed class SettingsForm : Form
{
    readonly Config _cfg;
    readonly Action<Config> _onApply;
    readonly Logger _log;

    readonly RadioButton _blacklist, _whitelist;
    readonly ListView _apps;
    readonly ImageList _icons;
    readonly TextBox _addBox;
    readonly Button _remove;
    readonly CheckBox _watchTaskbar, _watchDesktop, _moveOpen, _startup, _debugLog;
    readonly NumericUpDown _armTimeout, _grace, _cancel, _shrink;
    readonly Button _applyBtn;
    readonly Dictionary<string, string> _iconKeys = new(StringComparer.OrdinalIgnoreCase);

    public SettingsForm(Config cfg, Logger log, Icon? icon, Action<Config> onApply)
    {
        _cfg = cfg.Clone();
        _log = log;
        _onApply = onApply;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Text = "LaunchHere Settings";
        if (icon != null) Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1 };

        // ---------------------------------------------------------------- apps
        _blacklist = new RadioButton { Text = "Move every app except the ones listed below", AutoSize = true };
        _whitelist = new RadioButton { Text = "Move only the apps listed below", AutoSize = true };
        _icons = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = IconSize(DeviceDpi) };
        _apps = new ListView
        {
            View = View.Details, FullRowSelect = true, HideSelection = false, HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Size = new Size(400, 190), SmallImageList = _icons, Margin = new Padding(3, 6, 3, 3),
        };
        _apps.Columns.Add("App", 150);
        _apps.Columns.Add("Matches", 230);
        ScaleColumns(this, _apps);
        _apps.SelectedIndexChanged += (_, _) => _remove!.Enabled = _apps.SelectedItems.Count > 0;
        _apps.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };

        var addRunning = SideButton("Add running app...");
        addRunning.Click += (_, _) => AddRunning();
        var browse = SideButton("Browse for app...");
        browse.Click += (_, _) => Browse();
        _remove = SideButton("Remove"); _remove.Enabled = false;
        _remove.Click += (_, _) => RemoveSelected();
        var side = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(3, 3, 0, 0) };
        side.Controls.Add(addRunning);
        side.Controls.Add(browse);
        side.Controls.Add(_remove);

        _addBox = new TextBox { Width = 400, Anchor = AnchorStyles.Left | AnchorStyles.Right, PlaceholderText = "or type an exe name, e.g. chrome.exe" };
        var addTyped = SideButton("Add"); addTyped.Margin = new Padding(3, 3, 0, 3);
        addTyped.Click += (_, _) => AddTyped();
        _addBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { AddTyped(); e.SuppressKeyPress = true; } };

        var appsGrid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
        appsGrid.Controls.Add(_blacklist, 0, 0); appsGrid.SetColumnSpan(_blacklist, 2);
        appsGrid.Controls.Add(_whitelist, 0, 1); appsGrid.SetColumnSpan(_whitelist, 2);
        appsGrid.Controls.Add(_apps, 0, 2);
        appsGrid.Controls.Add(side, 1, 2);
        appsGrid.Controls.Add(_addBox, 0, 3);
        appsGrid.Controls.Add(addTyped, 1, 3);
        var hint = Hint("An exe name matches every copy of that app. A full path matches only that copy.");
        appsGrid.Controls.Add(hint, 0, 4); appsGrid.SetColumnSpan(hint, 2);
        Section(root, "Apps", appsGrid, first: true);

        // ---------------------------------------------------------------- options
        _watchTaskbar = Check("Move apps opened from the taskbar, jump lists and the Start menu");
        _watchDesktop = Check("Move apps opened from desktop shortcuts");
        _moveOpen = Check("Bring an open window to the taskbar where its icon or preview is clicked");
        _startup = Check("Start LaunchHere with Windows");
        var options = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        options.Controls.AddRange(new Control[] { _watchTaskbar, _watchDesktop, _moveOpen, _startup });
        Section(root, "Options", options);

        // ---------------------------------------------------------------- advanced
        _armTimeout = Number(500, 60000, 0, 500);
        _grace = Number(0, 30000, 0, 500);
        _cancel = Number(100, 10000, 0, 100);
        _shrink = Number(0, 1, 2, 0.05m);
        _debugLog = Check("Detailed log (every window LaunchHere looks at)");
        var adv = new TableLayoutPanel { AutoSize = true, ColumnCount = 3 };
        void Row(string label, Control input, string unit)
        {
            int r = adv.RowCount++;
            adv.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, r);
            adv.Controls.Add(input, 1, r);
            adv.Controls.Add(new Label { Text = unit, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText }, 2, r);
        }
        Row("Wait for the app's window up to", _armTimeout, "ms (slow-starting apps need more)");
        Row("Also move the app's next windows for", _grace, "ms (splash screen, then main window)");
        Row("A click counts as a switch to an open window for", _cancel, "ms");
        Row("Too wide for the monitor: also shrink the height by", _shrink, "(0 = keep the height, 1 = in proportion)");
        int lr = adv.RowCount++;
        adv.Controls.Add(_debugLog, 0, lr); adv.SetColumnSpan(_debugLog, 3);
        var link = new LinkLabel { Text = "Open config.json (all settings, including re-check timings)", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(Config.FilePath) { UseShellExecute = true });
        int kr = adv.RowCount++;
        adv.Controls.Add(link, 0, kr); adv.SetColumnSpan(link, 3);
        Section(root, "Advanced", adv);

        // ---------------------------------------------------------------- buttons
        var ok = DialogButton("OK");
        var cancel = DialogButton("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        _applyBtn = DialogButton("Apply");
        _applyBtn.Enabled = false;
        ok.Click += (_, _) => { if (Apply()) Close(); };
        cancel.Click += (_, _) => Close();
        _applyBtn.Click += (_, _) => Apply();
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange(new Control[] { _applyBtn, cancel, ok });
        root.Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(root);
        LoadValues();
        HookDirty(this);
        ResumeLayout(true);
    }

    // ------------------------------------------------------------------ values

    void LoadValues()
    {
        _blacklist.Checked = _cfg.Mode == ListMode.Blacklist;
        _whitelist.Checked = _cfg.Mode == ListMode.Whitelist;
        _apps.Items.Clear();
        foreach (var a in _cfg.Apps) AddItem(a);
        _watchTaskbar.Checked = _cfg.WatchTaskbar;
        _watchDesktop.Checked = _cfg.WatchDesktop;
        _moveOpen.Checked = _cfg.MoveOpenWindows;
        _startup.Checked = Startup.IsEnabled;
        _armTimeout.Value = Math.Clamp(_cfg.ArmTimeoutMs, (int)_armTimeout.Minimum, (int)_armTimeout.Maximum);
        _grace.Value = Math.Clamp(_cfg.SameAppGraceMs, (int)_grace.Minimum, (int)_grace.Maximum);
        _cancel.Value = Math.Clamp(_cfg.ActivationCancelMs, (int)_cancel.Minimum, (int)_cancel.Maximum);
        _shrink.Value = (decimal)Math.Clamp(_cfg.VerticalShrinkShare, 0, 1);
        _debugLog.Checked = _cfg.DebugLog;
    }

    Config Collect()
    {
        var c = _cfg.Clone();
        c.Mode = _whitelist.Checked ? ListMode.Whitelist : ListMode.Blacklist;
        c.Apps = _apps.Items.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();
        c.WatchTaskbar = _watchTaskbar.Checked;
        c.WatchDesktop = _watchDesktop.Checked;
        c.MoveOpenWindows = _moveOpen.Checked;
        c.ArmTimeoutMs = (int)_armTimeout.Value;
        c.SameAppGraceMs = (int)_grace.Value;
        c.ActivationCancelMs = (int)_cancel.Value;
        c.VerticalShrinkShare = (double)_shrink.Value;
        c.DebugLog = _debugLog.Checked;
        return c;
    }

    bool Apply()
    {
        if (_whitelist.Checked && _apps.Items.Count == 0 &&
            MessageBox.Show(this, "\"Move only the apps listed\" is on, but the list is empty, so no app will be moved.\n\nSave anyway?",
                "LaunchHere", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return false;

        if (_startup.Checked != Startup.IsEnabled)
        {
            try
            {
                Startup.Set(_startup.Checked);
                _log.Info(_startup.Checked ? "start with Windows: on" : "start with Windows: off");
            }
            catch (Exception ex)
            {
                _log.Error($"could not change start with Windows: {ex.Message}");
                MessageBox.Show(this, $"Could not change the startup setting:\n{ex.Message}", "LaunchHere",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _startup.Checked = Startup.IsEnabled;
            }
        }
        _onApply(Collect());
        _applyBtn.Enabled = false;
        return true;
    }

    void HookDirty(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            switch (c)
            {
                case CheckBox cb: cb.CheckedChanged += (_, _) => Dirty(); break;
                case RadioButton rb: rb.CheckedChanged += (_, _) => Dirty(); break;
                case NumericUpDown n: n.ValueChanged += (_, _) => Dirty(); break;
            }
            HookDirty(c);
        }
    }

    void Dirty() => _applyBtn.Enabled = true;

    // ------------------------------------------------------------------ app list

    void AddItem(string entry)
    {
        bool isPath = entry.Contains('\\') || entry.Contains('/');
        var item = new ListViewItem(isPath ? Path.GetFileName(entry) : entry)
        {
            Tag = entry,
            ImageKey = IconKeyFor(entry),
            ToolTipText = entry,
        };
        item.SubItems.Add(isPath ? entry : "any copy");
        _apps.Items.Add(item);
    }

    bool Add(string entry)
    {
        entry = entry.Trim().Trim('"');
        if (entry.Length == 0) return false;
        if (!entry.Contains('\\') && !entry.Contains('/') && !entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            entry += ".exe";
        if (_apps.Items.Cast<ListViewItem>().Any(i => string.Equals((string)i.Tag!, entry, StringComparison.OrdinalIgnoreCase)))
            return false;
        AddItem(entry);
        Dirty();
        return true;
    }

    void AddTyped()
    {
        if (Add(_addBox.Text)) _addBox.Clear();
        else if (_addBox.Text.Trim().Length > 0) System.Media.SystemSounds.Beep.Play();
    }

    void Browse()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Choose an app",
            Filter = "Apps (*.exe)|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string name = Path.GetFileName(dlg.FileName);
        var answer = MessageBox.Show(this,
            $"Match every copy of {name} (Yes), or only this one (No)?\n\n{dlg.FileName}",
            "LaunchHere", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) return;
        string entry = answer == DialogResult.Yes ? name : dlg.FileName;
        _iconKeys.TryAdd(entry, AddIcon(dlg.FileName));
        Add(entry);
    }

    void AddRunning()
    {
        var existing = _apps.Items.Cast<ListViewItem>().Select(i => (string)i.Tag!);
        using var dlg = new RunningAppsDialog(existing, Icon);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        foreach (var (name, path) in dlg.Selected)
        {
            if (path != "") _iconKeys.TryAdd(name, AddIcon(path));
            Add(name);
        }
    }

    void RemoveSelected()
    {
        foreach (ListViewItem i in _apps.SelectedItems.Cast<ListViewItem>().ToList()) _apps.Items.Remove(i);
        Dirty();
    }

    string IconKeyFor(string entry)
    {
        if (_iconKeys.TryGetValue(entry, out var k)) return k;
        string path = entry.Contains('\\') || entry.Contains('/') ? entry : RunningApps.FindPath(entry);
        k = path != "" ? AddIcon(path) : "";
        _iconKeys[entry] = k;
        return k;
    }

    string AddIcon(string path)
    {
        if (_icons.Images.ContainsKey(path)) return path;
        try
        {
            using var ico = Icon.ExtractAssociatedIcon(path);
            if (ico == null) return "";
            // Copy to a bitmap: the ImageList reads the image later, after this icon is disposed.
            _icons.Images.Add(path, ico.ToBitmap());
            return path;
        }
        catch { return ""; }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A bold heading, then the section's controls indented under it.</summary>
    static void Section(TableLayoutPanel root, string title, Control content, bool first = false)
    {
        var head = new Label { Text = title, AutoSize = true, Margin = new Padding(0, first ? 0 : 14, 0, 2) };
        head.Font = new Font(SystemFonts.MessageBoxFont ?? head.Font, FontStyle.Bold);
        root.Controls.Add(head);
        content.Margin = new Padding(10, 0, 0, 0);
        root.Controls.Add(content);
    }

    static Button SideButton(string text) => new() { Text = text, Size = new Size(150, 30), Margin = new Padding(3, 0, 0, 6) };

    internal static Button DialogButton(string text) => new() { Text = text, Size = new Size(88, 30), Margin = new Padding(6, 0, 0, 0) };

    internal static Size IconSize(int dpi) => new(16 * dpi / 96, 16 * dpi / 96);

    /// <summary>List view columns aren't scaled by AutoScale: scale the widths (given at 96 dpi)
    /// when the window opens, and again when it moves to a monitor with another DPI.</summary>
    internal static void ScaleColumns(Form form, ListView lv)
    {
        int[] logical = lv.Columns.Cast<ColumnHeader>().Select(c => c.Width).ToArray();
        void Apply()
        {
            for (int i = 0; i < logical.Length; i++) lv.Columns[i].Width = logical[i] * form.DeviceDpi / 96;
        }
        form.Load += (_, _) => Apply();
        form.DpiChanged += (_, _) => Apply();
    }

    static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 3, 3, 3) };

    static Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 4, 3, 0) };

    static NumericUpDown Number(decimal min, decimal max, int decimals, decimal step) => new()
    {
        Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = step,
        Width = 80, TextAlign = HorizontalAlignment.Right, Anchor = AnchorStyles.Left, ThousandsSeparator = decimals == 0,
    };
}

/// <summary>Picker listing the apps that have a window open right now.</summary>
internal sealed class RunningAppsDialog : Form
{
    readonly ListView _list;
    public List<(string Name, string Path)> Selected { get; } = new();

    public RunningAppsDialog(IEnumerable<string> existing, Icon? icon)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Text = "Add running apps";
        if (icon != null) Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        var icons = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = SettingsForm.IconSize(DeviceDpi) };
        _list = new ListView
        {
            View = View.Details, CheckBoxes = true, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Size = new Size(520, 300), SmallImageList = icons,
        };
        _list.Columns.Add("App", 170);
        _list.Columns.Add("Window", 330);
        SettingsForm.ScaleColumns(this, _list);
        _list.ItemActivate += (_, _) => { foreach (ListViewItem i in _list.SelectedItems) i.Checked = !i.Checked; };

        var have = new HashSet<string>(existing.Select(e => System.IO.Path.GetFileName(e)), StringComparer.OrdinalIgnoreCase);
        foreach (var app in RunningApps.List().Where(a => !have.Contains(a.Name)))
        {
            string key = "";
            if (app.Path != "")
            {
                try
                {
                    using var ico = Icon.ExtractAssociatedIcon(app.Path);
                    if (ico != null) { icons.Images.Add(app.Path, ico.ToBitmap()); key = app.Path; }
                }
                catch { }
            }
            var item = new ListViewItem(app.Name) { ImageKey = key, Tag = app };
            item.SubItems.Add(app.Title);
            _list.Items.Add(item);
        }

        var note = new Label
        {
            Text = _list.Items.Count == 0 ? "No other apps with open windows found." : "Tick the apps to add, then click Add.",
            AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 6),
        };
        var add = SettingsForm.DialogButton("Add");
        var cancel = SettingsForm.DialogButton("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        add.Click += (_, _) =>
        {
            foreach (ListViewItem i in _list.CheckedItems)
            {
                var a = ((string Name, string Path, string Title))i.Tag!;
                Selected.Add((a.Name, a.Path));
            }
            DialogResult = DialogResult.OK;
        };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange(new Control[] { cancel, add });
        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1 };
        root.Controls.Add(note);
        root.Controls.Add(_list);
        root.Controls.Add(buttons);
        Controls.Add(root);
        AcceptButton = add;
        CancelButton = cancel;
        ResumeLayout(true);
    }
}

/// <summary>Apps that have a normal top-level window open.</summary>
internal static class RunningApps
{
    public static List<(string Name, string Path, string Title)> List()
    {
        var found = new Dictionary<string, (string Name, string Path, List<string> Titles)>(StringComparer.OrdinalIgnoreCase);
        int own = Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            if ((ExStyle(h) & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0 || IsCloaked(h)) return true;
            string title = TitleOf(h);
            if (title.Length == 0) return true;
            string cls = ClassOf(h);
            if (Tracker.ShellClasses.Contains(cls)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if ((int)pid == own) return true;
            string path = ProcessPath(pid);
            string name = path != "" ? System.IO.Path.GetFileName(path) : NameOf(pid);
            if (name == "" || Tracker.ShellProcesses.Contains(name)) return true;
            if (name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) && cls != "CabinetWClass") return true;
            if (!found.TryGetValue(name, out var e)) found[name] = e = (name, path, new List<string>());
            e.Titles.Add(title);
            return true;
        }, IntPtr.Zero);

        return found.Values
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => (a.Name, a.Path, Describe(a.Name, a.Path, a.Titles)))
            .ToList();
    }

    static string Describe(string name, string path, List<string> titles)
    {
        string t = titles[0] + (titles.Count > 1 ? $" (+{titles.Count - 1} more)" : "");
        if (name.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            return $"Store apps: {string.Join(", ", titles)} - they all share this entry";
        if (path == "") t += " (running as admin)";
        return t;
    }

    static string NameOf(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName + ".exe"; }
        catch { return ""; }
    }

    /// <summary>Path of a running copy of this exe name, for its icon ("" if none is running).</summary>
    public static string FindPath(string exeName)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(exeName)))
            {
                using (p)
                {
                    string path = ProcessPath((uint)p.Id);
                    if (path != "") return path;
                }
            }
        }
        catch { }
        return "";
    }
}
