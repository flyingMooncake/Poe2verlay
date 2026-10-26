using System.Globalization;

namespace Poe2Verlay;

/// <summary>
/// Run report (Ctrl+O / Ctrl+0): the Ctrl+I snapshot, the Ctrl+O snapshot, the difference and the saved run history,
/// as tabs. An overlay window: no frame, no taskbar / alt-tab entry, never takes focus from the game, left out of the
/// screen captures we OCR. Small = just the sums and the farming session; big = the tabs with every item.
/// </summary>
internal sealed class InventoryForm : Form
{
    enum Page { Start, Now, Diff, History, Special }

    static readonly Color Dim = DarkUi.Dim;
    static readonly Color Gold = DarkUi.Gold;
    static readonly Color Up = DarkUi.Up;
    static readonly Color Down = DarkUi.Down;

    readonly Button _tabStart, _tabNow, _tabDiff, _tabHistory, _tabSpecial, _save, _delete, _size, _close;
    readonly FlowLayoutPanel _tabs;
    readonly Panel _bar;
    readonly Label _summary;
    readonly ListView _list;
    readonly RichTextBox _details;
    readonly AppSettings _settings;
    readonly System.Windows.Forms.Timer _refresh = new() { Interval = 2000 };

    Page _page;
    SnapshotValue? _start, _now;
    PriceDb? _db;
    SavedRun? _run;          // what the Difference tab shows
    bool _runIsLive, _runSaved, _compact;
    TimeSpan? _runPlayed;    // live run: time actually played (farming session)
    List<SavedRun> _history = new();
    string _summaryText = "";
    Size _bigSize;

    /// <summary>Raised when the window is moved or resized: location + the big size, to be remembered.</summary>
    public event Action<Rectangle>? BoundsSaved;
    /// <summary>Raised when switched between small and big.</summary>
    public event Action<bool>? CompactChanged;
    /// <summary>The farming session line under the sums; refreshed every few seconds while shown.</summary>
    public Func<string?>? SessionLine;
    /// <summary>Current poe.ninja price of an item by name ("Perfect Flux ≈ 34.7 div"), for the special maps tab.</summary>
    public Func<string, string?>? PriceText;

