using System.Diagnostics;
using System.Drawing.Text;
using System.Globalization;

namespace Poe2Verlay;

/// <summary>
/// Headless checks, results go to files:
///   --selftest   OUTDIR [league]         synthetic reward panel
///   --scan-image IMAGE OUTDIR [league]   any screenshot
///   --scan-game  OUTDIR [league]         capture the Path of Exile 2 window right now
///   --rumours    OUTDIR [IMAGE]          expedition rumour sheet, matching, labels (IMAGE = tooltip crop)
/// </summary>
internal static class SelfTest
{
    public static int Run(string[] args) => Task.Run(() => RunAsync(args)).GetAwaiter().GetResult();

    static async Task<int> RunAsync(string[] args)
    {
        var mode = args[0];
        var rest = args.Skip(1).ToList();
        string? image = null;
        if (mode == "--scan-image" || (mode is "--inv-grid" or "--stash-grid" && rest.Count >= 2))
        {
            if (rest.Count < 2) return 2;
            image = rest[0];
            rest.RemoveAt(0);
        }
        if (rest.Count < 1) return 2;

        var outDir = rest[0];
        Directory.CreateDirectory(outDir);
        using var log = new StreamWriter(Path.Combine(outDir, "selftest.txt")) { AutoFlush = true };
        if (mode == "--smoke") return Smoke(outDir, log);
        if (mode == "--stash-names") return StashNames(log, rest.Skip(1).ToArray());
        if (mode == "--rumours") return await RumourTest(outDir, log, rest.Count > 1 ? rest[1] : null);
        if (mode == "--rumour-ocr") return await RumourOcrTest(outDir, log, rest.Skip(1).ToList());

        try
        {
            var ocr = OcrScanner.TryCreate(out var ocrError);
            log.WriteLine(ocr == null ? "OCR: FAILED " + ocrError : "OCR: ok, language " + ocr.Language);
            if (ocr == null) return 1;

            var service = new PriceService();
            var leagues = await service.GetLeaguesAsync();
            log.WriteLine("leagues: " + string.Join(" | ", leagues));
            var league = rest.Count > 1 ? rest[1] : AppSettings.Load().League is { Length: > 0 } l ? l : leagues[0];

            var settings = AppSettings.Load();
            var sw = Stopwatch.StartNew();
            var db = PriceCache.Load(league);
            if (db == null || (DateTime.UtcNow - db.UpdatedUtc).TotalMinutes > settings.PriceRefreshMinutes)
            {
                db = await service.FetchAsync(league, settings.IncludeUniques);
                PriceCache.Save(db);
            }
            var matcher = new ItemMatcher(db);
            log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"prices: {league}, {db.Entries.Count} entries, {matcher.Count} names, 1 div = {db.ChaosPerDivine:0.##} c = {db.ExaltedPerDivine:0.#} ex ({sw.ElapsedMilliseconds} ms)"));
            foreach (var name in new[] { "Divine Orb", "Exalted Orb", "Liquid Verisium", "Celestial Alloy", "Uhtred's Saga" })
            {
                var e = matcher.Lookup(name);
                log.WriteLine(e == null ? $"  {name}: MISSING" : string.Create(CultureInfo.InvariantCulture,
                    $"  {name}: {e[0].Divine:0.####} div, vol {e[0].Volume:0.##}"));
            }

            if (mode is "--inv-grid" or "--stash-grid") return await GridTest(mode == "--stash-grid", outDir, image, ocr, settings, log);

            Bitmap bmp;
            Rectangle region;
            if (mode == "--selftest")
            {
                bmp = SyntheticPanel();
                region = new Rectangle(0, 0, bmp.Width, bmp.Height);
            }
            else if (mode == "--scan-image")
            {
                using (var loaded = new Bitmap(image!)) bmp = new Bitmap(loaded);
                region = new Rectangle(0, 0, bmp.Width, bmp.Height);
            }
            else
            {
                var hwnd = GameWindow.FindPoe();
                if (GameWindow.ClientRect(hwnd) is not { } client)
                {
                    log.WriteLine("game window not found / minimized");
                    return 1;
                }
                region = client;
                bmp = GameWindow.Capture(region);
            }

