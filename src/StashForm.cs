using System.Globalization;

namespace Poe2Verlay;

/// <summary>
/// Stash value window: every scanned tab with its value (top), and the items of the selected tab, or of all
/// tabs together, sorted by value (bottom).
/// </summary>
internal sealed class StashForm : Form
{
    readonly Label _summary;
    readonly ListView _tabs, _items;
    readonly Button _rename, _delete;
    readonly AppSettings _settings;

    List<StashTabRecord> _records = new();
    readonly Dictionary<StashTabRecord, SnapshotValue> _values = new();
    PriceDb? _db;

    /// <summary>Raised when the window is moved or resized, so the position can be remembered.</summary>
    public event Action<Rectangle>? BoundsSaved;

    /// <summary>Raised after a tab was renamed or deleted, so the list can be saved.</summary>
    public event Action? RecordsChanged;

    public StashForm(AppSettings settings)
    {
        _settings = settings;
        DarkUi.StyleWindow(this, "poe2verlay – stash value");
        MinimumSize = new Size(560, 380);

        _rename = DarkUi.Button("Rename");
        _delete = DarkUi.Button("Delete");
        _rename.Click += (_, _) => RenameSelected();
        _delete.Click += (_, _) => DeleteSelected();
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, BackColor = DarkUi.Back, Padding = new Padding(6, 6, 6, 4),
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
        };
        bar.Controls.AddRange(new Control[] { _delete, _rename });

        _summary = DarkUi.Summary();

        _tabs = DarkUi.List();
        _tabs.LabelEdit = true;
        _tabs.SelectedIndexChanged += (_, _) => ShowItems();
        _tabs.AfterLabelEdit += (_, e) =>
        {
            if (e.Label is not { Length: > 0 } name || _tabs.Items[e.Item].Tag is not StashTabRecord record)
            {
                e.CancelEdit = true;
                return;
            }
            record.Name = name.Trim();
            record.UserNamed = true;
            RecordsChanged?.Invoke();
            BeginInvoke(Rebuild);
        };
        _tabs.BeforeLabelEdit += (_, e) =>
        {
            if (_tabs.Items[e.Item].Tag is not StashTabRecord) e.CancelEdit = true; // "All tabs" row
        };

