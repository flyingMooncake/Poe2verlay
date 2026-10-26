using System.Text;
using System.Text.RegularExpressions;

namespace Poe2Verlay;

public sealed class OcrWord
{
    public string Text = "";
    public RectangleF Rect;
}

/// <summary>A run of OCR words on one line, split where the horizontal gap is large (separate columns).</summary>
public sealed class TextSegment
{
    public List<OcrWord> Words = new();
    public RectangleF Bounds;
    public string Text => string.Join(" ", Words.Select(w => w.Text));
}

public sealed class Detection
{
    public RectangleF Bounds;
    public string OcrText = "";
    public PriceEntry Entry = null!;
    public List<PriceEntry> Variants = new();
    public int Quantity = 1;
    public int Distance;
    /// <summary>Height of one line of the matched text, used to size the label.</summary>
    public float TextHeight;
    public double TotalDivine => Entry.Divine * Quantity;
}

public sealed partial class ItemMatcher
{
    const int MaxWindowWords = 7;
    const int MaxLeftoverWords = 4;

    readonly Dictionary<string, List<PriceEntry>> _byKey = new();
    readonly Dictionary<int, List<string>> _keysByLength = new();
    readonly Dictionary<string, List<PriceEntry>> _uniquesByBase = new();

    public ItemMatcher(PriceDb db)
    {
        foreach (var e in db.Entries)
        {
            Add(Normalize(e.Name), e);
            if (e.IsUnique && e.BaseType != null)
            {
                var b = Normalize(e.BaseType);
                if (!_uniquesByBase.TryGetValue(b, out var list)) _uniquesByBase[b] = list = new List<PriceEntry>();
                list.Add(e);
            }
        }
        AddUniqueAggregates(db);
    }

    void Add(string key, PriceEntry e)
    {
        if (key.Length < 3) return;
        if (!_byKey.TryGetValue(key, out var list))
        {
            _byKey[key] = list = new List<PriceEntry>();
            if (!_keysByLength.TryGetValue(key.Length, out var bucket))
                _keysByLength[key.Length] = bucket = new List<string>();
            bucket.Add(key);
        }
        list.Add(e);
    }

    /// <summary>
    /// Remnants offer "Unique Ring", "Unique Belt"...: a random unique of that kind. Price it as the median
    /// of every listed unique in the category, i.e. what you will typically get.
    /// </summary>
    void AddUniqueAggregates(PriceDb db)
    {
        var uniques = db.Entries.Where(e => e.IsUnique && !e.Corrupted && e.Divine > 0).ToList();

        void Aggregate(string label, IEnumerable<PriceEntry> group)
        {
            var prices = group.Select(e => e.Divine).OrderBy(v => v).ToList();
            var key = Normalize("Unique " + label);
            if (prices.Count == 0 || _byKey.ContainsKey(key)) return;
            Add(key, new PriceEntry
            {
                Name = "Unique " + label,
                Category = "random unique",
                Divine = prices[prices.Count / 2],
                Volume = prices.Count,
                IsUnique = true,
                IsAggregate = true,
            });
        }

        foreach (var group in uniques.Where(e => e.ItemCategory != null).GroupBy(e => e.ItemCategory!))
            Aggregate(group.Key, group);
        Aggregate("Weapon", uniques.Where(e => e.Category == "UniqueWeapons"));
        Aggregate("Armour", uniques.Where(e => e.Category == "UniqueArmours"));
        Aggregate("Jewellery", uniques.Where(e => e.Category == "UniqueAccessories"));
        Aggregate("Accessory", uniques.Where(e => e.Category == "UniqueAccessories"));
        Aggregate("Item", uniques);

        // "Uncut Spirit Gem" without a level: median over the levels poe.ninja lists.
        foreach (var group in db.Entries.Where(e => !e.IsUnique && LevelSuffix().IsMatch(e.Name))
                     .GroupBy(e => LevelSuffix().Replace(e.Name, "")))
        {
            var key = Normalize(group.Key);
            if (_byKey.ContainsKey(key)) continue;
            var prices = group.Select(e => e.Divine).OrderBy(v => v).ToList();
            Add(key, new PriceEntry
            {
                Name = group.Key,
                Category = group.First().Category,
                Divine = prices[prices.Count / 2],
                Volume = prices.Count,
                IsAggregate = true,
            });
        }
    }

