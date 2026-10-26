namespace Poe2Verlay;

internal sealed class SettingsForm : Form
{
    readonly ComboBox _league = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    readonly HotkeyBox _scanKey = new(), _itemKey = new(), _autoKey = new(), _runStartKey = new(), _runEndKey = new(), _windowKey = new(), _stashKey = new(), _sessionKey = new(), _shotKey = new();
    readonly CheckBox _onlyInGame = Check("Hotkeys only work while Path of Exile 2 is focused");
    readonly CheckBox _auto = Check("Auto-scan the screen while the game is focused (uses more CPU)");
    readonly NumericUpDown _interval = Num(300, 20000, 0, 100);
    readonly NumericUpDown _seconds = Num(0, 600, 0, 1);
    readonly NumericUpDown _idle = Num(10, 3600, 0, 10);
    readonly NumericUpDown _roll = Num(0, 1000, 1, 0.5m);
    readonly NumericUpDown _low = Num(0, 100000, 2, 0.5m);
    readonly NumericUpDown _veryLow = Num(0, 100000, 2, 0.1m);
    readonly NumericUpDown _listings = Num(0, 1000, 0, 1);
    readonly NumericUpDown _scale = Num(0, 4, 1, 0.1m);
    readonly CheckBox _rumours = Check("Expedition: rate island rumours when hovering uncharted waters (hideout)");
    readonly TextBox _sheet = new() { Width = 320 };
    readonly CheckBox _exalted = Check("Also show price in exalted orbs");
    readonly CheckBox _uniques = Check("Load unique item prices too");
    readonly CheckBox _debug = Check("Save debug captures of each scan (data folder\\debug)");

    readonly AppSettings _source;
    public AppSettings Result { get; private set; }

    /// <summary>Show without taking focus (used by the off-screen preview in --smoke).</summary>
    public bool NoActivate { get; set; }
    protected override bool ShowWithoutActivation => NoActivate;

