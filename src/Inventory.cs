using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Poe2Verlay;

public sealed class InventoryItem
{
    public string Name { get; set; } = "";
    public string? BaseType { get; set; }
    public string Rarity { get; set; } = "";
    public string ItemClass { get; set; } = "";
    public int Stack { get; set; } = 1;
    public int? Level { get; set; }
    public int Row { get; set; }
    public int Col { get; set; }

    /// <summary>Identity used to group and compare snapshots (rares/uniques: name + base).</summary>
    public string Key => Rarity is "Unique" or "Rare" or "Magic" && BaseType != null ? $"{Name} | {BaseType}" : Name;

    public ParsedItem ToParsed() => new()
    {
        Name = Name, BaseType = BaseType, Rarity = Rarity, ItemClass = ItemClass, Stack = Stack, Level = Level,
    };
}

public sealed class InventorySnapshot
{
    public DateTime Time { get; set; }
    public List<InventoryItem> Items { get; set; } = new();
    public int OccupiedCells { get; set; }
    public int UnreadCells { get; set; }
}

/// <summary>An item grid on screen (inventory, stash tab). Measured at 1080p, scales with the window height.</summary>
internal readonly record struct GridGeometry(float Left, float Top, float Cell, int Rows, int Cols)
{
    /// <summary>The 12x5 main inventory, anchored to the right edge of the window.</summary>
    public static GridGeometry Inventory(Rectangle client, AppSettings s)
    {
        var k = client.Height / 1080f;
        return new GridGeometry(
            client.Right - (float)s.InventoryRightOffset * k,
            client.Top + (float)s.InventoryTop * k,
            (float)s.InventoryCell * k, 5, 12);
    }

    /// <summary>A stash tab (anchored to the left edge) split into <paramref name="cells"/> x <paramref name="cells"/> slots.</summary>
    public static GridGeometry Stash(Rectangle client, AppSettings s, int cells)
    {
        var k = client.Height / 1080f;
        var size = (float)s.StashSize * k;
        return new GridGeometry(client.Left + (float)s.StashLeft * k, client.Top + (float)s.StashTop * k, size / cells, cells, cells);
    }

    public RectangleF CellRect(int row, int col) => new(Left + col * Cell, Top + row * Cell, Cell, Cell);

    public Point Center(int row, int col) =>
        new((int)(Left + (col + 0.5f) * Cell), (int)(Top + (row + 0.5f) * Cell));

    public RectangleF Bounds => new(Left, Top, Cols * Cell, Rows * Cell);
}

