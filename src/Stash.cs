using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Poe2Verlay;

public enum StashLayout { Normal, Quad, Special }

/// <summary>One scanned stash tab. Prices are applied when it is shown, so values follow poe.ninja.</summary>
public sealed class StashTabRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public StashLayout Layout { get; set; }
    /// <summary>Renamed by the user: re-scans keep the name instead of re-deriving it.</summary>
    public bool UserNamed { get; set; }
    public InventorySnapshot Snapshot { get; set; } = new();
}

internal static class StashStore
{
    static string FilePath => Path.Combine(AppSettings.Dir, "stash.json");

    public static List<StashTabRecord> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<StashTabRecord>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Write("stash load failed: " + ex.Message);
        }
        return new();
    }

    public static void Save(List<StashTabRecord> tabs)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(tabs));
        }
        catch (Exception ex)
        {
            Log.Write("stash save failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Store a scan. A tab holding mostly the same items (a re-scan) is replaced; anything else becomes a new tab,
    /// named after what it holds (tab labels in game are coloured and OCR misreads them).
    /// </summary>
    public static StashTabRecord Upsert(List<StashTabRecord> tabs, InventorySnapshot snapshot, StashLayout layout,
        SnapshotValue value, IEnumerable<string> visibleLabels)
    {
        var match = tabs.Select(t => (Tab: t, Score: Similarity(t.Snapshot, snapshot)))
            .Where(x => x.Score >= 0.5)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Tab)
            .FirstOrDefault();

        var name = PickName(value, visibleLabels);
        if (match == null)
        {
            match = new StashTabRecord { Name = UniqueName(tabs, name) };
            tabs.Add(match);
        }
        else if (!match.UserNamed)
        {
            match.Name = UniqueName(tabs.Where(t => t != match).ToList(), name);
        }
        match.Layout = layout;
        match.Snapshot = snapshot;
        Save(tabs);
        return match;
    }

    /// <summary>Words a stash tab label may use for each kind of content.</summary>
    static readonly Dictionary<string, string[]> LabelWords = new()
    {
        ["Currency"] = new[] { "currency", "orbs" },
        ["Essences"] = new[] { "essence", "essences" },
        ["Uniques"] = new[] { "unique", "uniques" },
        ["Gems"] = new[] { "gem", "gems", "uncut" },
        ["Runes"] = new[] { "rune", "runes", "augment", "augments", "socketable", "socketables" },
        ["Abyss"] = new[] { "abyss", "abyssal", "bones" },
        ["Ritual"] = new[] { "ritual", "omen", "omens" },
        ["Expedition"] = new[] { "expedition", "logbook", "logbooks", "verisium" },
        ["Verisium"] = new[] { "expedition", "verisium", "alloy", "alloys" },
        ["Fragments"] = new[] { "fragment", "fragments", "frags" },
        ["Breach"] = new[] { "breach", "catalyst", "catalysts" },
        ["Delirium"] = new[] { "delirium", "delirious", "liquid" },
        ["SoulCores"] = new[] { "soul", "souls", "cores" },
        ["Idols"] = new[] { "idol", "idols" },
        ["Waystones"] = new[] { "waystone", "waystones", "maps", "map" },
        ["Tablet"] = new[] { "tablet", "tablets" },
    };

    /// <summary>
    /// Prefer one of the tab labels visible above the grid when it fits what the tab holds (so the user's own
    /// "ABYSS" wins over poe.ninja filing abyss omens under Ritual); otherwise the dominant kind of content.
    /// </summary>
    internal static string PickName(SnapshotValue value, IEnumerable<string> visibleLabels)
    {
        if (value.Items.Count == 0) return "Empty";
        var kinds = value.Items.Select(i => i.Entry?.Category switch
        {
            null => string.IsNullOrEmpty(i.Detail) ? "Mixed" : i.Detail,
            var c when c.StartsWith("Unique", StringComparison.Ordinal) || c == "random unique" => "Uniques",
            "UncutGems" or "LineageSupportGems" => "Gems",
            var c => c,
        }).ToList();
        var counts = kinds.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());

        string? bestLabel = null;
        var bestCount = 0;
        foreach (var label in visibleLabels.SelectMany(SplitLabels))
        {
            if (label.Length < 3 || ItemMatcher.Levenshtein(ItemMatcher.Normalize(label), "stash", 1) >= 0) continue;
            var words = ItemMatcher.Normalize(label).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var (kind, count) in counts)
            {
                var synonyms = LabelWords.TryGetValue(kind, out var list) ? list : new[] { ItemMatcher.Normalize(kind) };
                var hit = words.SelectMany(w => synonyms.Select(syn => (Syn: syn, Dist: ItemMatcher.Levenshtein(w, syn, syn.Length >= 7 ? 2 : 1))))
                    .Where(x => x.Dist >= 0)
                    .OrderBy(x => x.Dist)
                    .FirstOrDefault();
                if (hit.Syn == null || count <= bestCount) continue;
                bestCount = count;
                // Read exactly: keep the user's own spelling ("ABYSS"); misread ("Auguments"): use the real word.
                bestLabel = hit.Dist == 0 ? label : char.ToUpperInvariant(hit.Syn[0]) + hit.Syn[1..];
            }
        }
        if (bestLabel != null) return bestLabel;

        // poe.ninja files every omen (abyss ones too) under Ritual; omens are generic consumables, so they
        // count half when deciding what a tab is for.
        double Weight(KeyValuePair<string, int> kv) => kv.Key == "Ritual" ? kv.Value * 0.5 : kv.Value;
        var top = counts.OrderByDescending(Weight).First();
        if (Weight(top) * 2 < counts.Sum(Weight)) return "Mixed";
        return top.Key == "Ritual" ? "Omens" : top.Key;
    }

    /// <summary>
    /// OCR reads a row of tab labels plus each tab's little lock/close icon as stray letters
    /// ("Currency O ' Essences"): split on those into separate labels.
    /// </summary>
    static IEnumerable<string> SplitLabels(string raw)
    {
        var current = new List<string>();
        foreach (var word in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var junk = word.Length <= 1 || !word.Any(char.IsLetter);
            if (junk)
            {
                if (current.Count > 0) yield return string.Join(" ", current);
                current.Clear();
            }
            else
            {
                current.Add(word.Trim('\'', '"', '.', ','));
            }
        }
        if (current.Count > 0) yield return string.Join(" ", current);
    }

    static string UniqueName(List<StashTabRecord> tabs, string name)
    {
        if (tabs.All(t => t.Name != name)) return name;
        for (var n = 2; ; n++)
            if (tabs.All(t => t.Name != $"{name} {n}")) return $"{name} {n}";
    }

    /// <summary>Share of item kinds two scans have in common (0..1).</summary>
    static double Similarity(InventorySnapshot a, InventorySnapshot b)
    {
        var x = a.Items.Select(i => i.Key).ToHashSet();
        var y = b.Items.Select(i => i.Key).ToHashSet();
        if (x.Count == 0 || y.Count == 0) return 0;
        return (double)x.Intersect(y).Count() / Math.Max(x.Count, y.Count);
    }
}