    [GeneratedRegex(@"\s*\(Level \d+\)$")]
    private static partial Regex LevelSuffix();

    public int Count => _byKey.Count;

    /// <summary>Lowercase, no apostrophes, everything that is not a letter/digit becomes a single space.</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        var pendingSpace = false;
        foreach (var raw in s)
        {
            var ch = char.ToLowerInvariant(raw);
            if (ch is '\'' or '’' or '‘' or '`' or '´') continue;
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                sb.Append(ch);
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }
        return sb.ToString();
    }

    /// <summary>Uniques that can drop on this base (an unidentified unique only shows its base).</summary>
    public List<PriceEntry>? UniquesOnBase(string baseType) =>
        _uniquesByBase.TryGetValue(Normalize(baseType), out var list) ? list : null;

    /// <summary>Exact lookup by item name (used for Ctrl+C item text).</summary>
    public List<PriceEntry>? Lookup(string name) => _byKey.TryGetValue(Normalize(name), out var list) ? list : null;

    /// <summary>
    /// A reward panel lists its choices right-aligned in one column. When at least 3 matches share a right edge, only
    /// matches beside that column count; the rest (chat lines, item labels on the ground) are not choices and must
    /// not take the best-pick star.
    /// </summary>
    public static List<Detection> PanelRows(List<Detection> detections)
    {
        if (detections.Count < 3) return detections;
        var heights = detections.Select(d => d.TextHeight).OrderBy(h => h).ToList();
        var lineHeight = heights[heights.Count / 2];
        var tolerance = Math.Max(12f, lineHeight * 0.6f);
        var column = detections
            .Select(d => detections.Where(o => Math.Abs(o.Bounds.Right - d.Bounds.Right) <= tolerance).ToList())
            .OrderByDescending(g => g.Count)
            .First();
        if (column.Count < 3) return detections;

        // Rows OCR read a bit off (a stray comma at the end) still belong: anything overlapping the column's width
        // within about a row of its top and bottom.
        var rows = column.OrderBy(d => d.Bounds.Top).ToList();
        var pitch = rows.Zip(rows.Skip(1), (a, b) => b.Bounds.Top - a.Bounds.Top).Where(g => g > 5).DefaultIfEmpty(lineHeight * 2).Min();
        float left = column.Min(d => d.Bounds.Left), right = column.Max(d => d.Bounds.Right);
        float top = rows[0].Bounds.Top - pitch * 1.5f, bottom = rows[^1].Bounds.Bottom + pitch * 1.5f;
        return detections.Where(d => d.Bounds.Right > left && d.Bounds.Left < right + lineHeight * 2 &&
                                     d.Bounds.Top > top && d.Bounds.Bottom < bottom).ToList();
    }

    public List<Detection> Find(IReadOnlyList<TextSegment> segments)
    {
        var result = new List<Detection>();
        var used = new HashSet<TextSegment>();

        foreach (var seg in segments)
        {
            var found = MatchSegment(seg, requireFull: false);
            if (found.Count == 0) continue;
            result.AddRange(found);
            used.Add(seg);
        }

        // Names that wrap onto a second line, e.g. "Thaumaturgic Flux" / "(Level 20)".
        foreach (var seg in segments)
        {
            if (used.Contains(seg)) continue;
            var below = FindLineBelow(seg, segments, used);
            if (below == null) continue;
            var merged = new TextSegment
            {
                Words = seg.Words.Concat(below.Words).ToList(),
                Bounds = RectangleF.Union(seg.Bounds, below.Bounds),
            };
            var found = MatchSegment(merged, requireFull: true);
            if (found.Count == 0) continue;
            result.AddRange(found);
            used.Add(seg);
            used.Add(below);
        }

        return result;
    }

    static TextSegment? FindLineBelow(TextSegment seg, IReadOnlyList<TextSegment> all, HashSet<TextSegment> used)
    {
        var a = seg.Bounds;
        TextSegment? best = null;
        var bestGap = float.MaxValue;
        foreach (var b in all)
        {
            if (b == seg || used.Contains(b)) continue;
            var r = b.Bounds;
            var gap = r.Top - a.Bottom;
            if (r.Top <= a.Top || gap > a.Height * 0.9f || gap < -a.Height * 0.3f) continue;
            var heightRatio = r.Height / Math.Max(1f, a.Height);
            if (heightRatio is < 0.6f or > 1.6f) continue;
            var centerDiff = Math.Abs((r.Left + r.Right) / 2 - (a.Left + a.Right) / 2);
            var leftDiff = Math.Abs(r.Left - a.Left);
            if (centerDiff > Math.Max(a.Width, r.Width) / 2 && leftDiff > a.Height) continue;
            if (gap < bestGap) { bestGap = gap; best = b; }
        }
        return best;
    }

    List<Detection> MatchSegment(TextSegment seg, bool requireFull)
    {
        var words = seg.Words;
        var norms = words.Select(w => Normalize(w.Text)).ToList();
        var matches = new List<(int Start, int Len, string Key, int Dist)>();

        var i = 0;
        while (i < words.Count)
        {
            if (norms[i].Length == 0 || QuantityBefore(words[i].Text) != null) { i++; continue; }
            var match = MatchAt(words, norms, i);
            if (match is { } m)
            {
                matches.Add((i, m.Len, m.Key, m.Dist));
                i += m.Len;
            }
            else
            {
                i++;
            }
        }

        var detections = new List<Detection>();
        if (matches.Count == 0) return detections;

        var covered = new bool[words.Count];
        foreach (var m in matches)
            for (var k = m.Start; k < m.Start + m.Len; k++) covered[k] = true;

        var perMatchQty = new List<(int Qty, int QtyWords)>();
        foreach (var m in matches)
            perMatchQty.Add(ReadQuantity(words, m.Start, m.Start + m.Len, covered));

        var leftover = 0;
        for (var k = 0; k < words.Count; k++)
            if (!covered[k] && norms[k].Length > 0) leftover++;
        leftover -= perMatchQty.Sum(q => q.QtyWords);
        if (requireFull ? leftover > 0 : leftover > MaxLeftoverWords) return detections;

        for (var n = 0; n < matches.Count; n++)
        {
            var m = matches[n];
            var span = words.GetRange(m.Start, m.Len);
            var bounds = span[0].Rect;
            foreach (var w in span.Skip(1)) bounds = RectangleF.Union(bounds, w.Rect);
            var variants = _byKey[m.Key];
            detections.Add(new Detection
            {
                Bounds = bounds,
                OcrText = string.Join(" ", span.Select(w => w.Text)),
                Entry = PickPrimary(variants),
                Variants = variants,
                Quantity = perMatchQty[n].Qty,
                Distance = m.Dist,
                TextHeight = span.Select(w => w.Rect.Height).OrderBy(h => h).ElementAt(span.Count / 2),
            });
        }
        return detections;
    }

    /// <summary>
    /// Longest exact match starting at word <paramref name="start"/>; failing that, the longest fuzzy match.
    /// Fuzzy windows may not end on a quantity token, otherwise "Liquid Verisium x5" would swallow the "x5".
    /// </summary>
    (int Len, string Key, int Dist)? MatchAt(List<OcrWord> words, List<string> norms, int start)
    {
        var maxLen = Math.Min(MaxWindowWords, words.Count - start);
        for (var pass = 0; pass < 2; pass++)
        {
            for (var len = maxLen; len >= 1; len--)
            {
                var candidate = string.Join(" ", norms.Skip(start).Take(len).Where(n => n.Length > 0));
                if (candidate.Length < 4) continue;
                if (pass == 0)
                {
                    if (_byKey.ContainsKey(candidate)) return (len, candidate, 0);
                    continue;
                }
                var last = words[start + len - 1].Text;
                if (len > 1 && (QuantityAfter(last) != null || QuantityBefore(last) != null)) continue;
                if (TryFuzzy(candidate, out var key, out var dist)) return (len, key, dist);
            }
        }
        return null;
    }

    /// <summary>Exchange items win over uniques with the same name; among uniques take the cheapest uncorrupted one.</summary>
    public static PriceEntry PickPrimary(List<PriceEntry> variants)
    {
        var exchange = variants.Where(v => !v.IsUnique).OrderByDescending(v => v.Volume).FirstOrDefault();
        if (exchange != null) return exchange;
        return variants.OrderBy(v => v.Corrupted).ThenBy(v => v.Divine).First();
    }

    static (int Qty, int QtyWords) ReadQuantity(List<OcrWord> words, int start, int end, bool[] covered)
    {
        if (start > 0 && !covered[start - 1] && QuantityBefore(words[start - 1].Text) is { } before)
            return (before, 1);
        if (end < words.Count && !covered[end])
        {
            if (QuantityAfter(words[end].Text) is { } after) return (after, 1);
            if (end + 1 < words.Count && !covered[end + 1] && words[end].Text.Trim() is "x" or "X" or "×" &&
                int.TryParse(words[end + 1].Text.Trim('(', ')', ' '), out var n) && n is > 0 and < 100000)
                return (n, 2);
        }
        return (1, 0);
    }

    // "3x", "3", "3×", "x3" in front of a name
    static int? QuantityBefore(string raw)
    {
        var m = QtyBeforeRegex().Match(OcrDigits(raw));
        return m.Success && int.TryParse(m.Groups[1].Value + m.Groups[2].Value, out var n) && n is > 0 and < 100000 ? n : null;
    }

    // "x3", "×3", "(x3)", "(3)" after a name
    static int? QuantityAfter(string raw)
    {
        var m = QtyAfterRegex().Match(OcrDigits(raw));
        return m.Success && int.TryParse(m.Groups[1].Value + m.Groups[2].Value, out var n) && n is > 0 and < 100000 ? n : null;
    }

    /// <summary>OCR reads the game's "1x" as "lx", "Ix" or "IX"; fix digits, but only inside quantity-shaped tokens.</summary>
    static string OcrDigits(string raw)
    {
        if (!QtyShapeRegex().IsMatch(raw)) return raw;
        return raw.Replace('l', '1').Replace('I', '1').Replace('|', '1').Replace('!', '1').Replace('O', '0').Replace('o', '0');
    }

    [GeneratedRegex(@"^\s*\(?(?:[\dlI|!Oo]{1,5}\s*[xX×]|[xX×]\s*[\dlI|!Oo]{1,5})\)?\s*$")]
    private static partial Regex QtyShapeRegex();

    [GeneratedRegex(@"^\s*(?:(\d{1,5})\s*[xX×]?|[xX×]\s*(\d{1,5}))\s*$")]
    private static partial Regex QtyBeforeRegex();

    [GeneratedRegex(@"^\s*(?:\(?[xX×]\s*(\d{1,5})\)?|\((\d{1,5})\))\s*$")]
    private static partial Regex QtyAfterRegex();

    bool TryFuzzy(string candidate, out string key, out int dist)
    {
        key = "";
        dist = int.MaxValue;
        var maxDist = MaxDistance(candidate.Length);
        if (maxDist == 0) return false;

        for (var len = candidate.Length - maxDist; len <= candidate.Length + maxDist; len++)
        {
            if (!_keysByLength.TryGetValue(len, out var keys)) continue;
            foreach (var k in keys)
            {
                var d = Levenshtein(candidate, k, Math.Min(maxDist, dist - 1));
                if (d < 0) continue;
                dist = d;
                key = k;
            }
        }
        return dist <= maxDist;
    }

    static int MaxDistance(int length) => length switch
    {
        < 6 => 0,
        < 10 => 1,
        < 17 => 2,
        _ => 3,
    };

    /// <summary>Edit distance, or -1 when it is larger than <paramref name="max"/>.</summary>
    public static int Levenshtein(string a, string b, int max)
    {
        if (max < 0 || Math.Abs(a.Length - b.Length) > max) return -1;
        Span<int> prev = stackalloc int[b.Length + 1];
        Span<int> cur = stackalloc int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (cur[j] < rowMin) rowMin = cur[j];
            }
            if (rowMin > max) return -1;
            var tmp = prev;
            prev = cur;
            cur = tmp;
        }
        return prev[b.Length] <= max ? prev[b.Length] : -1;
    }
}
