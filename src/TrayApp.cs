using System.Diagnostics;
using System.Globalization;

namespace Poe2Verlay;

internal sealed class TrayApp : ApplicationContext
{
    enum OverlayKind { None, Scan, Auto, Item, Message, Rumours, Report }

    readonly PriceService _prices = new();
    readonly RunTracker _runs = new();
    readonly OverlayForm _overlay = new();
    readonly NotifyIcon _tray;
    readonly KeyboardHook _hook;
    readonly System.Windows.Forms.Timer _watchdog = new() { Interval = 300 };
    readonly System.Windows.Forms.Timer _autoTimer = new();
    readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 60_000 };
    readonly System.Windows.Forms.Timer _verifyTimer = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer _rumourTimer = new() { Interval = 150 };
    readonly ClientLog _clientLog = new();
    readonly FarmSession _session = new();
    readonly System.Windows.Forms.Timer _sessionTimer = new() { Interval = 1000 };
    readonly bool _firstRun;

    AppSettings _s;
    PriceDb? _db;
    ItemMatcher? _matcher;
    readonly OcrScanner? _ocr;
    readonly string _ocrError;
    List<string> _leagues = new();
    Keys _scanKey, _itemKey, _autoKey, _runStartKey, _runEndKey, _windowKey, _stashKey, _sessionKey, _shotKey;

    bool _scanning, _loading, _inventoryScanning, _cancelInventory, _verifying, _shooting;
    RectangleF _scanBounds;
    Rectangle _scanArea;
    int _scanCount;
    OverlayKind _kind;
    DateTime _hideAt = DateTime.MaxValue;
    IntPtr _shownForeground;
    Point _itemCursor;
    SettingsForm? _settingsForm;
    ClickWatcher? _clickWatcher;
    InventoryForm? _inventoryForm;
    StashForm? _stashForm;
    readonly List<StashTabRecord> _stashTabs = StashStore.Load();

    // Expedition rumours: the uncharted-waters tooltip under a resting cursor.
    bool _rumourBusy, _rumourDetails;
    Point _rumourCursor, _rumourShownCursor;
    DateTime _rumourStillSince, _nextLogPoll;
    int _rumourTries;
    RumourArea? _rumourArea;
    Rumour? _rumourIsland;
    RectangleF _rumourTooltip;
    Rectangle _rumourScreen;
    List<RectangleF> _rumourText = new();

    public TrayApp()
    {
        _firstRun = !AppSettings.Exists;
        _s = AppSettings.Load();
        _overlay.EnsureHandle(); // also installs the WinForms SynchronizationContext for the awaits below

        _ocr = OcrScanner.TryCreate(out _ocrError);
        if (_ocr == null) Log.Write(_ocrError);

        _tray = new NotifyIcon { Icon = LoadIcon(), Text = "poe2verlay", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        _tray.ContextMenuStrip.Opening += (_, e) => { BuildMenu(); e.Cancel = false; };
        _tray.DoubleClick += (_, _) => OpenSettings();
        BuildMenu();

        ApplyHotkeys();
        _hook = new KeyboardHook { KeyDown = OnKeyDown };

        _watchdog.Tick += (_, _) => Watchdog();
        _watchdog.Start();
        _autoTimer.Tick += async (_, _) => await AutoTickAsync();
        ApplyAutoScan();
        _refreshTimer.Tick += async (_, _) => await RefreshIfStaleAsync();
        _refreshTimer.Start();
        _verifyTimer.Tick += async (_, _) => await VerifyScanAsync();
        _verifyTimer.Start();
        _rumourTimer.Tick += async (_, _) => await RumourTickAsync();
        _rumourTimer.Start();
        _sessionTimer.Tick += (_, _) => _session.Tick(GameWindow.ForegroundPoe() != IntPtr.Zero, _s.SessionIdleSeconds);
        _sessionTimer.Start();
        _clientLog.AreaChanged += area =>
        {
            _session.AreaEntered(area);
            // Charting an area or going anywhere else: the rumours seen so far no longer apply.
            _rumourArea = null;
            _rumourIsland = null;
            if (_kind == OverlayKind.Rumours) HideOverlay();
        };

        _ = InitAsync();
    }

    string CurrentLeague => !string.IsNullOrEmpty(_s.League) ? _s.League : _leagues.FirstOrDefault() ?? "";

    async Task InitAsync()
    {
        try
        {
            _leagues = await _prices.GetLeaguesAsync();
        }
        catch (Exception ex)
        {
            Log.Write("league list: " + ex.Message);
        }

        if (string.IsNullOrEmpty(_s.League) && _leagues.Count > 0)
        {
            _s.League = _leagues[0];
            _s.Save();
        }

        var ocrNote = _ocr == null ? "\n" + _ocrError : "";
        if (string.IsNullOrEmpty(CurrentLeague))
        {
            Balloon("Could not reach poe.ninja to get the league list. Will retry." + ocrNote, ToolTipIcon.Warning);
            return;
        }

        Balloon($"League: {CurrentLeague}\n{_s.ScanHotkey} = price choices on screen, {_s.ItemHotkey} = item under cursor\n{_s.RunStartHotkey} / {_s.RunEndHotkey} = start / finish run (inventory open)" + ocrNote,
            _ocr == null ? ToolTipIcon.Warning : ToolTipIcon.Info);

        var loading = LoadPricesAsync(force: false);
        _ = RumourBook.RefreshAsync(_s.RumourSheet);
        if (_firstRun) OpenSettings();
        await loading;
    }

    async Task RefreshIfStaleAsync()
    {
        if (_leagues.Count == 0)
        {
            try { _leagues = await _prices.GetLeaguesAsync(); } catch { /* offline */ }
            if (string.IsNullOrEmpty(_s.League) && _leagues.Count > 0) { _s.League = _leagues[0]; _s.Save(); }
        }
        if (_db == null || (DateTime.UtcNow - _db.UpdatedUtc).TotalMinutes >= _s.PriceRefreshMinutes)
            await LoadPricesAsync(force: false);
        await RumourBook.RefreshAsync(_s.RumourSheet);
    }

    async Task LoadPricesAsync(bool force)
    {
        var league = CurrentLeague;
        if (_loading || string.IsNullOrEmpty(league)) return;
        _loading = true;
        try
        {
            if (!force && (_db == null || _db.League != league) && PriceCache.Load(league) is { } cached)
            {
                SetDb(cached);
                if ((DateTime.UtcNow - cached.UpdatedUtc).TotalMinutes < _s.PriceRefreshMinutes) return;
            }

            var db = await _prices.FetchAsync(league, _s.IncludeUniques);
            PriceCache.Save(db);
            if (league == CurrentLeague) SetDb(db);
            if (force) Balloon($"Prices updated: {db.Entries.Count} items ({league})", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Write("price load: " + ex);
            Balloon($"Price update failed ({league}): {ex.Message}", ToolTipIcon.Warning);
        }
        finally
        {
            _loading = false;
        }

        // The league was switched while this download was running.
        if (league != CurrentLeague) await LoadPricesAsync(force: false);
    }

    void SetDb(PriceDb db)
    {
        _db = db;
        _matcher = new ItemMatcher(db);
        RumourBook.AldursSagaDrops = db.League.Contains("Aldur", StringComparison.OrdinalIgnoreCase);
        Log.Write($"prices: {db.League}, {db.Entries.Count} entries, {_matcher.Count} names, 1 div = {db.ChaosPerDivine:0.##} c");
    }

    async Task SetLeagueAsync(string league)
    {
        if (league == _s.League && _db?.League == league) return;
        _s.League = league;
        _s.Save();
        _db = null;
        _matcher = null;
        HideOverlay();
        await LoadPricesAsync(force: false);
        if (_db != null) Balloon($"League: {league} ({_db.Entries.Count} prices, {Fmt.Age(_db.UpdatedUtc)})", ToolTipIcon.Info);
    }

    // ---------------------------------------------------------------- hotkeys

    void ApplyHotkeys()
    {
        _scanKey = Hotkey.Parse(_s.ScanHotkey);
        _itemKey = Hotkey.Parse(_s.ItemHotkey);
        _autoKey = Hotkey.Parse(_s.AutoScanHotkey);
        _runStartKey = Hotkey.Parse(_s.RunStartHotkey);
        _runEndKey = Hotkey.Parse(_s.RunEndHotkey);
        _windowKey = Hotkey.Parse(_s.InventoryWindowHotkey);
        _stashKey = Hotkey.Parse(_s.StashHotkey);
        _sessionKey = Hotkey.Parse(_s.SessionHotkey);
        _shotKey = Hotkey.Parse(_s.ScreenshotHotkey);
    }

    /// <summary>Hotkey compare where the numpad digits count as the normal digits (Ctrl+0 either way).</summary>
    static bool Is(Keys pressed, Keys hotkey)
    {
        if (hotkey == Keys.None) return false;
        var code = pressed & Keys.KeyCode;
        if (code is >= Keys.NumPad0 and <= Keys.NumPad9)
            pressed = (pressed & Keys.Modifiers) | (Keys.D0 + (code - Keys.NumPad0));
        return pressed == hotkey;
    }

    bool OnKeyDown(Keys key)
    {
        if ((key & Keys.KeyCode) == Keys.Escape)
        {
            if (_inventoryScanning) _cancelInventory = true;
            if (_overlay.Visible) _overlay.BeginInvoke(HideOverlay);
            return false;
        }

        var foreground = Native.GetForegroundWindow();
        if (_settingsForm != null && foreground == _settingsForm.Handle) return false;
        if (_s.HotkeysOnlyInGame && !GameWindow.IsPoe(foreground)) return false;

        var ours = new[] { _scanKey, _itemKey, _autoKey, _runStartKey, _runEndKey, _windowKey, _stashKey, _sessionKey, _shotKey }.Any(h => Is(key, h));
        if (_inventoryScanning) return ours; // swallow, but don't start anything mid-scan

        if (Is(key, _runStartKey))
        {
            Run(() => InventoryRunAsync(start: true));
            return true;
        }
        if (Is(key, _runEndKey))
        {
            Run(() => InventoryRunAsync(start: false));
            return true;
        }
        if (Is(key, _stashKey))
        {
            Run(StashScanAsync);
            return true;
        }
        if (Is(key, _windowKey))
        {
            _overlay.BeginInvoke(ToggleInventoryWindow);
            return true;
        }
        if (Is(key, _sessionKey))
        {
            _overlay.BeginInvoke(ToggleSession);
            return true;
        }
        if (Is(key, _shotKey))
        {
            Run(ScreenshotAsync);
            return true;
        }

        if (key == _scanKey && _scanKey != Keys.None)
        {
            Run(ManualScanAsync);
            return true;
        }
        if (key == _itemKey && _itemKey != Keys.None)
        {
            Run(ItemCheckAsync);
            return true;
        }
        if (key == _autoKey && _autoKey != Keys.None)
        {
            _overlay.BeginInvoke(ToggleAutoScan);
            return true;
        }
        return false;
    }

    /// <summary>Leave the hook callback quickly; do the work on the next message loop turn.</summary>
    void Run(Func<Task> work) => _overlay.BeginInvoke(async () =>
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Log.Write("hotkey: " + ex);
        }
    });

    // ---------------------------------------------------------------- screenshot

    static string ScreenshotDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "poe2verlay");

    /// <summary>
    /// The game with our overlay on it. Our windows are kept out of screen captures (so OCR never reads them, which
    /// also hides them from normal screenshots): let them be captured for one frame, then hide them again.
    /// </summary>
    async Task ScreenshotAsync()
    {
        if (_shooting || _scanning) return;
        var windows = new List<IntPtr> { _overlay.Handle };
        if (_inventoryForm is { IsHandleCreated: true, Visible: true } form) windows.Add(form.Handle);
        var area = ScanArea();
        Bitmap bmp;
        _shooting = true;
        try
        {
            foreach (var w in windows) Native.SetWindowDisplayAffinity(w, Native.WDA_NONE);
            await Task.Delay(150); // let the compositor draw a frame with them
            bmp = GameWindow.Capture(area, layered: true);
        }
        finally
        {
            foreach (var w in windows) Native.SetWindowDisplayAffinity(w, Native.WDA_EXCLUDEFROMCAPTURE);
            _shooting = false;
        }

        using (bmp)
        {
            Directory.CreateDirectory(ScreenshotDir);
            var path = Path.Combine(ScreenshotDir, $"poe2verlay-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Log.Write("screenshot: " + path);
            Balloon("Screenshot saved to Pictures\\poe2verlay", ToolTipIcon.Info);
        }
    }

    // ---------------------------------------------------------------- scanning

    async Task ManualScanAsync()
    {
        if (_overlay.Visible && _kind is OverlayKind.Scan or OverlayKind.Auto)
        {
            HideOverlay();
            if (!_s.AutoScan) return;
        }
        await ScanAsync(manual: true);
    }

    Rectangle ScanArea()
    {
        var poe = GameWindow.ForegroundPoe();
        if (GameWindow.ClientRect(poe) is { } client) return client;
        return Screen.FromPoint(Cursor.Position).Bounds;
    }

    Rectangle ScanRegion(Rectangle area)
    {
        double l = Math.Clamp(_s.RegionLeft, 0, 1), t = Math.Clamp(_s.RegionTop, 0, 1);
        double r = Math.Clamp(_s.RegionRight, 0, 1), b = Math.Clamp(_s.RegionBottom, 0, 1);
        if (r - l < 0.05 || b - t < 0.05) return area;
        return Rectangle.FromLTRB(
            area.Left + (int)(area.Width * l), area.Top + (int)(area.Height * t),
            area.Left + (int)(area.Width * r), area.Top + (int)(area.Height * b));
    }

    async Task ScanAsync(bool manual)
    {
        if (_scanning) return;
        if (_ocr == null)
        {
            if (manual) ShowMessage("OCR unavailable", _ocrError);
            return;
        }
        if (_db == null || _matcher == null)
        {
            if (manual) ShowMessage("Prices not loaded yet", _loading ? "downloading from poe.ninja…" : "check the league / internet connection");
            return;
        }

        _scanning = true;
        try
        {
            var area = ScanArea();
            var region = ScanRegion(area);
            using var capture = GameWindow.Capture(region);
            var scale = _s.OcrScale > 0 ? _s.OcrScale : OcrScanner.AutoScale(region.Height, manual ? 2160 : 1600);

            var sw = Stopwatch.StartNew();
            var segments = await _ocr.RecognizeAsync(capture, scale, region.Location);
            var detections = ItemMatcher.PanelRows(_matcher.Find(segments));
            sw.Stop();

            var bars = LabelBuilder.BarsForDetections(detections, _db, _s);
            if (manual && _s.SaveDebug)
            {
                var info = string.Create(CultureInfo.InvariantCulture,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} league={_db.League} scale={scale:0.##} ocr+match={sw.ElapsedMilliseconds}ms lang={_ocr.Language} tilt={_ocr.LastTextAngle:0.##}");
                DebugDump.Save(capture, region, segments, detections, bars, info);
            }

            // Auto-scan must not replace a manual result or an item/message label that is still showing.
            if (!manual && _overlay.Visible && _kind is OverlayKind.Scan or OverlayKind.Item or OverlayKind.Message or OverlayKind.Rumours or OverlayKind.Report) return;

            if (detections.Count == 0)
            {
                if (manual) ShowMessage("No priced items found", $"{segments.Count} text lines read in {sw.ElapsedMilliseconds} ms · {_db.League} · prices {Fmt.Age(_db.UpdatedUtc)}", area);
                else if (_kind == OverlayKind.Auto) HideOverlay();
                return;
            }

            // Manual results stay up until the panel closes (VerifyScanAsync), Esc, or the scan key again.
            ShowOverlay(area, Array.Empty<OverlayLabel>(), manual ? OverlayKind.Scan : OverlayKind.Auto, manual ? 300 : 0,
                segments.Select(sg => sg.Bounds).ToList(), bars);
            _scanBounds = detections.Select(d => d.Bounds).Aggregate(RectangleF.Union);
            _scanArea = area;
            _scanCount = detections.Count;
        }
        catch (Exception ex)
        {
            Log.Write("scan: " + ex);
            if (manual) ShowMessage("Scan failed", ex.Message);
        }
        finally
        {
            _scanning = false;
        }
    }

    /// <summary>
    /// Re-read just the rows we priced; once most of them are gone the panel was closed (Esc, X, reward picked),
    /// so the bars go too.
    /// </summary>
    async Task VerifyScanAsync()
    {
        if (_verifying || _scanning || _shooting || _inventoryScanning || !_overlay.Visible || _kind != OverlayKind.Scan) return;
        if (_ocr == null || _matcher == null || GameWindow.ForegroundPoe() == IntPtr.Zero) return;
        _verifying = true;
        try
        {
            var region = Rectangle.Round(RectangleF.Inflate(_scanBounds, 12, 12));
            region.Intersect(_scanArea);
            if (region.Width < 8 || region.Height < 8) return;
            using var capture = GameWindow.Capture(region);
            var segments = await _ocr.RecognizeAsync(capture, 2.0, region.Location);
            if (_kind != OverlayKind.Scan || _matcher == null) return;
            if (_matcher.Find(segments).Count * 2 < _scanCount) HideOverlay();
        }
        catch (Exception ex)
        {
            Log.Write("verify: " + ex.Message);
        }
        finally
        {
            _verifying = false;
        }
    }

    async Task AutoTickAsync()
    {
        if (!_s.AutoScan || _scanning || _shooting || GameWindow.ForegroundPoe() == IntPtr.Zero) return;
        if (_overlay.Visible && _kind is OverlayKind.Scan or OverlayKind.Item or OverlayKind.Message or OverlayKind.Rumours or OverlayKind.Report) return;
        await ScanAsync(manual: false);
    }

    async Task ItemCheckAsync()
    {
        if (_db == null || _matcher == null)
        {
            ShowMessage("Prices not loaded yet", _loading ? "downloading from poe.ninja…" : "check the league / internet connection");
            return;
        }

        var cursor = Cursor.Position;
        var text = await ItemText.CopyHoveredItemAsync();
        var item = text == null ? null : ItemText.Parse(text);
        if (item == null)
        {
            ShowMessageAt(cursor, "Nothing copied", "hover an item, then press " + _s.ItemHotkey);
            return;
        }

        var entries = ItemText.Lookup(_matcher, item);
        if (entries == null || entries.Count == 0)
        {
            ShowMessageAt(cursor, item.Name, $"no poe.ninja price ({item.Rarity})");
            return;
        }

        var entry = ItemMatcher.PickPrimary(entries);
        var label = LabelBuilder.ForItem(cursor, item.Name, entry, entries, item.Stack, _db, _s);
        var area = Screen.FromPoint(cursor).Bounds;
        ShowOverlay(area, new[] { label }, OverlayKind.Item, _s.OverlaySeconds > 0 ? _s.OverlaySeconds : 15);
        _itemCursor = cursor;
    }

    // ---------------------------------------------------------------- runs

    async Task InventoryRunAsync(bool start)
    {
        if (_inventoryScanning) return;
        var hwnd = GameWindow.ForegroundPoe();
        if (GameWindow.ClientRect(hwnd) is not { } client) return;

        if (!start && _runs.Start == null)
        {
            ShowMessage("No run in progress", $"press {_s.RunStartHotkey} with the inventory open when the run starts");
            return;
        }
        if (_db == null || _matcher == null)
        {
            ShowMessage("Prices not loaded yet", _loading ? "downloading from poe.ninja\u2026" : "check the league / internet connection");
            return;
        }

        HideOverlay();
        var restoreWindows = HideWindowsForScan();
        var windowWasVisible = restoreWindows.Inventory;
        var geometry = GridGeometry.Inventory(client, _s);
        var original = Cursor.Position;
        Cursor.Position = InventoryScanner.InventoryPark(client, geometry); // a tooltip could hide the title
        await Task.Delay(120);
        if (_ocr != null && !await InventoryScanner.InventoryOpenAsync(_ocr, client, geometry))
        {
            Cursor.Position = original;
            ShowMessage("Open your inventory first", $"then press {(start ? _s.RunStartHotkey : _s.RunEndHotkey)} again \u2013 don't move the mouse while it scans");
            RestoreWindows(restoreWindows);
            return;
        }

        _inventoryScanning = true;
        _cancelInventory = false;
        try
        {
            InventorySnapshot snapshot;
            try
            {
                snapshot = await InventoryScanner.ScanAsync(client, geometry, InventoryScanner.InventoryPark(client, geometry),
                    false, _s, () => _cancelInventory || GameWindow.ForegroundPoe() == IntPtr.Zero);
            }
            finally
            {
                Cursor.Position = original;
            }
            if (restoreWindows.Stash) _stashForm?.Show();
            Log.Write($"inventory scan ({(start ? "start" : "end")}): {snapshot.Items.Count} items, {snapshot.OccupiedCells} slots, {snapshot.UnreadCells} unread");

            var form = InventoryWindow();
            if (start)
            {
                // Ctrl+I only tells the total; the item list is one Ctrl+0 away.
                _runs.Begin(snapshot);
                _session.RunStarted(snapshot);
                var value = Valuation.Value(snapshot, _matcher, _s);
                form.SetStart(value, _db);
                if (windowWasVisible) form.Show();
                var unread = snapshot.UnreadCells > 0 ? $"  \u00b7  \u26a0 {snapshot.UnreadCells} slot(s) unread" : "";
                ShowMessage($"Inventory:  {Fmt.Range(value.Min, value.Max)} div   ({Fmt.Range(value.Min * _db.ChaosPerDivine, value.Max * _db.ChaosPerDivine)} c)",
                    $"{value.Items.Count} items, {value.Unpriced} without a price{unread}  \u00b7  {_s.RunEndHotkey} at the end  \u00b7  {_s.InventoryWindowHotkey} for details",
                    client, seconds: 8);
            }
            else
            {
                var report = _runs.Finish(snapshot, _matcher, _s);
                form.SetReport(report, _db, _session.RunFinished(report));
                form.Show();
            }
        }
        catch (OperationCanceledException)
        {
            ShowMessage("Inventory scan cancelled", start ? "the run was not started" : "the run is still in progress", client);
            RestoreWindows(restoreWindows);
        }
        finally
        {
            _inventoryScanning = false;
        }
    }

    /// <summary>Our windows must not cover the slots while the cursor walks them.</summary>
    (bool Inventory, bool Stash) HideWindowsForScan()
    {
        var state = (_inventoryForm?.Visible == true, _stashForm?.Visible == true);
        _inventoryForm?.Hide();
        _stashForm?.Hide();
        return state;
    }

    void RestoreWindows((bool Inventory, bool Stash) state)
    {
        if (state.Inventory) _inventoryForm?.Show();
        if (state.Stash) _stashForm?.Show();
    }

    // ---------------------------------------------------------------- stash

    async Task StashScanAsync()
    {
        if (_inventoryScanning || _ocr == null) return;
        var hwnd = GameWindow.ForegroundPoe();
        if (GameWindow.ClientRect(hwnd) is not { } client) return;
        if (_db == null || _matcher == null)
        {
            ShowMessage("Prices not loaded yet", _loading ? "downloading from poe.ninja\u2026" : "check the league / internet connection");
            return;
        }

        HideOverlay();
        var restore = HideWindowsForScan();
        var k = client.Height / 1080f;
        var grid = GridGeometry.Stash(client, _s, 12);
        var original = Cursor.Position;
        string? clipboard = null;
        try { if (Clipboard.ContainsText()) clipboard = Clipboard.GetText(); } catch { /* busy */ }

        _inventoryScanning = true;
        _cancelInventory = false;
        bool Cancelled() => _cancelInventory || GameWindow.ForegroundPoe() == IntPtr.Zero;
        try
        {
            // Park the cursor first: the tooltip of a hovered item would cover the title and the grid.
            var park = new Point((int)(grid.Left + grid.Cell * 6), (int)(grid.Bounds.Bottom + 25 * k)); // empty strip under the grid
            Cursor.Position = park;
            await Task.Delay(150);

            var titleArea = Rectangle.FromLTRB(client.Left, client.Top, (int)(grid.Bounds.Right + 40 * k), (int)grid.Top);
            var titles = await InventoryScanner.ReadTitleAreaAsync(_ocr, titleArea);
            StashLayout layout;
            string detail;
            bool[,] occupied;
            using (var capture = GameWindow.Capture(client))
            {
                layout = StashDetect.Layout(capture, client, grid.Bounds, out detail);
                occupied = InventoryScanner.Occupancy(capture, client, grid, out _);
                if (_s.SaveDebug) DebugDump.SaveRaw(capture, "stash");
            }
            var stashOpen = InventoryScanner.HasWord(titles, "stash") || layout != StashLayout.Special;
            Log.Write($"stash: open={stashOpen} layout={layout} ({detail}) title=[{string.Join(" | ", titles.Select(t => t.Text))}]");
            if (!stashOpen)
            {
                ShowMessage("Open your stash first", $"then press {_s.StashHotkey} on each tab \u2013 don't move the mouse while it scans");
                RestoreWindows(restore);
                return;
            }

            ShowMessage("Scanning stash tab\u2026", "don't move the mouse \u00b7 Esc cancels", client, seconds: 120);
            layout = _s.StashLayoutMode.ToLowerInvariant() switch
            {
                "normal" => StashLayout.Normal,
                "quad" => StashLayout.Quad,
                "special" => StashLayout.Special,
                // Grid lines unclear (packed tab): let the items tell whether slots are 52 px.
                _ when layout != StashLayout.Normal && await InventoryScanner.ProbeNormalAsync(grid, occupied, Cancelled) == true
                    => StashLayout.Normal,
                _ => layout,
            };

            var geometry = layout == StashLayout.Normal ? grid : GridGeometry.Stash(client, _s, 24);
            var snapshot = await InventoryScanner.ScanAsync(client, geometry, park, layout != StashLayout.Normal, _s, Cancelled);
            HideOverlay();
            Log.Write($"stash scan ({layout}): {snapshot.Items.Count} items, {snapshot.OccupiedCells} slots, {snapshot.UnreadCells} unread");

            var value = Valuation.Value(snapshot, _matcher, _s);
            var record = StashStore.Upsert(_stashTabs, snapshot, layout, value, titles.Select(t => t.Text));
            var form = StashWindow();
            form.SetData(_stashTabs, _matcher, _db, _s, record);
            if (restore.Inventory) _inventoryForm?.Show();
            form.Show();
            ShowMessage($"{record.Name}:  {Fmt.Range(value.Min, value.Max)} div   ({Fmt.Range(value.Min * _db.ChaosPerDivine, value.Max * _db.ChaosPerDivine)} c)",
                $"{value.Items.Count} item kinds \u00b7 {layout.ToString().ToLowerInvariant()} tab" +
                (snapshot.UnreadCells > 0 ? $" \u00b7 \u26a0 {snapshot.UnreadCells} slot(s) unread" : ""), client, seconds: 6);
        }
        catch (OperationCanceledException)
        {
            ShowMessage("Stash scan cancelled", null, client);
            RestoreWindows(restore);
        }
        finally
        {
            _inventoryScanning = false;
            Cursor.Position = original;
            if (clipboard != null)
            {
                try { Clipboard.SetText(clipboard); } catch { /* best effort */ }
            }
        }
    }

    StashForm StashWindow()
    {
        if (_stashForm != null) return _stashForm;
        _stashForm = new StashForm(_s);
        _stashForm.BoundsSaved += b =>
        {
            _s.StashWindowBounds = $"{b.X},{b.Y},{b.Width},{b.Height}";
            _s.Save();
        };
        _stashForm.RecordsChanged += () => StashStore.Save(_stashTabs);
        return _stashForm;
    }

    void ToggleStashWindow()
    {
        var form = StashWindow();
        if (form.Visible)
        {
            form.Hide();
            return;
        }
        if (_matcher != null && _db != null) form.SetData(_stashTabs, _matcher, _db, _s, null);
        form.Show();
    }

    InventoryForm InventoryWindow()
    {
        if (_inventoryForm != null) return _inventoryForm;
        _inventoryForm = new InventoryForm(_s) { SessionLine = SessionLine, PriceText = name => RumourLabels.PriceText(name, _matcher) };
        _inventoryForm.BoundsSaved += b =>
        {
            _s.InventoryWindowBounds = $"{b.X},{b.Y},{b.Width},{b.Height}";
            _s.Save();
        };
        _inventoryForm.CompactChanged += compact =>
        {
            _s.RunWindowCompact = compact;
            _s.Save();
        };
        return _inventoryForm;
    }

    void ToggleInventoryWindow()
    {
        var form = InventoryWindow();
        if (form.Visible)
        {
            form.Hide();
            return;
        }
        // After an app restart only the Ctrl+I snapshot survives; show that.
        if (_runs.LastReport == null && _runs.Start != null && _matcher != null && _db != null && !form.HasData)
            form.SetStart(Valuation.Value(_runs.Start, _matcher, _s), _db);
        form.ShowDefaultPage();
        form.Show();
    }

    // ---------------------------------------------------------------- report + farming session on screen

    /// <summary>The farming session under the run sums.</summary>
    string SessionLine()
    {
        if (!_session.Running) return $"no farming session  \u00b7  {_s.SessionHotkey} starts one (div/h on played time only)";
        var h = _session.Played.TotalHours;
        return $"Session: {Fmt.Range(_session.ProfitMin, _session.ProfitMax, signed: true)} div" +
               (h > 0.01 ? $"  \u00b7  \u2248 {Fmt.Range(_session.ProfitMin / h, _session.ProfitMax / h, signed: true)} div/h" : "") +
               $"  \u00b7  {Fmt.Duration(_session.Played)} played{(_session.Paused ? " (paused)" : "")}  \u00b7  {_session.Runs} run(s), {_session.Maps} map(s)";
    }

    void ToggleSession()
    {
        var area = ScanArea();
        OverlayPanel panel;
        if (_session.Running)
        {
            panel = ReportPanels.Session("Farming session stopped", _session, _db, _s, area);
            _session.Stop();
        }
        else
        {
            _session.Start();
            panel = ReportPanels.Session("Farming session started", _session, _db, _s, area);
        }
        if (GameWindow.ForegroundPoe() == IntPtr.Zero)
            Balloon(panel.Header[0].Text + (panel.Header.Count > 1 ? "\n" + panel.Header[1].Text : ""), ToolTipIcon.Info);
        else
            ShowOverlay(area, Array.Empty<OverlayLabel>(), OverlayKind.Report, 15, panel: panel);
    }

    // ---------------------------------------------------------------- expedition rumours

    /// <summary>
    /// In a hideout, when the cursor rests, look for the "Island Rumours" tooltip of uncharted waters around it and
    /// rate the islands. Holding Ctrl swaps in the long version (why, prices, sagas, tablets).
    /// </summary>
    async Task RumourTickAsync()
    {
        if (_shooting) return; // our labels are capturable right now: don't OCR them
        if (DateTime.UtcNow >= _nextLogPoll)
        {
            _nextLogPoll = DateTime.UtcNow.AddSeconds(1);
            _clientLog.Poll();
        }
        if (!_s.ExpeditionRumours || _ocr == null || _rumourBusy || _scanning || _inventoryScanning) return;
        var showing = _overlay.Visible && _kind == OverlayKind.Rumours;
        if (_overlay.Visible && !showing) return; // a price label or message is up
        var hwnd = GameWindow.ForegroundPoe();
        if (hwnd == IntPtr.Zero || !_clientLog.MapDevicePossible)
        {
            if (showing) HideOverlay();
            return;
        }

        var c = Cursor.Position;
        var k = _rumourScreen.Height > 0 ? _rumourScreen.Height / 1080f : 1f;
        if (showing)
        {
            if (Distance(c, _rumourShownCursor) > 40 * k)
            {
                HideOverlay();
            }
            else if (Native.GetAsyncKeyState(0x11) < 0 != _rumourDetails)
            {
                _rumourDetails = !_rumourDetails;
                ShowRumours();
            }
        }
        if (Distance(c, _rumourCursor) > 6)
        {
            _rumourCursor = c;
            _rumourStillSince = DateTime.UtcNow;
            _rumourTries = 0;
            return;
        }
        if (showing) return;

        // One look when the cursor stops, one more a bit later in case the tooltip came up slowly.
        var still = (DateTime.UtcNow - _rumourStillSince).TotalMilliseconds;
        if (!(_rumourTries == 0 && still >= 200 || _rumourTries == 1 && still >= 900)) return;
        _rumourTries++;
        _rumourBusy = true;
        try
        {
            await ReadRumoursAsync(hwnd, c);
        }
        catch (Exception ex)
        {
            Log.Write("rumours: " + ex.Message);
        }
        finally
        {
            _rumourBusy = false;
        }
    }

    static double Distance(Point a, Point b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    async Task ReadRumoursAsync(IntPtr hwnd, Point cursor)
    {
        if (GameWindow.ClientRect(hwnd) is not { } client) return;
        var k = client.Height / 1080f;
        // The tooltip sits around the hovered area, mostly above it.
        var region = Rectangle.FromLTRB((int)(cursor.X - 460 * k), (int)(cursor.Y - 720 * k), (int)(cursor.X + 460 * k), (int)(cursor.Y + 280 * k));
        region.Intersect(client);
        if (region.Width < 120 || region.Height < 120) return;

        using var capture = GameWindow.Capture(region);
        var segments = await _ocr!.RecognizeAsync(capture, Math.Clamp(2.0 / k, 1.2, 2.5), region.Location);
        var tooltip = RumourTooltip.Parse(segments, k);
        if (tooltip != null) await RumourOcr.RereadAsync(_ocr, tooltip, capture, region, k);
        if (Distance(Cursor.Position, cursor) > 40 * k || GameWindow.ForegroundPoe() == IntPtr.Zero) return;
        if (_overlay.Visible && _kind != OverlayKind.Rumours) return;

        _rumourText = segments.Select(sg => sg.Bounds).ToList();
        _rumourScreen = client;
        _rumourShownCursor = cursor;
        if (tooltip == null)
        {
            // A charted island with a chase drop (Moor, Jade Isles...): what to do there.
            if (RumourBook.FindIsland(segments) is not { } island) return;
            if (_rumourIsland != island.Rumour)
            {
                Log.Write($"rumours: island {island.Rumour.Map} ({island.Rumour.ShortText}), area {_clientLog.Area ?? "?"}");
                if (_s.SaveDebug) DebugDump.SaveRaw(capture, "rumours");
            }
            _rumourIsland = island.Rumour;
            _rumourTooltip = RectangleF.Inflate(island.Bounds, 40 * k, 10 * k);
            ShowRumours();
            return;
        }
        _rumourIsland = null;

        var same = _rumourArea != null && DateTime.UtcNow - _rumourArea.LastSeenUtc < TimeSpan.FromMinutes(15) &&
                   Math.Abs(_rumourArea.Where.X - tooltip.Where.X) < 40 * k && Math.Abs(_rumourArea.Where.Y - tooltip.Where.Y) < 40 * k;
        if (!same) _rumourArea = new RumourArea { Where = tooltip.Where };
        if (_rumourArea!.Add(tooltip))
        {
            Log.Write($"rumours: [{string.Join(" | ", tooltip.Rumours.Select(r => r.ShortText).Concat(tooltip.Unknown.Select(u => "?" + u)))}] " +
                      $"seen {_rumourArea.Seen.Count} in {_rumourArea.Looks} look(s), {_rumourArea.GrandCount} GE, area {_clientLog.Area ?? "?"}");
            if (_s.SaveDebug) DebugDump.SaveRaw(capture, "rumours");
        }

        _rumourTooltip = tooltip.Bounds;
        _rumourDetails = Native.GetAsyncKeyState(0x11) < 0;
        ShowRumours();
    }

    void ShowRumours()
    {
        var k = _rumourScreen.Height / 1080f;
        var label = _rumourIsland != null ? RumourLabels.Island(_rumourIsland, _rumourTooltip, 21 * k, _matcher, _s.ScanHotkey)
            : _rumourArea != null ? RumourLabels.Build(_rumourArea, _rumourTooltip, (_rumourDetails ? 18 : 21) * k, _rumourDetails, _matcher, _s.ScanHotkey)
            : null;
        if (label == null) return;
        ShowOverlay(_rumourScreen, new[] { label }, OverlayKind.Rumours, 0, _rumourText);
    }

    void ToggleRumours()
    {
        _s.ExpeditionRumours = !_s.ExpeditionRumours;
        _s.Save();
        if (!_s.ExpeditionRumours && _kind == OverlayKind.Rumours) HideOverlay();
        Balloon(_s.ExpeditionRumours ? "Expedition rumours ON" : "Expedition rumours OFF", ToolTipIcon.Info);
    }

    // ---------------------------------------------------------------- overlay

    void ShowOverlay(Rectangle area, IReadOnlyList<OverlayLabel> labels, OverlayKind kind, int seconds,
        IReadOnlyList<RectangleF>? obstacles = null, IReadOnlyList<OverlayBar>? bars = null, OverlayPanel? panel = null)
    {
        _overlay.ShowLabels(area, labels, obstacles, bars, panel);
        // Clicking a reward means it was picked: the bars go away. Messages just time out.
        if (kind is OverlayKind.Scan or OverlayKind.Auto or OverlayKind.Item or OverlayKind.Rumours or OverlayKind.Report)
            _clickWatcher ??= new ClickWatcher(() => _overlay.BeginInvoke(HideOverlay));
        else
            StopClickWatcher();
        _kind = kind;
        _hideAt = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : DateTime.MaxValue;
        _shownForeground = Native.GetForegroundWindow();
    }

    void ShowMessage(string title, string? detail, Rectangle? area = null, int seconds = 4)
    {
        var a = area ?? ScanArea();
        var anchor = new RectangleF(a.Left + a.Width / 2f - 150, a.Top + a.Height * 0.12f, 1, 24);
        ShowOverlay(a, new[] { LabelBuilder.Message(anchor, title, detail) }, OverlayKind.Message, seconds);
    }

    void ShowMessageAt(Point cursor, string title, string? detail)
    {
        var anchor = new RectangleF(cursor.X + 12, cursor.Y - 12, 8, 24);
        ShowOverlay(Screen.FromPoint(cursor).Bounds, new[] { LabelBuilder.Message(anchor, title, detail) }, OverlayKind.Message, 3);
    }

    void StopClickWatcher()
    {
        _clickWatcher?.Dispose();
        _clickWatcher = null;
    }

    void HideOverlay()
    {
        StopClickWatcher();
        if (_overlay.Visible) _overlay.Hide();
        _kind = OverlayKind.None;
        _hideAt = DateTime.MaxValue;
    }

    void Watchdog()
    {
        if (!_overlay.Visible) return;
        if (DateTime.UtcNow >= _hideAt) { HideOverlay(); return; }

        // Hide when the user switches away from the window the labels were drawn for.
        var foreground = Native.GetForegroundWindow();
        if (foreground != _shownForeground && !GameWindow.IsPoe(foreground)) { HideOverlay(); return; }

        if (_kind == OverlayKind.Item)
        {
            var c = Cursor.Position;
            var dx = c.X - _itemCursor.X;
            var dy = c.Y - _itemCursor.Y;
            if (dx * dx + dy * dy > 220 * 220) HideOverlay();
        }
    }

    // ---------------------------------------------------------------- auto-scan

    void ApplyAutoScan()
    {
        _autoTimer.Interval = Math.Clamp(_s.AutoScanIntervalMs, 300, 20000);
        _autoTimer.Enabled = _s.AutoScan;
        if (!_s.AutoScan && _kind == OverlayKind.Auto) HideOverlay();
    }

    void ToggleAutoScan()
    {
        _s.AutoScan = !_s.AutoScan;
        _s.Save();
        ApplyAutoScan();
        if (GameWindow.ForegroundPoe() != IntPtr.Zero)
            ShowMessage(_s.AutoScan ? "Auto-scan ON" : "Auto-scan OFF", null);
        else
            Balloon(_s.AutoScan ? "Auto-scan ON" : "Auto-scan OFF", ToolTipIcon.Info);
    }

    // ---------------------------------------------------------------- tray

    void BuildMenu()
    {
        var menu = _tray.ContextMenuStrip!;
        menu.Items.Clear();

        var status = _db != null ? $"{_db.Entries.Count} prices, {Fmt.Age(_db.UpdatedUtc)}" : _loading ? "loading prices…" : "no prices";
        menu.Items.Add(new ToolStripMenuItem($"{(CurrentLeague == "" ? "no league" : CurrentLeague)}  ·  {status}") { Enabled = false });
        if (_ocr == null) menu.Items.Add(new ToolStripMenuItem("OCR unavailable – see log") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add($"Scan screen in 2 s   (in game: {_s.ScanHotkey})", null, async (_, _) =>
        {
            await Task.Delay(2000);
            await ScanAsync(manual: true);
        });

        var auto = new ToolStripMenuItem($"Auto-scan   ({_s.AutoScanHotkey})") { Checked = _s.AutoScan };
        auto.Click += (_, _) => ToggleAutoScan();
        menu.Items.Add(auto);

        var leagues = new ToolStripMenuItem("League");
        var names = _leagues.Count > 0 ? _leagues : new List<string> { CurrentLeague };
        foreach (var name in names.Where(n => n != ""))
        {
            var item = new ToolStripMenuItem(name) { Checked = name == CurrentLeague };
            item.Click += async (_, _) => await SetLeagueAsync(name);
            leagues.DropDownItems.Add(item);
        }
        menu.Items.Add(leagues);

        if (_session.Running)
        {
            var h = _session.Played.TotalHours;
            menu.Items.Add(new ToolStripMenuItem($"Session: {Fmt.Duration(_session.Played)} played{(_session.Paused ? " (paused)" : "")}  \u00b7  {_session.Runs} run(s)  \u00b7  " +
                $"{Fmt.Range(_session.ProfitMin, _session.ProfitMax, signed: true)} div" +
                (h > 0.01 ? $"  \u00b7  \u2248 {Fmt.Range(_session.ProfitMin / h, _session.ProfitMax / h, signed: true)} div/h" : "")) { Enabled = false });
            menu.Items.Add($"Stop farming session   ({_s.SessionHotkey})", null, (_, _) => ToggleSession());
        }
        else
        {
            menu.Items.Add($"Start farming session   ({_s.SessionHotkey})", null, (_, _) => ToggleSession());
        }
        menu.Items.Add($"Run report   ({_s.InventoryWindowHotkey})", null, (_, _) => ToggleInventoryWindow());
        menu.Items.Add($"Stash value window   (scan a tab: {_s.StashHotkey})", null, (_, _) => ToggleStashWindow());
        var runs = new ToolStripMenuItem("Runs");
        runs.DropDownItems.Add(new ToolStripMenuItem(_runs.Start != null
            ? $"run in progress since {_runs.Start.Time:HH:mm}"
            : $"start: {_s.RunStartHotkey}   finish: {_s.RunEndHotkey}  (inventory open)") { Enabled = false });
        runs.DropDownItems.Add(new ToolStripMenuItem($"{_runs.SessionRuns} run(s) this session") { Enabled = false });
        if (_runs.LastReport is { } last && _db is { } db)
            runs.DropDownItems.Add($"Save last run (#{last.Number}, {Fmt.Range(last.ProfitMin, last.ProfitMax, signed: true)} div)", null, (_, _) =>
            {
                RunStore.Save(SavedRun.From(last, db));
                Balloon($"Run #{last.Number} saved", ToolTipIcon.Info);
            });
        runs.DropDownItems.Add("Run history", null, (_, _) =>
        {
            var form = InventoryWindow();
            form.ShowHistory();
            form.Show();
        });
        runs.DropDownItems.Add("Open runs.csv", null, (_, _) =>
        {
            if (File.Exists(RunStore.CsvPath)) Process.Start(new ProcessStartInfo(RunStore.CsvPath) { UseShellExecute = true });
            else Balloon("No saved runs yet.", ToolTipIcon.Info);
        });
        runs.DropDownItems.Add("Reset session totals", null, (_, _) => _runs.ResetSession());
        menu.Items.Add(runs);

        var expedition = new ToolStripMenuItem("Expedition rumours");
        var rumoursOn = new ToolStripMenuItem("Rate rumours when hovering uncharted waters") { Checked = _s.ExpeditionRumours };
        rumoursOn.Click += (_, _) => ToggleRumours();
        expedition.DropDownItems.Add(rumoursOn);
        expedition.DropDownItems.Add(new ToolStripMenuItem("ratings: " + RumourBook.Source) { Enabled = false });
        expedition.DropDownItems.Add($"Special maps   ({_s.InventoryWindowHotkey} \u2192 Special maps)", null, (_, _) =>
        {
            var form = InventoryWindow();
            form.ShowSpecialMaps();
            form.Show();
        });
        expedition.DropDownItems.Add(new ToolStripSeparator());
        foreach (var r in RumourBook.All.OrderByDescending(r => r.Points))
            expedition.DropDownItems.Add(new ToolStripMenuItem($"{r.Rating}   {r.ShortText} \u2192 {r.Map} ({r.KindText}): {r.Mods}") { Enabled = false });
        expedition.DropDownItems.Add(new ToolStripSeparator());
        if (_s.RumourSheet.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            expedition.DropDownItems.Add("Open the rating sheet", null, (_, _) => Process.Start(new ProcessStartInfo(_s.RumourSheet) { UseShellExecute = true }));
        expedition.DropDownItems.Add("Reload ratings now", null, async (_, _) =>
        {
            await RumourBook.RefreshAsync(_s.RumourSheet, force: true);
            Balloon("Rumour ratings: " + RumourBook.Source, ToolTipIcon.Info);
        });
        menu.Items.Add(expedition);

        menu.Items.Add($"Screenshots with overlay   ({_s.ScreenshotHotkey})", null, (_, _) =>
        {
            Directory.CreateDirectory(ScreenshotDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ScreenshotDir}\"") { UseShellExecute = true });
        });
        menu.Items.Add("Refresh prices now", null, async (_, _) => await LoadPricesAsync(force: true));
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        menu.Items.Add("Open data folder (logs, debug)", null, (_, _) =>
        {
            Directory.CreateDirectory(AppSettings.Dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppSettings.Dir}\"") { UseShellExecute = true });
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
    }

    void OpenSettings()
    {
        if (_settingsForm != null)
        {
            _settingsForm.Activate();
            return;
        }

        using var form = new SettingsForm(_s, _leagues);
        _settingsForm = form;
        try
        {
            if (form.ShowDialog() != DialogResult.OK) return;
        }
        finally
        {
            _settingsForm = null;
        }

        var old = _s;
        _s = form.Result;
        _s.Save();
        ApplyHotkeys();
        ApplyAutoScan();
        if (!_s.ExpeditionRumours && _kind == OverlayKind.Rumours) HideOverlay();
        if (old.RumourSheet != _s.RumourSheet) _ = RumourBook.RefreshAsync(_s.RumourSheet, force: true);
        if (old.League != _s.League || old.IncludeUniques != _s.IncludeUniques)
        {
            _db = null;
            _matcher = null;
            HideOverlay();
            _ = LoadPricesAsync(force: old.League == _s.League);
        }
    }

    void Balloon(string text, ToolTipIcon icon) => _tray.ShowBalloonTip(4000, "poe2verlay", text, icon);

    static Icon LoadIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && Icon.ExtractAssociatedIcon(exe) is { } icon) return icon;
        }
        catch
        {
            // fall back to the stock icon
        }
        return SystemIcons.Application;
    }

    protected override void ExitThreadCore()
    {
        _hook.Dispose();
        StopClickWatcher();
        _inventoryForm?.Dispose();
        _stashForm?.Dispose();
        _watchdog.Stop();
        _autoTimer.Stop();
        _refreshTimer.Stop();
        _verifyTimer.Stop();
        _rumourTimer.Stop();
        _sessionTimer.Stop();
        _session.Save();
        _tray.Visible = false;
        _tray.Dispose();
        _overlay.Close();
        base.ExitThreadCore();
    }
}