        _items = DarkUi.List();

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            BackColor = DarkUi.Header,
            SplitterWidth = 5,
        };
        split.Panel1.Controls.Add(_tabs);
        split.Panel2.Controls.Add(_items);

        Controls.Add(split);
        Controls.Add(_summary);
        Controls.Add(bar);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Hide();
            if (e.KeyCode == Keys.F2) RenameSelected();
            if (e.KeyCode == Keys.Delete) DeleteSelected();
        };
        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            Hide();
        };
        ResizeEnd += (_, _) => BoundsSaved?.Invoke(Bounds);

        DarkUi.PlaceWindow(this, _settings.StashWindowBounds, 700, 110, 760, 680);
        Shown += (_, _) => split.SplitterDistance = Math.Max(LogicalToDeviceUnits(120), split.Height / 3);
        Rebuild();
    }

    protected override bool ShowWithoutActivation => true;

    public void SetData(List<StashTabRecord> records, ItemMatcher matcher, PriceDb db, AppSettings settings, StashTabRecord? select)
    {
        _records = records;
        _db = db;
        _values.Clear();
        foreach (var r in records) _values[r] = Valuation.Value(r.Snapshot, matcher, settings);
        Rebuild(select);
    }

    void Rebuild() => Rebuild(_tabs.SelectedItems.Count == 1 ? _tabs.SelectedItems[0].Tag as StashTabRecord : null);

    void Rebuild(StashTabRecord? select)
    {
        DarkUi.Columns(_tabs, ("Tab", 170, false), ("Items", 60, true), ("Value div", 130, true), ("Value c", 120, true),
            ("Type", 70, false), ("Scanned", 120, false));

        _tabs.BeginUpdate();
        try
        {
            _tabs.Items.Clear();
            if (_db == null || _records.Count == 0)
            {
                _summary.Text = $"No stash tabs scanned yet.\nOpen a stash tab in game and press {_settings.StashHotkey} (don't move the mouse while it scans). Repeat for every tab.";
                ShowItems();
                return;
            }

            var c = _db.ChaosPerDivine;
            var min = _values.Values.Sum(v => v.Min);
            var max = _values.Values.Sum(v => v.Max);
            _summary.Text =
                $"Stash:  {Fmt.Range(min, max)} div     ({Fmt.Range(min * c, max * c)} c)\n" +
                $"{_records.Count} tab(s), {_values.Values.Sum(v => v.Items.Count)} item kinds  ·  min = only items with real trade volume, max = everything with a price\n" +
                $"prices: {_db.League}, {Fmt.Age(_db.UpdatedUtc)}  ·  F2 renames the selected tab, Delete removes it";

            var all = DarkUi.Row("All tabs", _values.Values.Sum(v => v.Items.Count).ToString(CultureInfo.InvariantCulture),
                Fmt.Range(min, max), Fmt.Range(min * c, max * c), "", "");
            DarkUi.Tint(all, Trade.Ok);
            foreach (ListViewItem.ListViewSubItem sub in all.SubItems) sub.ForeColor = DarkUi.Gold;
            _tabs.Items.Add(all);

            foreach (var record in _records.OrderByDescending(r => _values[r].Max))
            {
                var v = _values[record];
                var row = DarkUi.Row(record.Name, v.Items.Count.ToString(CultureInfo.InvariantCulture),
                    Fmt.Range(v.Min, v.Max), Fmt.Range(v.Min * c, v.Max * c),
                    record.Layout.ToString().ToLowerInvariant(),
                    record.Snapshot.Time.ToString("dd MMM HH:mm", CultureInfo.InvariantCulture) +
                    (record.Snapshot.UnreadCells > 0 ? $"  ⚠{record.Snapshot.UnreadCells}" : ""));
                DarkUi.Tint(row, Trade.Ok);
                row.Tag = record;
                _tabs.Items.Add(row);
                if (record == select) row.Selected = true;
            }
            if (_tabs.SelectedItems.Count == 0) _tabs.Items[0].Selected = true;
        }
        finally
        {
            _tabs.EndUpdate();
        }
        DarkUi.FillLastColumn(_tabs);
        ShowItems();
    }

    /// <summary>Items of the selected tab (or all tabs merged), most valuable first.</summary>
    void ShowItems()
    {
        var record = _tabs.SelectedItems.Count == 1 ? _tabs.SelectedItems[0].Tag as StashTabRecord : null;
        var allTabs = record == null;
        DarkUi.Columns(_items, ("Item", 240, false), ("Qty", 55, true), ("Each div", 80, true), ("Total div", 85, true),
            ("Total c", 75, true), ("Trade", 100, false), (allTabs ? "Tabs" : "", 120, false));

        _items.BeginUpdate();
        try
        {
            _items.Items.Clear();
            if (_db == null || _records.Count == 0) return;

            var rows = allTabs
                ? _records.SelectMany(r => _values[r].Items.Select(i => (Item: i, Tab: r.Name)))
                    .GroupBy(x => x.Item.Key)
                    .Select(g => (Item: Merge(g.Select(x => x.Item)), Tabs: string.Join(", ", g.Select(x => x.Tab).Distinct())))
                : _values[record!].Items.Select(i => (Item: i, Tabs: ""));

            foreach (var (i, tabs) in rows.OrderBy(x => x.Item.Entry == null).ThenByDescending(x => x.Item.Total).ThenBy(x => x.Item.Name))
            {
                var priced = i.Entry != null;
                var row = DarkUi.Row(DarkUi.ItemName(i.Name, i.Detail, priced), i.Qty.ToString(CultureInfo.InvariantCulture),
                    priced ? Fmt.Number(i.Unit) : "–",
                    priced ? Fmt.Number(i.Total) : "–",
                    priced ? Fmt.Number(i.Total * _db.ChaosPerDivine) : "–",
                    Valuation.Text(i.Trade), tabs);
                DarkUi.Tint(row, i.Trade);
                if (priced && i.Total >= 1 && i.Trade == Trade.Ok)
                {
                    row.SubItems[3].ForeColor = DarkUi.Gold;
                    row.SubItems[4].ForeColor = DarkUi.Gold;
                }
                _items.Items.Add(row);
            }
        }
        finally
        {
            _items.EndUpdate();
        }
        DarkUi.FillLastColumn(_items);
    }

    static ValuedItem Merge(IEnumerable<ValuedItem> copies)
    {
        var list = copies.ToList();
        var first = list[0];
        return new ValuedItem
        {
            Key = first.Key, Name = first.Name, Detail = first.Detail, Entry = first.Entry, Trade = first.Trade,
            Qty = list.Sum(i => i.Qty),
        };
    }

    void RenameSelected()
    {
        if (_tabs.SelectedItems.Count == 1 && _tabs.SelectedItems[0].Tag is StashTabRecord)
        {
            _tabs.Focus();
            _tabs.SelectedItems[0].BeginEdit();
        }
    }

    void DeleteSelected()
    {
        if (_tabs.SelectedItems.Count != 1 || _tabs.SelectedItems[0].Tag is not StashTabRecord record) return;
        var answer = MessageBox.Show(this, $"Remove \"{record.Name}\" from the stash list?", "poe2verlay",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;
        _records.Remove(record);
        _values.Remove(record);
        RecordsChanged?.Invoke();
        Rebuild(null);
    }
}