    public SettingsForm(AppSettings settings, IReadOnlyList<string> leagues)
    {
        _source = settings;
        Result = settings.Clone();

        Text = "poe2verlay – settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        TopMost = true;
        ShowInTaskbar = true;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { /* default icon */ }

        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(12), Dock = DockStyle.Fill };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void Row(string text, Control control)
        {
            table.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 14, 3) });
            table.Controls.Add(control);
        }

        void Full(Control control)
        {
            table.Controls.Add(control);
            table.SetColumnSpan(control, 2);
        }

        var names = leagues.Count > 0 ? leagues.ToList() : new List<string>();
        if (!string.IsNullOrEmpty(settings.League) && !names.Contains(settings.League)) names.Insert(0, settings.League);
        _league.Items.AddRange(names.Cast<object>().ToArray());
        _league.SelectedItem = string.IsNullOrEmpty(settings.League) ? names.FirstOrDefault() : settings.League;

        _scanKey.Value = Hotkey.Parse(settings.ScanHotkey);
        _itemKey.Value = Hotkey.Parse(settings.ItemHotkey);
        _autoKey.Value = Hotkey.Parse(settings.AutoScanHotkey);
        _runStartKey.Value = Hotkey.Parse(settings.RunStartHotkey);
        _runEndKey.Value = Hotkey.Parse(settings.RunEndHotkey);
        _windowKey.Value = Hotkey.Parse(settings.InventoryWindowHotkey);
        _stashKey.Value = Hotkey.Parse(settings.StashHotkey);
        _sessionKey.Value = Hotkey.Parse(settings.SessionHotkey);
        _shotKey.Value = Hotkey.Parse(settings.ScreenshotHotkey);
        _idle.Value = Math.Clamp(settings.SessionIdleSeconds, 10, 3600);
        _roll.Value = (decimal)Math.Clamp(settings.RollBelowChaos, 0, 1000);
        _onlyInGame.Checked = settings.HotkeysOnlyInGame;
        _auto.Checked = settings.AutoScan;
        _interval.Value = Math.Clamp(settings.AutoScanIntervalMs, 300, 20000);
        _seconds.Value = Math.Clamp(settings.OverlaySeconds, 0, 600);
        _low.Value = (decimal)Math.Clamp(settings.LowTradeVolume, 0, 100000);
        _veryLow.Value = (decimal)Math.Clamp(settings.VeryLowTradeVolume, 0, 100000);
        _listings.Value = Math.Clamp(settings.LowListings, 0, 1000);
        _scale.Value = (decimal)Math.Clamp(settings.OcrScale, 0, 4);
        _rumours.Checked = settings.ExpeditionRumours;
        _sheet.Text = settings.RumourSheet;
        _exalted.Checked = settings.ShowExalted;
        _uniques.Checked = settings.IncludeUniques;
        _debug.Checked = settings.SaveDebug;

        Row("League (poe.ninja)", _league);
        Row("Scan choices on screen", _scanKey);
        Row("Price item under cursor", _itemKey);
        Row("Toggle auto-scan", _autoKey);
        Row("Run start (scan inventory)", _runStartKey);
        Row("Run finish + difference", _runEndKey);
        Row("Show/hide run report", _windowKey);
        Row("Scan open stash tab", _stashKey);
        Row("Start/stop farming session", _sessionKey);
        Row("Session pauses after idle (s)", _idle);
        Row("Screenshot with overlay", _shotKey);
        Full(_onlyInGame);
        Full(_auto);
        Row("Auto-scan every (ms)", _interval);
        Row("Hide labels after (s, 0 = never)", _seconds);
        Row("Show ROLL when best is under (c)", _roll);
        Row("\"Low trade\" below volume (div)", _low);
        Row("\"Very low trade\" below (div)", _veryLow);
        Row("Uniques: \"few listings\" below", _listings);
        Row("OCR upscale (0 = auto)", _scale);
        Full(_rumours);
        Row("Rumour ratings (sheet link or .csv)", _sheet);
        Full(_exalted);
        Full(_uniques);
        Full(_debug);
        Full(new Label
        {
            Text = "Click a hotkey box and press the key combination. Backspace clears it.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 8, 3, 3),
        });

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        Full(buttons);
        AcceptButton = save;
        CancelButton = cancel;
        save.Click += (_, _) => Collect();

        Controls.Add(table);
    }

    void Collect()
    {
        var r = _source.Clone();
        r.League = _league.SelectedItem as string ?? r.League;
        r.ScanHotkey = Hotkey.Format(_scanKey.Value);
        r.ItemHotkey = Hotkey.Format(_itemKey.Value);
        r.AutoScanHotkey = Hotkey.Format(_autoKey.Value);
        r.RunStartHotkey = Hotkey.Format(_runStartKey.Value);
        r.RunEndHotkey = Hotkey.Format(_runEndKey.Value);
        r.InventoryWindowHotkey = Hotkey.Format(_windowKey.Value);
        r.StashHotkey = Hotkey.Format(_stashKey.Value);
        r.SessionHotkey = Hotkey.Format(_sessionKey.Value);
        r.ScreenshotHotkey = Hotkey.Format(_shotKey.Value);
        r.SessionIdleSeconds = (int)_idle.Value;
        r.RollBelowChaos = (double)_roll.Value;
        r.HotkeysOnlyInGame = _onlyInGame.Checked;
        r.AutoScan = _auto.Checked;
        r.AutoScanIntervalMs = (int)_interval.Value;
        r.OverlaySeconds = (int)_seconds.Value;
        r.LowTradeVolume = (double)_low.Value;
        r.VeryLowTradeVolume = (double)_veryLow.Value;
        r.LowListings = (int)_listings.Value;
        r.OcrScale = (double)_scale.Value;
        r.ExpeditionRumours = _rumours.Checked;
        r.RumourSheet = _sheet.Text.Trim();
        r.ShowExalted = _exalted.Checked;
        r.IncludeUniques = _uniques.Checked;
        r.SaveDebug = _debug.Checked;
        Result = r;
    }

    static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 6, 3, 3) };

    static NumericUpDown Num(decimal min, decimal max, int decimals, decimal step) => new()
    {
        Minimum = min,
        Maximum = max,
        DecimalPlaces = decimals,
        Increment = step,
        Width = 100,
    };
}

/// <summary>Read-only text box that records the next key combination pressed in it.</summary>
internal sealed class HotkeyBox : TextBox
{
    Keys _value;

    public HotkeyBox()
    {
        ReadOnly = true;
        Width = 160;
        BackColor = SystemColors.Window;
        ShortcutsEnabled = false;
    }

    public Keys Value
    {
        get => _value;
        set
        {
            _value = value;
            Text = Hotkey.Format(value);
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var code = keyData & Keys.KeyCode;
        if (code is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.Tab or Keys.Enter or Keys.Escape)
            return base.ProcessCmdKey(ref msg, keyData);
        Value = code is Keys.Back or Keys.Delete ? Keys.None : keyData;
        return true;
    }
}