internal static class InventoryScanner
{
    /// <summary>
    /// Empty slots are near-black; an item icon always has some bright pixels. Only occupied slots get hovered,
    /// which keeps a scan of a half-empty inventory around a second or two.
    /// </summary>
    public static bool[,] Occupancy(Bitmap capture, Rectangle captureRect, GridGeometry g, out float[,] scores, float threshold = 0.03f)
    {
        var occupied = new bool[g.Rows, g.Cols];
        scores = new float[g.Rows, g.Cols];
        var data = capture.LockBits(new Rectangle(0, 0, capture.Width, capture.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[data.Stride];
            for (var r = 0; r < g.Rows; r++)
            for (var c = 0; c < g.Cols; c++)
            {
                var cell = g.CellRect(r, c);
                cell.Offset(-captureRect.X, -captureRect.Y);
                var inset = cell.Width * 0.18f;
                int x0 = (int)(cell.Left + inset), x1 = (int)(cell.Right - inset);
                int y0 = (int)(cell.Top + inset), y1 = (int)(cell.Bottom - inset);
                x0 = Math.Clamp(x0, 0, capture.Width - 1); x1 = Math.Clamp(x1, x0 + 1, capture.Width);
                y0 = Math.Clamp(y0, 0, capture.Height - 1); y1 = Math.Clamp(y1, y0 + 1, capture.Height);

                int bright = 0, total = 0;
                for (var y = y0; y < y1; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                    for (var x = x0; x < x1; x++)
                    {
                        var i = x * 4;
                        var lum = (row[i] * 11 + row[i + 1] * 59 + row[i + 2] * 30) / 100;
                        if (lum > 70) bright++;
                        total++;
                    }
                }
                var score = total == 0 ? 0 : (float)bright / total;
                scores[r, c] = score;
                occupied[r, c] = score > threshold;
            }
        }
        finally
        {
            capture.UnlockBits(data);
        }
        return occupied;
    }

    /// <summary>
    /// Hover each occupied slot and Ctrl+C it. Only the cursor moves and only item text is copied:
    /// no clicks, nothing that reaches the game server.
    /// </summary>
    /// <param name="park">Where to rest the cursor before the occupancy capture (a spot without items).</param>
    /// <param name="fine">
    /// Quad and special stash tabs (currency, fragments...) are walked on a fine 24x24 grid that does not match
    /// every slot, so one item can answer on several neighbouring points: touching points with identical text
    /// are counted once.
    /// </param>
    public static async Task<InventorySnapshot> ScanAsync(Rectangle client, GridGeometry geo, Point park,
        bool fine, AppSettings s, Func<bool> cancelled, Action<int, int>? progress = null)
    {
        // Let go of the hotkey's modifiers first, otherwise the game sees Ctrl+Shift+C etc.
        for (var i = 0; i < 50 && (Native.GetAsyncKeyState(0x11) < 0 || Native.GetAsyncKeyState(0x10) < 0 || Native.GetAsyncKeyState(0x12) < 0); i++)
            await Task.Delay(20);

        var cursor = Cursor.Position;

        // Park the cursor first: a tooltip from the item under the cursor would cover empty slots and
        // make them look occupied.
        Cursor.Position = park;
        await Task.Delay(120);

        bool[,] occupied;
        // Special tabs draw empty slot frames that are a bit bright; real items score far higher.
        using (var capture = GameWindow.Capture(client))
            occupied = Occupancy(capture, client, geo, out _, fine ? 0.10f : 0.03f);

        var snapshot = new InventorySnapshot { Time = DateTime.Now };
        var owner = new InventoryItem?[geo.Rows, geo.Cols];
        var texts = new Dictionary<InventoryItem, string>();
        string? previousClipboard = null;
        try { if (Clipboard.ContainsText()) previousClipboard = Clipboard.GetText(); } catch { /* busy */ }

        var cells = new List<(int R, int C)>();
        for (var r = 0; r < geo.Rows; r++)
        for (var c = 0; c < geo.Cols; c++)
            if (occupied[r, c]) cells.Add((r, c));
        snapshot.OccupiedCells = cells.Count;

        try
        {
            // Second pass, slower, for slots that gave nothing the first time.
            var missed = new List<(int R, int C)>();
            for (var pass = 0; pass < (fine ? 1 : 2); pass++)
            {
                var todo = pass == 0 ? cells : missed.ToList();
                missed.Clear();
                var done = 0;
                foreach (var (r, c) in todo)
                {
                    if (cancelled()) throw new OperationCanceledException();
                    if (pass == 0) progress?.Invoke(++done, todo.Count);
                    if (owner[r, c] != null) continue;

                    Cursor.Position = geo.Center(r, c);
                    await Task.Delay(Math.Clamp(s.InventoryHoverDelayMs, 15, 500) * (pass + 1) + pass * 50);
                    var text = await ItemText.CopyAsync(pass == 1 ? 500 : fine ? 150 : 250);
                    var item = text == null ? null : ItemText.Parse(text);
                    if (item == null)
                    {
                        missed.Add((r, c));
                        continue;
                    }

                    // A 2x3 weapon answers on all six slots: same text as an already-read neighbour = same item.
                    // Normal grid: only for non-stackables (two equal stacks side by side are two items).
                    if (fine || !text!.Contains("Stack Size:"))
                    {
                        var same = Neighbours(r, c, geo, fine).Select(n => owner[n.R, n.C])
                            .FirstOrDefault(o => o != null && texts[o] == text);
                        if (same != null)
                        {
                            owner[r, c] = same;
                            continue;
                        }
                    }

                    var entry = new InventoryItem
                    {
                        Name = item.Name, BaseType = item.BaseType, Rarity = item.Rarity, ItemClass = item.ItemClass,
                        Stack = item.Stack, Level = item.Level, Row = r, Col = c,
                    };
                    owner[r, c] = entry;
                    texts[entry] = text!; // item != null implies text != null
                    snapshot.Items.Add(entry);
                }
            }
            snapshot.UnreadCells = fine ? 0 : missed.Count;
        }
        finally
        {
            Cursor.Position = cursor;
            if (previousClipboard != null)
            {
                try { Clipboard.SetText(previousClipboard); } catch { /* best effort */ }
            }
        }
        return snapshot;
    }

    /// <summary>Already visited neighbours: left and up, plus the diagonals above on the fine grid.</summary>
    static IEnumerable<(int R, int C)> Neighbours(int r, int c, GridGeometry g, bool fine)
    {
        if (c > 0) yield return (r, c - 1);
        if (r > 0) yield return (r - 1, c);
        if (!fine || r == 0) yield break;
        if (c > 0) yield return (r - 1, c - 1);
        if (c + 1 < g.Cols) yield return (r - 1, c + 1);
    }

    /// <summary>
    /// A packed normal tab hides its grid lines, so ask the items: in a normal tab two points inside one
    /// 52 px slot belong to the same item; in quad/special tabs they mostly don't. Null = not enough items.
    /// </summary>
    public static async Task<bool?> ProbeNormalAsync(GridGeometry normal, bool[,] occupied, Func<bool> cancelled)
    {
        var cells = new List<(int R, int C)>();
        for (var r = 0; r < normal.Rows; r++)
        for (var c = 0; c < normal.Cols; c++)
            if (occupied[r, c]) cells.Add((r, c));
        if (cells.Count == 0) return null;

        // Spread the probes over the tab.
        var step = Math.Max(1, cells.Count / 8);
        int same = 0, differ = 0;
        for (var i = 0; i < cells.Count && same + differ < 8; i += step)
        {
            if (cancelled()) throw new OperationCanceledException();
            var (r, c) = cells[i];
            var cell = normal.CellRect(r, c);
            var a = await TextAt(new Point((int)(cell.Left + cell.Width * 0.28f), (int)(cell.Top + cell.Height * 0.28f)));
            var b = await TextAt(new Point((int)(cell.Left + cell.Width * 0.72f), (int)(cell.Top + cell.Height * 0.72f)));
            if (a == null && b == null) continue;
            if (a == b) same++;
            else differ++;
        }
        Log.Write($"stash probe: same={same} differ={differ}");
        if (same + differ < 3) return null;
        return same >= 0.75 * (same + differ);

        static async Task<string?> TextAt(Point p)
        {
            Cursor.Position = p;
            await Task.Delay(60);
            return await ItemText.CopyAsync(200);
        }
    }

    /// <summary>Is a panel open? Looks for its title (e.g. INVENTORY, STASH) with OCR before the mouse moves.</summary>
    public static async Task<List<TextSegment>> ReadTitleAreaAsync(OcrScanner ocr, Rectangle area)
    {
        using var bmp = GameWindow.Capture(area);
        return await ocr.RecognizeAsync(bmp, 2.0, area.Location);
    }

    public static bool HasWord(IEnumerable<TextSegment> segments, string word) =>
        segments.Any(sg => sg.Words.Any(w => ItemMatcher.Levenshtein(ItemMatcher.Normalize(w.Text), word, word.Length > 6 ? 2 : 1) >= 0));

    public static async Task<bool> InventoryOpenAsync(OcrScanner ocr, Rectangle client, GridGeometry g)
    {
        var k = client.Height / 1080f;
        var header = Rectangle.FromLTRB((int)g.Left, client.Top, client.Right, client.Top + (int)(130 * k));
        return HasWord(await ReadTitleAreaAsync(ocr, header), "inventory");
    }

    public static Point InventoryPark(Rectangle client, GridGeometry g) =>
        new((int)(g.Left + g.Cell * 6), client.Top + (int)(client.Height / 1080f * 62));
}