            using (bmp)
            {
                var scale = settings.OcrScale > 0 ? settings.OcrScale : OcrScanner.AutoScale(region.Height, 2160);
                sw.Restart();
                var segments = await ocr.RecognizeAsync(bmp, scale, region.Location);
                var ocrMs = sw.ElapsedMilliseconds;
                var detections = ItemMatcher.PanelRows(matcher.Find(segments));
                var bars = LabelBuilder.BarsForDetections(detections, db, settings);
                var info = string.Create(CultureInfo.InvariantCulture,
                    $"{mode} scale={scale:0.##} ocr={ocrMs}ms total={sw.ElapsedMilliseconds}ms segments={segments.Count} matches={detections.Count} tilt={ocr.LastTextAngle:0.##}");
                log.WriteLine(info);
                foreach (var d in detections)
                    log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"  \"{d.OcrText}\" -> {d.Entry.Name} x{d.Quantity} (dist {d.Distance}) = {Fmt.Price(d.TotalDivine, db, settings)}"));
                DebugDump.Write(Path.Combine(outDir, "scan"), bmp, region, segments, detections, bars, info);
                // What the player sees: just the bars, without the OCR boxes of the debug copy.
                using (var clean = new Bitmap(bmp))
                {
                    using (var g = Graphics.FromImage(clean))
                        OverlayRenderer.DrawBars(g, region.Location, region.Size, bars, segments.Select(sg => sg.Bounds).ToList());
                    clean.Save(Path.Combine(outDir, "scan-clean.png"));
                }
            }
            log.WriteLine("done");
            return 0;
        }
        catch (Exception ex)
        {
            log.WriteLine("ERROR " + ex);
            return 1;
        }
    }

    /// <summary>Draw where the inventory / stash grid is assumed to be and which slots look occupied. Never moves the mouse.</summary>
    static async Task<int> GridTest(bool stash, string outDir, string? image, OcrScanner ocr, AppSettings settings, StreamWriter log)
    {
        Bitmap bmp;
        Rectangle client;
        if (image != null)
        {
            using (var loaded = new Bitmap(image)) bmp = new Bitmap(loaded);
            client = new Rectangle(0, 0, bmp.Width, bmp.Height);
        }
        else
        {
            if (GameWindow.ClientRect(GameWindow.FindPoe()) is not { } c)
            {
                log.WriteLine("game window not found / minimized");
                return 1;
            }
            client = c;
            bmp = GameWindow.Capture(client);
        }

        using (bmp)
        {
            var g = GridGeometry.Inventory(client, settings);
            if (stash)
            {
                var k = client.Height / 1080f;
                var g12 = GridGeometry.Stash(client, settings, 12);
                var titleArea = Rectangle.FromLTRB(client.Left, client.Top, (int)(g12.Bounds.Right + 40 * k), (int)g12.Top);
                titleArea.Offset(-client.X, -client.Y);
                titleArea.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
                List<TextSegment> titles;
                using (var titleBmp = bmp.Clone(titleArea, bmp.PixelFormat))
                    titles = await ocr.RecognizeAsync(titleBmp, 2.0, new Point(titleArea.X + client.X, titleArea.Y + client.Y));
                var layout = StashDetect.Layout(bmp, client, g12.Bounds, out var detail);
                log.WriteLine($"stash word: {InventoryScanner.HasWord(titles, "stash")}  layout: {layout} ({detail})");
                foreach (var t in titles) log.WriteLine($"  title text: {t.Text} @ {Rectangle.Round(t.Bounds)}");
                g = layout == StashLayout.Normal ? g12 : GridGeometry.Stash(client, settings, 24);
            }
            log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"client {client}, grid left={g.Left:0.#} top={g.Top:0.#} cell={g.Cell:0.##} {g.Cols}x{g.Rows}"));
            if (image == null && !stash) log.WriteLine("inventory open (OCR): " + await InventoryScanner.InventoryOpenAsync(ocr, client, g));
            var occupied = InventoryScanner.Occupancy(bmp, client, g, out var scores);
            using (var gr = Graphics.FromImage(bmp))
            using (var font = new Font("Segoe UI", 10, GraphicsUnit.Pixel))
            {
                for (var r = 0; r < g.Rows; r++)
                {
                    var line = new System.Text.StringBuilder();
                    for (var c = 0; c < g.Cols; c++)
                    {
                        var cell = g.CellRect(r, c);
                        cell.Offset(-client.X, -client.Y);
                        using var pen = new Pen(occupied[r, c] ? Color.Lime : Color.Red, 1);
                        gr.DrawRectangle(pen, cell.X, cell.Y, cell.Width, cell.Height);
                        gr.DrawString(scores[r, c].ToString("0.00", CultureInfo.InvariantCulture), font, Brushes.Yellow, cell.X + 2, cell.Bottom - 13);
                        line.Append(occupied[r, c] ? '#' : '.');
                        line.Append(string.Create(CultureInfo.InvariantCulture, $"{scores[r, c],5:0.00} "));
                    }
                    log.WriteLine(line.ToString());
                }
            }
            var crop = Rectangle.Round(g.Bounds);
            crop.Offset(-client.X, -client.Y);
            crop.Inflate(40, 40);
            crop.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
            using var cropped = bmp.Clone(crop, bmp.PixelFormat);
            cropped.Save(Path.Combine(outDir, stash ? "stash.png" : "inventory.png"));
        }
        log.WriteLine("done");
        return 0;
    }

    /// <summary>Which name each saved stash tab would get now (read-only; labels as OCR'd above the grid).</summary>
    static int StashNames(StreamWriter log, string[] labels)
    {
        var settings = AppSettings.Load();
        if (PriceCache.Load(settings.League) is not { } db)
        {
            log.WriteLine("no cached prices");
            return 1;
        }
        var matcher = new ItemMatcher(db);
        foreach (var tab in StashStore.Load())
        {
            var value = Valuation.Value(tab.Snapshot, matcher, settings);
            log.WriteLine($"{tab.Name} -> {StashStore.PickName(value, labels)} (no labels: {StashStore.PickName(value, Array.Empty<string>())})");
        }
        return 0;
    }

    /// <summary>
    /// Rating sheet and rumour matching; with IMAGE (a crop of the "Uncharted Waters" tooltip) also the OCR read.
    /// Draws the compact and the Ctrl label onto a fake 1080p screen.
    /// </summary>
    static async Task<int> RumourTest(string outDir, StreamWriter log, string? image)
    {
        var settings = AppSettings.Load();
        await RumourBook.RefreshAsync(settings.RumourSheet, force: true);
        log.WriteLine("ratings: " + RumourBook.Source);
        foreach (var r in RumourBook.All.OrderByDescending(r => r.Points))
            log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {r.Rating,-4} {r.Points,4:0.#}  {r.Text,-24} {r.Map,-20} {r.KindText,-10} {r.Mods}"));
        foreach (var probe in new[]
                 {
                     "Somethin' fishy...", "Something Fishy", "Reflective waters...", "Warm but risky...", "It's Warm", "Sulphite!",
                     "Wild,.Roaming Free", "Nothin to drink", "Cold as ice..", "Fallen stars...", "Stardrinker...", "The last to fall...",
                     "Use a logbook to chart the area", "Expedition Logbook", "Consumes:", "Island Rumours",
                 })
            log.WriteLine($"  find \"{probe}\" -> {RumourBook.Find(probe)?.Text ?? "-"}");

        var db = PriceCache.Load(settings.League);
        var matcher = db == null ? null : new ItemMatcher(db);
        RumourBook.AldursSagaDrops = settings.League.Contains("Aldur", StringComparison.OrdinalIgnoreCase);
        using var screen = new Bitmap(1920, 1080);
        using (var g = Graphics.FromImage(screen)) g.Clear(Color.FromArgb(28, 38, 48));

        var area = new RumourArea();
        var tooltip = new RectangleF(840, 300, 300, 330);
        var text = new List<RectangleF>();
        if (image != null)
        {
            var ocr = OcrScanner.TryCreate(out var error);
            if (ocr == null)
            {
                log.WriteLine("OCR: " + error);
                return 1;
            }
            Rectangle region;
            using (var src = new Bitmap(image))
            using (var g = Graphics.FromImage(screen))
            {
                if (src.Width < 400)
                {
                    // A small crop from a guide: put it back at roughly its 1080p size.
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    var dest = new Rectangle(820, 260, (int)(src.Width * 1.3), (int)(src.Height * 1.3));
                    g.DrawImage(src, dest);
                    var cursor = new Point(dest.Left + dest.Width / 2, dest.Bottom - 20);
                    region = Rectangle.FromLTRB(cursor.X - 460, cursor.Y - 720, cursor.X + 460, cursor.Y + 280);
                }
                else
                {
                    // debug\rumours-*.png: the region read in game, drawn where it was (roughly centred).
                    var at = new Point((1920 - src.Width) / 2, Math.Max(0, (1080 - src.Height) / 2));
                    g.DrawImageUnscaled(src, at);
                    region = new Rectangle(at, src.Size);
                }
            }
            region.Intersect(new Rectangle(Point.Empty, screen.Size));
            using var crop = screen.Clone(region, screen.PixelFormat);
            RumourTooltip? t = null;
            List<TextSegment> segments = new();
            foreach (var scale in new[] { 1.6, 2.0, 2.5, 3.0 })
            {
                var sw = Stopwatch.StartNew();
                var read = await ocr.RecognizeAsync(crop, scale, region.Location);
                var parsed = RumourTooltip.Parse(read, 1f);
                log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"ocr x{scale}: {sw.ElapsedMilliseconds} ms, {read.Count} segments, region {region}"));
                foreach (var sg in read) log.WriteLine($"  {sg.Bounds} {sg.Text}");
                log.WriteLine(parsed == null ? "  -> tooltip NOT found"
                    : $"  -> {parsed.LineCount} lines, rumours [{string.Join(", ", parsed.Rumours.Select(r => r.ShortText))}], unknown [{string.Join(", ", parsed.Unknown)}]");
                if (parsed != null && (t == null || parsed.Rumours.Count > t.Rumours.Count))
                {
                    t = parsed;
                    segments = read;
                }
            }
            if (t == null) return 1;
            log.WriteLine($"tooltip: {t.LineCount} lines, rumours [{string.Join(", ", t.Rumours.Select(r => r.ShortText))}], unknown [{string.Join(", ", t.Unknown)}], bounds {t.Bounds}");
            area.Add(t);
            tooltip = t.Bounds;
            text = segments.Select(sg => sg.Bounds).ToList();
        }
        else
        {
            area.Add(RumourTooltip.Of("Reflective waters...", "Warm but risky...", "Somethin' fishy..."));
        }

        void Draw(string name, bool details)
        {
            using var bmp = new Bitmap(screen);
            using (var g = Graphics.FromImage(bmp))
                OverlayRenderer.Draw(g, Point.Empty, bmp.Size, new[] { RumourLabels.Build(area, tooltip, 21, details, matcher, settings.ScanHotkey) }, text);
            bmp.Save(Path.Combine(outDir, name));
            log.WriteLine($"{name}: {RumourLabels.Verdict(area).Text}, {area.Seen.Count} seen, {area.GrandCount} GE");
        }
        Draw("rumours-1.png", false);
        Draw("rumours-1-ctrl.png", true);

        // Second look after moving an inventory item: two more islands show up.
        area.Add(RumourTooltip.Of("Fallen stars...", "Cold as ice...", "Stardrinker..."));
        Draw("rumours-2.png", false);
        Draw("rumours-2-ctrl.png", true);
        return 0;
    }

    /// <summary>Tuning: on in-game tooltip captures, read just the rumour box with different clean-ups and sizes.</summary>
    static async Task<int> RumourOcrTest(string outDir, StreamWriter log, List<string> images)
    {
        await RumourBook.RefreshAsync(AppSettings.Load().RumourSheet);
        var ocr = OcrScanner.TryCreate(out var error);
        if (ocr == null)
        {
            log.WriteLine("OCR: " + error);
            return 1;
        }
        foreach (var image in images.SelectMany(i => Directory.Exists(i) ? Directory.GetFiles(i, "*.png").Order().ToArray() : new[] { i }))
        {
            log.WriteLine("=== " + image);
            using var src = new Bitmap(image);
            var segments = await ocr.RecognizeAsync(src, 2.0, Point.Empty);
            // What the app does: read the area, then the rumour lines again.
            var sw = Stopwatch.StartNew();
            if (RumourTooltip.Parse(segments, 1f) is { } app)
            {
                await RumourOcr.RereadAsync(ocr, app, src, new Rectangle(Point.Empty, src.Size), 1f);
                log.WriteLine($"app: {app.LineCount} lines, rumours [{string.Join(", ", app.Rumours.Select(r => r.ShortText))}], unknown [{string.Join(", ", app.Unknown)}] (+{sw.ElapsedMilliseconds} ms)");
            }
            else
            {
                log.WriteLine("app: no rumour tooltip" + (RumourBook.FindIsland(segments) is { } island ? $", island {island.Rumour.Map}" : ""));
            }
            var header = segments.FirstOrDefault(sg => sg.Text.Contains("RUMOUR", StringComparison.OrdinalIgnoreCase));
            var end = segments.FirstOrDefault(sg => header != null && sg.Bounds.Top > header.Bounds.Bottom &&
                (sg.Text.Contains("REQUIRES", StringComparison.OrdinalIgnoreCase) || sg.Text.Contains("CONSUMES", StringComparison.OrdinalIgnoreCase)));
            if (header == null || end == null)
            {
                log.WriteLine("header/end not found");
                continue;
            }
            var cx = header.Bounds.Left + header.Bounds.Width / 2;
            var box = Rectangle.FromLTRB((int)(cx - 135), (int)header.Bounds.Bottom + 4, (int)(cx + 135), (int)end.Bounds.Top - 4);
            box.Intersect(new Rectangle(Point.Empty, src.Size));
            log.WriteLine($"box {box}");
            using var crop = src.Clone(box, src.PixelFormat);
            crop.Save(Path.Combine(outDir, Path.GetFileNameWithoutExtension(image) + "-box.png"));
            foreach (var (name, clean) in new (string, int)[] { ("raw", 0), ("gray", 1), ("bin", 2) })
            {
                using var img = clean == 0 ? new Bitmap(crop) : RumourOcr.Clean(crop, clean == 2);
                if (clean == 2) img.Save(Path.Combine(outDir, Path.GetFileNameWithoutExtension(image) + "-bin.png"));
                foreach (var scale in new[] { 2.0, 3.0, 4.0, 5.0 })
                {
                    var read = await ocr.RecognizeAsync(img, scale, box.Location);
                    var lines = read.Select(sg => $"{sg.Text} => {RumourBook.Find(sg.Text)?.ShortText ?? "?"}");
                    log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {name} x{scale}: {string.Join("  |  ", lines)}"));
                }
            }
        }
        return 0;
    }

    /// <summary>Fill the inventory window with a made-up run and save each tab as an image.</summary>
    static void RunDemo(string outDir, ItemMatcher matcher, PriceDb db, AppSettings settings, StreamWriter log)
    {
        InventoryItem It(string name, int stack, string cls = "Stackable Currency", string rarity = "Currency", string? baseType = null) =>
            new() { Name = name, Stack = stack, ItemClass = cls, Rarity = rarity, BaseType = baseType };
        var start = new InventorySnapshot
        {
            Time = DateTime.Now.AddMinutes(-7.7),
            Items =
            {
                It("Exalted Orb", 20), It("Vaal Orb", 3), It("Liquid Verisium", 15), It("Verisium", 477),
                It("Dream Stone", 1, "Waystones", "Rare", "Waystone (Tier 15)"), It("Thaumaturgic Flux", 1, "Stackable Currency", "Currency"),
            },
        };
        var end = new InventorySnapshot
        {
            Time = DateTime.Now,
            Items =
            {
                It("Exalted Orb", 20), It("Exalted Orb", 14), It("Vaal Orb", 2), It("Divine Orb", 1), It("Liquid Verisium", 22),
                It("Verisium", 640), It("Celestial Alloy", 1), It("Chaos Orb", 6), It("Lazuli Ring", 1, "Rings", "Unique"),
                It("Grim Bangle", 1, "Rings", "Rare", "Gold Ring"),
            },
        };
        var tracker = new RunTracker(persist: false);
        tracker.Begin(start);
        var report = tracker.Finish(end, matcher, settings);
        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"run demo: profit {report.ProfitMin:0.###} .. {report.ProfitMax:0.###} div, {report.Lines.Count} lines"));

        // Farming session start / stop panel, on a fake 1080p screen.
        var screen = new Rectangle(0, 0, 1920, 1080);
        var session = new FarmSession(persist: false);
        void Panel(string name, OverlayPanel panel)
        {
            using var bmp = new Bitmap(screen.Width, screen.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(40, 44, 40));
                OverlayRenderer.DrawPanel(g, Point.Empty, bmp.Size, panel);
            }
            bmp.Save(Path.Combine(outDir, name));
        }
        session.Start();
        session.RunFinished(report);
        Panel("overlay-session.png", ReportPanels.Session("Farming session stopped", session, db, settings, screen));

        using var form = new InventoryForm(settings) { StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, TopMost = false };
        form.Location = new Point(-10000, -10000);
        form.SetStart(report.Start, db);
        form.Show();
        Application.DoEvents();
        Snap(form, Path.Combine(outDir, "inventory-start.png"));
        form.SessionLine = () => "Session: +12.3 \u2013 15 div  \u00b7  \u2248 +10.2 \u2013 12.5 div/h  \u00b7  1h 12m played  \u00b7  4 run(s), 9 map(s)";
        form.SetCompact(false);
        form.SetReport(report, db, TimeSpan.FromMinutes(6.2));
        Application.DoEvents();
        Snap(form, Path.Combine(outDir, "inventory-diff.png"));
        form.SetCompact(true);
        Application.DoEvents();
        Snap(form, Path.Combine(outDir, "inventory-small.png"));
        form.SetCompact(false);
        form.PriceText = name => RumourLabels.PriceText(name, matcher);
        form.ShowSpecialMaps();
        Application.DoEvents();
        Snap(form, Path.Combine(outDir, "inventory-special.png"));
        form.ShowHistory(); // read-only view of whatever the user has saved
        Application.DoEvents();
        Snap(form, Path.Combine(outDir, "inventory-history.png"));
        form.Hide();

        // Stash window with two made-up tabs (in memory only, nothing is saved).
        var tabs = new List<StashTabRecord>
        {
            new() { Name = "Currency", Layout = StashLayout.Special, Snapshot = end },
            new() { Name = "Dump 1", Layout = StashLayout.Quad, Snapshot = start },
        };
        using var stash = new StashForm(settings) { StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, TopMost = false };
        stash.Location = new Point(-10000, -10000);
        stash.Show();
        stash.SetData(tabs, matcher, db, settings, null);
        Application.DoEvents();
        Snap(stash, Path.Combine(outDir, "stash-window.png"));
        stash.Hide();
    }

    static void Snap(Form form, string path)
    {
        using var bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
        bmp.Save(path);
    }

    /// <summary>Exercise the window/hook plumbing without putting anything visible on screen.</summary>
    static int Smoke(string outDir, StreamWriter log)
    {
        var ok = true;
        var thread = new Thread(() =>
        {
            try
            {
                using (var hook = new KeyboardHook()) log.WriteLine("keyboard hook: installed");

                using var overlay = new OverlayForm();
                overlay.EnsureHandle();
                var corner = Screen.PrimaryScreen!.Bounds;
                overlay.ShowLabels(new Rectangle(corner.Right - 64, corner.Bottom - 64, 64, 64), Array.Empty<OverlayLabel>());
                Application.DoEvents();
                log.WriteLine($"overlay: visible={overlay.Visible} bounds={overlay.Bounds}");
                overlay.Hide();

                var leagues = new List<string> { "Forbidden Rites", "Runes of Aldur", "Standard" };
                using var form = new SettingsForm(new AppSettings { League = "Forbidden Rites" }, leagues);
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-10000, -10000);
                form.TopMost = false;
                form.ShowInTaskbar = false;
                form.NoActivate = true;
                form.Show();
                Application.DoEvents();
                using var bmp = new Bitmap(Math.Max(1, form.Width), Math.Max(1, form.Height));
                form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
                form.Close();
                bmp.Save(Path.Combine(outDir, "settings.png"));
                log.WriteLine($"settings form: {form.Size}");

                var settings = AppSettings.Load();
                if (PriceCache.Load(string.IsNullOrEmpty(settings.League) ? "Standard" : settings.League) is { } db)
                    RunDemo(outDir, new ItemMatcher(db), db, settings, log);
                else
                    log.WriteLine("no cached prices, skipping inventory window demo");
            }
            catch (Exception ex)
            {
                ok = false;
                log.WriteLine("ERROR " + ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        log.WriteLine(ok ? "done" : "failed");
        return ok ? 0 : 1;
    }

    /// <summary>A fake reward panel with the kinds of text layouts the matcher has to cope with.</summary>
    static Bitmap SyntheticPanel()
    {
        var bmp = new Bitmap(1600, 900);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(18, 16, 14));
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var card = new SolidBrush(Color.FromArgb(34, 30, 26));
        using var pen = new Pen(Color.FromArgb(90, 75, 50), 2);
        using var title = new Font("Georgia", 26, FontStyle.Regular, GraphicsUnit.Pixel);
        using var font = new Font("Georgia", 19, FontStyle.Regular, GraphicsUnit.Pixel);
        using var gold = new SolidBrush(Color.FromArgb(200, 180, 130));
        using var white = new SolidBrush(Color.FromArgb(220, 220, 220));
        using var blue = new SolidBrush(Color.FromArgb(140, 150, 230));

        g.DrawString("Choose a Reward", title, gold, 640, 60);
        var cards = new (int X, int Y, string[] Lines)[]
        {
            (120, 160, new[] { "3x Exalted Orb" }),
            (520, 160, new[] { "Liquid Verisium x5" }),
            (920, 160, new[] { "Ancient Rune of Detonation" }),
            (120, 400, new[] { "Divine Orb" }),
            (520, 400, new[] { "Thaumaturgic Flux", "(Level 20)" }),
            (920, 400, new[] { "Celestial Alloy" }),
            (120, 640, new[] { "Monsters deal 20% more damage" }),
            (520, 640, new[] { "Uhtred's Saga" }),
            (920, 640, new[] { "Greater Exalted Orb (3)" }),
        };
        foreach (var (x, y, lines) in cards)
        {
            g.FillRectangle(card, x, y, 340, 160);
            g.DrawRectangle(pen, x, y, 340, 160);
            for (var i = 0; i < lines.Length; i++)
                g.DrawString(lines[i], font, i == 0 && lines[0].StartsWith("Monsters") ? blue : white, x + 24, y + 60 + i * 26);
        }
        // Two names on one row with a wide gap: OCR may return them as a single line.
        g.DrawString("Perfect Exalted Orb", font, white, 1300, 200);
        g.DrawString("Vaal Orb", font, white, 1300, 440);
        return bmp;
    }
}