internal static class StashDetect
{
    /// <summary>
    /// Normal (12x12) and quad (24x24) tabs draw a slot grid; its lines show up as brightness edges every
    /// slot width. Quad tabs also have lines in the middle of normal-sized slots. Neither = a special tab.
    /// </summary>
    public static StashLayout Layout(Bitmap capture, Rectangle captureRect, RectangleF grid, out string detail)
    {
        var r = Rectangle.Round(grid);
        r.Offset(-captureRect.X, -captureRect.Y);
        r.Intersect(new Rectangle(0, 0, capture.Width, capture.Height));
        if (r.Width < 48 || r.Height < 48)
        {
            detail = "grid outside the capture";
            return StashLayout.Special;
        }

        var lum = Luminance(capture, r);
        var w = r.Width;
        var h = r.Height;
        var px = new double[w];
        var py = new double[h];
        for (var y = 0; y < h - 1; y++)
        for (var x = 0; x < w - 1; x++)
        {
            var v = lum[y * w + x];
            px[x] += Math.Abs(lum[y * w + x + 1] - v);
            py[y] += Math.Abs(lum[(y + 1) * w + x] - v);
        }

        var normal = (Score(px, w / 12.0, odd: false) + Score(py, h / 12.0, odd: false)) / 2;
        var quad = (Score(px, w / 24.0, odd: true) + Score(py, h / 24.0, odd: true)) / 2;
        var ratio = quad / Math.Max(1e-6, normal);
        detail = string.Create(CultureInfo.InvariantCulture, $"grid score normal={normal:0.00} quad={quad:0.00} ratio={ratio:0.00}");
        // Measured: normal tabs ~5-6 with quad/normal ~0.3-0.4, the currency tab ~1.9 / 1.5.
        if (normal >= 3 && ratio < 0.6) return StashLayout.Normal;
        if (quad >= 2.5 && ratio >= 0.6) return StashLayout.Quad;
        return StashLayout.Special;
    }

    /// <summary>Edge strength at multiples of <paramref name="period"/> relative to the typical column.</summary>
    static double Score(double[] profile, double period, bool odd)
    {
        var sorted = profile.OrderBy(v => v).ToArray();
        var median = Math.Max(1e-6, sorted[sorted.Length / 2]);
        var hits = new List<double>();
        for (var k = 1; k * period < profile.Length - 1; k++)
        {
            if (odd && k % 2 == 0) continue;
            var c = (int)Math.Round(k * period);
            var best = 0.0;
            for (var d = -1; d <= 1; d++)
                if (c + d >= 0 && c + d < profile.Length) best = Math.Max(best, profile[c + d]);
            hits.Add(best);
        }
        return hits.Count == 0 ? 0 : hits.Average() / median;
    }

    static float[] Luminance(Bitmap bmp, Rectangle r)
    {
        var data = bmp.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var lum = new float[r.Width * r.Height];
            var row = new byte[r.Width * 4];
            for (var y = 0; y < r.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var x = 0; x < r.Width; x++)
                    lum[y * r.Width + x] = (row[x * 4] * 11 + row[x * 4 + 1] * 59 + row[x * 4 + 2] * 30) / 100f;
            }
            return lum;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