    public InventoryForm(AppSettings settings)
    {
        _settings = settings;
        DarkUi.StyleWindow(this, "poe2verlay \u2013 run report");
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        Opacity = 0.95;
        BackColor = DarkUi.Header; // the padding shows as a thin frame, and is where it can be resized
        Padding = new Padding(5);
        MinimumSize = new Size(320, 60);

        _tabStart = TabButton("Inventory  (Ctrl+I)", Page.Start);
        _tabNow = TabButton("Now  (Ctrl+O)", Page.Now);
        _tabDiff = TabButton("Difference", Page.Diff);
        _tabHistory = TabButton("History", Page.History);
        _tabSpecial = TabButton("Special maps", Page.Special);
        _save = ActionButton("Save run", SaveRun);
        _delete = ActionButton("Delete", DeleteSelected);
        _size = ActionButton("", () => SetCompact(!_compact));
        _close = ActionButton("\u2715", Hide);

        _tabs = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = DarkUi.Back, WrapContents = false };
        _tabs.Controls.AddRange(new Control[] { _tabStart, _tabNow, _tabDiff, _tabHistory, _tabSpecial });
        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, BackColor = DarkUi.Back, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.AddRange(new Control[] { _close, _size, _save, _delete });
        _bar = new Panel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(40), BackColor = DarkUi.Back, Padding = new Padding(6, 6, 6, 0) };
        _bar.Controls.Add(_tabs);
        _bar.Controls.Add(actions);

        _summary = DarkUi.Summary();
        _summary.SizeChanged += (_, _) => FitCompactHeight();
        _list = DarkUi.List();
        _list.ItemActivate += (_, _) => OpenSelectedRun();
        _list.SelectedIndexChanged += (_, _) =>
        {
            UpdateActions();
            if (_page == Page.Special && _list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is Rumour r) ShowIsland(r);
        };
        _details = new RichTextBox
        {
            Dock = DockStyle.Bottom,
            Height = LogicalToDeviceUnits(300),
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = DarkUi.Panel,
            ForeColor = DarkUi.Text,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false,
            Visible = false,
        };
        _details.HandleCreated += (_, _) => Native.SetWindowTheme(_details.Handle, "DarkMode_Explorer", null);

        Controls.Add(_list);
        Controls.Add(_details);
        Controls.Add(_summary);
        Controls.Add(_bar);

        // No title bar: drag it by the button bar or the sums.
        foreach (var c in new Control[] { _bar, _tabs, actions, _summary }) c.MouseDown += DragWindow;
        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true; // keep the data, just hide
            Hide();
        };
        ResizeEnd += (_, _) => SaveBounds();
        Resize += (_, _) => FitSpecialSplit();
        _refresh.Tick += (_, _) => UpdateSummary();
        VisibleChanged += (_, _) => _refresh.Enabled = Visible;

        DarkUi.PlaceWindow(this, _settings.InventoryWindowBounds, 24, 110, 760, 620);
        _bigSize = Size;
        _compact = settings.RunWindowCompact;
        ShowPage(Page.Start);
        ApplyMode();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Like the price labels: never in the screenshots that get OCR'd.
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>The frame (form padding) resizes the big window.</summary>
    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84, HTCLIENT = 1;
        base.WndProc(ref m);
        if (m.Msg != WM_NCHITTEST || _compact || (int)m.Result != HTCLIENT) return;
        var lp = m.LParam.ToInt64();
        var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
        var grip = Padding.Left + 2;
        bool l = p.X < grip, r = p.X >= ClientSize.Width - grip, t = p.Y < grip, b = p.Y >= ClientSize.Height - grip;
        m.Result = (IntPtr)(t && l ? 13 : t && r ? 14 : b && l ? 16 : b && r ? 17 : l ? 10 : r ? 11 : t ? 12 : b ? 15 : HTCLIENT);
    }

    void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        Native.ReleaseCapture();
        Native.SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
    }

    void SaveBounds()
    {
        if (!_compact) _bigSize = Size;
        BoundsSaved?.Invoke(new Rectangle(Location, _bigSize));
    }

    public bool Compact => _compact;

    public void SetCompact(bool compact)
    {
        if (compact == _compact) return;
        if (!_compact) _bigSize = Size;
        _compact = compact;
        if (compact) ShowDefaultPage();
        ApplyMode();
        CompactChanged?.Invoke(compact);
    }

    void ApplyMode()
    {
        _tabs.Visible = !_compact;
        _list.Visible = !_compact;
        _details.Visible = _page == Page.Special && !_compact;
        _size.Text = _compact ? "\u25be details" : "\u25b4 small";
        UpdateActions();
        if (_compact) FitCompactHeight();
        else Size = _bigSize;
    }

    /// <summary>Small: as tall as the sums need.</summary>
    void FitCompactHeight()
    {
        if (!_compact) return;
        var size = new Size(Math.Min(_bigSize.Width, LogicalToDeviceUnits(560)), _bar.Height + _summary.Height + Padding.Vertical);
        if (Size != size) Size = size;
    }

    public bool HasData => _start != null;

    public void SetStart(SnapshotValue start, PriceDb db)
    {
        _start = start;
        _now = null;
        _db = db;
        if (_runIsLive) _run = null;
        ShowPage(Page.Start);
    }

    /// <param name="played">Time actually played during the run, when a farming session was running.</param>
    public void SetReport(RunReport report, PriceDb db, TimeSpan? played = null)
    {
        _start = report.Start;
        _now = report.End;
        _db = db;
        _run = SavedRun.From(report, db);
        _runPlayed = played;
        _runIsLive = true;
        _runSaved = false;
        ShowPage(Page.Diff);
    }

    public void ShowHistory() => ShowPage(Page.History);

    /// <summary>Big, on the special maps tab.</summary>
    public void ShowSpecialMaps()
    {
        SetCompact(false);
        ShowPage(Page.Special);
    }

    /// <summary>Ctrl+0: the difference when there is one, otherwise the Ctrl+I inventory.</summary>
    public void ShowDefaultPage() => ShowPage(_run != null ? Page.Diff : Page.Start);

    Button TabButton(string text, Page page)
    {
        var b = DarkUi.Button(text);
        b.Click += (_, _) => ShowPage(page);
        return b;
    }

    Button ActionButton(string text, Action onClick)
    {
        var b = DarkUi.Button(text);
        b.Margin = new Padding(4, 0, 0, 0);
        b.Click += (_, _) => onClick();
        return b;
    }

    void ShowPage(Page page)
    {
        _page = page;
        foreach (var (button, p) in new[] { (_tabStart, Page.Start), (_tabNow, Page.Now), (_tabDiff, Page.Diff), (_tabHistory, Page.History), (_tabSpecial, Page.Special) })
            DarkUi.SetActive(button, p == page);
        _details.Visible = page == Page.Special && !_compact;
        _tabNow.Enabled = _now != null;
        _tabDiff.Enabled = _run != null;

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            _list.Columns.Clear();
            switch (page)
            {
                case Page.Start: FillSnapshot(_start, "Ctrl+I"); break;
                case Page.Now: FillSnapshot(_now, "Ctrl+O"); break;
                case Page.Diff: FillDiff(); break;
                case Page.History: FillHistory(); break;
                case Page.Special: FillSpecial(); break;
            }
        }
        finally
        {
            _list.EndUpdate();
        }
        DarkUi.FillLastColumn(_list);
        UpdateSummary();
        UpdateActions();
    }

    /// <summary>The page's sums plus the farming session line (live).</summary>
    void UpdateSummary()
    {
        var session = _page is Page.Start or Page.Now or Page.Diff ? SessionLine?.Invoke() : null;
        var text = session == null ? _summaryText : _summaryText + "\n" + session;
        if (_summary.Text != text) _summary.Text = text;
    }

    void UpdateActions()
    {
        _save.Visible = _page == Page.Diff && _run != null && _runIsLive;
        _save.Enabled = !_runSaved;
        _save.Text = _runSaved ? "Saved ✓" : "Save run";
        _save.ForeColor = _runSaved ? Up : Gold;
        _delete.Visible = _page == Page.History && !_compact;
        _delete.Enabled = _list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is SavedRun;
    }

    // ---------------------------------------------------------------- pages

    void FillSnapshot(SnapshotValue? value, string key)
    {
        AddColumns(("Item", 230, false), ("Qty", 50, true), ("Each div", 75, true), ("Each c", 65, true),
            ("Total div", 80, true), ("Total c", 70, true), ("Trade", 100, false));

        if (value == null || _db == null)
        {
            _summaryText = $"Nothing scanned yet.\nOpen the inventory in game and press {key}.";
            return;
        }

        _summaryText =
            $"Total:  {Fmt.Range(value.Min, value.Max)} div     ({Fmt.Range(value.Min * _db.ChaosPerDivine, value.Max * _db.ChaosPerDivine)} c)\n" +
            $"min = only items with real trade volume (≥ {Fmt.Number(_settings.LowTradeVolume)} div)  ·  max = everything with a price\n" +
            $"{value.Items.Count} items, {value.Unpriced} without a price  ·  scanned {value.Snapshot.Time:HH:mm:ss}" +
            (value.Snapshot.UnreadCells > 0 ? $"  ·  ⚠ {value.Snapshot.UnreadCells} slot(s) unread" : "");

        foreach (var i in value.Items)
        {
            var priced = i.Entry != null;
            var row = DarkUi.Row(DarkUi.ItemName(i.Name, i.Detail, priced), i.Qty.ToString(CultureInfo.InvariantCulture),
                priced ? Fmt.Number(i.Unit) : "–",
                priced ? Fmt.Number(i.Unit * _db.ChaosPerDivine) : "–",
                priced ? Fmt.Number(i.Total) : "–",
                priced ? Fmt.Number(i.Total * _db.ChaosPerDivine) : "–",
                Valuation.Text(i.Trade));
            DarkUi.Tint(row, i.Trade);
            if (priced && i.Total >= 1 && i.Trade == Trade.Ok)
            {
                row.SubItems[4].ForeColor = Gold;
                row.SubItems[5].ForeColor = Gold;
            }
            _list.Items.Add(row);
        }
    }

    void FillDiff()
    {
        AddColumns(("Item", 220, false), ("Ctrl+I", 50, true), ("Now", 50, true), ("Δ", 50, true),
            ("Each div", 70, true), ("Δ div", 75, true), ("Δ c", 65, true), ("Trade", 100, false));

        if (_run == null)
        {
            _summaryText = "No difference yet.\nPress Ctrl+I at the start of a run and Ctrl+O at the end.";
            return;
        }

        var r = _run;
        var c = r.ChaosPerDivine;
        // With a farming session running, div/h is on the time actually played.
        var played = _runIsLive && _runPlayed is { TotalSeconds: > 30 } t ? t : (TimeSpan?)null;
        var hours = (played ?? r.Duration).TotalHours;
        var perHour = hours > 0.01
            ? $"  ·  ≈ {Fmt.Range(r.ProfitMin / hours, r.ProfitMax / hours, signed: true)} div/h"
            : "";
        var time = played is { } p ? $"{Fmt.Duration(p)} played ({Fmt.Duration(r.Duration)} total)" : Fmt.Duration(r.Duration);
        var title = _runIsLive ? $"Run #{r.Number}" : $"Saved run {r.Start:yyyy-MM-dd HH:mm}";
        _summaryText =
            $"{title}:  {Fmt.Range(r.ProfitMin, r.ProfitMax, signed: true)} div     ({Fmt.Range(r.ProfitMin * c, r.ProfitMax * c, signed: true)} c)\n" +
            $"{time}{perHour}  ·  Ctrl+I {Fmt.Range(r.StartMin, r.StartMax)} → now {Fmt.Range(r.EndMin, r.EndMax)} div\n" +
            $"{r.League}  ·  {r.Start:HH:mm} – {r.End:HH:mm}" +
            (r.UnreadSlots > 0 ? $"  ·  ⚠ {r.UnreadSlots} slot(s) unread" : "");

        foreach (var l in r.Lines)
        {
            var priced = l.Unit != null;
            var row = DarkUi.Row(DarkUi.ItemName(l.Name, l.Detail, priced), l.Before.ToString(CultureInfo.InvariantCulture),
                l.After.ToString(CultureInfo.InvariantCulture), Signed(l.Delta),
                priced ? Fmt.Number(l.Unit!.Value) : "–",
                priced ? Fmt.SignedNumber(l.Value) : "–",
                priced ? Fmt.SignedNumber(l.Value * c) : "–",
                Valuation.Text(l.Trade));
            DarkUi.Tint(row, l.Trade);
            var deltaColor = l.Delta > 0 ? Up : Down;
            row.SubItems[3].ForeColor = deltaColor;
            if (priced)
            {
                row.SubItems[5].ForeColor = deltaColor;
                row.SubItems[6].ForeColor = deltaColor;
            }
            _list.Items.Add(row);
        }
        if (r.Lines.Count == 0)
            _list.Items.Add(new ListViewItem("nothing changed") { ForeColor = Dim });
    }

    void FillHistory()
    {
        AddColumns(("Date", 125, false), ("League", 120, false), ("Time", 70, true), ("Profit div", 120, true),
            ("Profit c", 110, true), ("div/h", 75, true), ("Top items", 200, false));

        _history = RunStore.LoadAll();
        if (_history.Count == 0)
        {
            _summaryText = "No saved runs yet.\nAfter Ctrl+O, press \"Save run\" in the Difference tab.";
            return;
        }

        var totalMin = _history.Sum(r => r.ProfitMin);
        var totalMax = _history.Sum(r => r.ProfitMax);
        var hours = _history.Sum(r => r.Duration.TotalHours);
        _summaryText =
            $"{_history.Count} saved run(s):  {Fmt.Range(totalMin, totalMax, signed: true)} div total\n" +
            $"avg {Fmt.Range(totalMin / _history.Count, totalMax / _history.Count, signed: true)} div per run" +
            (hours > 0.01 ? $"  ·  ≈ {Fmt.Range(totalMin / hours, totalMax / hours, signed: true)} div/h" : "") + "\n" +
            "double-click a run to see what changed  ·  Delete removes it";

        foreach (var r in _history)
        {
            var hoursRun = r.Duration.TotalHours;
            var top = string.Join(", ", r.Lines.Where(l => l.Unit != null && l.Value > 0).Take(3).Select(l => $"{Signed(l.Delta)} {l.Name}"));
            var row = DarkUi.Row(r.Start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), r.League, Fmt.Duration(r.Duration),
                Fmt.Range(r.ProfitMin, r.ProfitMax, signed: true),
                Fmt.Range(r.ProfitMin * r.ChaosPerDivine, r.ProfitMax * r.ChaosPerDivine, signed: true),
                hoursRun > 0.01 ? Fmt.SignedNumber(r.ProfitMax / hoursRun) : "–",
                top);
            DarkUi.Tint(row, Trade.Ok);
            var color = r.ProfitMax >= 0 ? Up : Down;
            row.SubItems[3].ForeColor = color;
            row.SubItems[4].ForeColor = color;
            row.Tag = r;
            _list.Items.Add(row);
        }
    }

    /// <summary>Expedition islands worth knowing (green "do this" ones first); the selected one in detail below.</summary>
    void FillSpecial()
    {
        AddColumns(("Rating", 60, false), ("Rumour", 135, false), ("Island", 130, false), ("Type", 80, false), ("Worth now", 200, false));
        _summaryText = "Expedition islands with something special (green = has a DO THIS box on the overlay). " +
                       "Pick one: what it is, why it matters, what to do.\nratings: " + RumourBook.Source;
        foreach (var r in RumourBook.All.Where(r => r.Why != null).OrderByDescending(r => r.Special).ThenByDescending(r => r.Points))
        {
            var worth = r.Prices.Select(n => PriceText?.Invoke(n)).OfType<string>().FirstOrDefault() ?? r.Mods;
            var row = DarkUi.Row(r.Rating, r.ShortText, r.Map, r.KindText, worth);
            DarkUi.Tint(row, Trade.Ok);
            if (r.Special)
                foreach (ListViewItem.ListViewSubItem sub in row.SubItems) sub.ForeColor = Gold;
            row.Tag = r;
            _list.Items.Add(row);
        }
        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        BeginInvoke(FitSpecialSplit);
    }

    /// <summary>The island list just tall enough for every row; the details take the rest.</summary>
    void FitSpecialSplit()
    {
        if (_page != Page.Special || _compact || _list.Items.Count == 0) return;
        var row = _list.GetItemRect(0).Height;
        var listHeight = row * (_list.Items.Count + 1) + SystemInformation.HorizontalScrollBarHeight + LogicalToDeviceUnits(8);
        var available = ClientSize.Height - Padding.Vertical - _bar.Height - _summary.Height;
        _details.Height = Math.Max(LogicalToDeviceUnits(160), available - listHeight);
    }

    readonly Dictionary<(float, FontStyle), Font> _fonts = new();

    void ShowIsland(Rumour r)
    {
        var box = _details;
        box.Clear();
        void Add(string text, Color color, FontStyle style = FontStyle.Regular, float size = 10f)
        {
            if (!_fonts.TryGetValue((size, style), out var font)) _fonts[(size, style)] = font = new Font("Segoe UI", size, style);
            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionFont = font;
            box.SelectionColor = color;
            box.AppendText(text);
        }
        void Section(string title, string? text, Color? color = null)
        {
            if (string.IsNullOrEmpty(text)) return;
            Add("\n" + title + "\n", Gold, FontStyle.Bold, 10.5f);
            Add(text + "\n", color ?? DarkUi.Text);
        }

        Add($"{r.Rating}   {r.ShortText}  \u2192  {r.Map}", r.Special ? Gold : DarkUi.Text, FontStyle.Bold, 13f);
        Add($"     {r.KindText}\n", Dim, FontStyle.Regular, 10f);
        Section("What it is", r.Mods);
        Section("Why it matters", r.Why);
        if (r.Todo.Length > 0)
            Section("What to do", string.Join("\n", r.Todo.Select((step, i) => $"{i + 1}.  {step.Replace("{scan}", _settings.ScanHotkey)}")), Up);
        else
            Section("Tip", r.Tip);
        Section("Prices now (poe.ninja)", string.Join("\n", r.Prices.Select(n => PriceText?.Invoke(n)).OfType<string>()));
        Section("Watch out", r.Note != null && r.Note != r.Why ? r.Note : null, Down);
        if (r.Source != null) Add("\nsources: " + r.Source + "\n", Dim, FontStyle.Italic, 8.5f);
        box.SelectionStart = 0;
        box.ScrollToCaret();
    }

    // ---------------------------------------------------------------- actions

    void SaveRun()
    {
        if (_run == null || !_runIsLive) return;
        try
        {
            RunStore.Save(_run);
            _runSaved = true;
        }
        catch (Exception ex)
        {
            Log.Write("save run: " + ex);
            MessageBox.Show(this, "Could not save the run: " + ex.Message, "poe2verlay");
        }
        UpdateActions();
    }

    void OpenSelectedRun()
    {
        if (_page != Page.History || _list.SelectedItems.Count != 1 || _list.SelectedItems[0].Tag is not SavedRun run) return;
        _run = run;
        _runIsLive = false;
        ShowPage(Page.Diff);
    }

    void DeleteSelected()
    {
        if (_page != Page.History || _list.SelectedItems.Count != 1 || _list.SelectedItems[0].Tag is not SavedRun run) return;
        var answer = MessageBox.Show(this, $"Delete the run from {run.Start:yyyy-MM-dd HH:mm} ({Fmt.Range(run.ProfitMin, run.ProfitMax, signed: true)} div)?",
            "poe2verlay", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;
        RunStore.Delete(run);
        if (_run != null && !_runIsLive && _run.Start == run.Start) _run = null;
        if (_run != null && _runIsLive && _run.Start == run.Start) _runSaved = false;
        ShowPage(Page.History);
    }

    // ---------------------------------------------------------------- helpers

    void AddColumns(params (string Text, int Width, bool Right)[] columns) => DarkUi.Columns(_list, columns);

    static string Signed(int v) => v > 0 ? "+" + v : v < 0 ? "−" + -v : "0";
}
