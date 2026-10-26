using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace Poe2Verlay;

public sealed class LabelLine
{
    public string Text = "";
    public Color Color = Palette.Text;
    public bool Bold;
    public float Size = 1f;
    /// <summary>Optional short text drawn after <see cref="Text"/> in its own color (e.g. a trade warning).</summary>
    public string? Tag;
    public Color TagColor = Palette.Warn;
}

public sealed class OverlayLabel
{
    /// <summary>Screen-space rectangle of the text the label belongs to.</summary>
    public RectangleF Anchor;
    public List<LabelLine> Lines = new();
    public Color Accent = Palette.Border;
    public bool HighlightAnchor = true;
    /// <summary>Height of one line of the anchor text (0 = use the anchor height).</summary>
    public float TextHeight;
}

/// <summary>A price bar drawn next to one reward row; all bars of a panel line up in one column.</summary>
public sealed class OverlayBar
{
    public RectangleF Anchor;
    public float TextHeight;
    /// <summary>Value relative to the most valuable choice on screen (0..1).</summary>
    public double Fraction;
    public string Text = "";
    public Color Fill = Palette.Gold;
    public string? Tag;
    public Color TagColor = Palette.Warn;
}

/// <summary>A report drawn on the overlay: text lines, an aligned table, more text lines.</summary>
public sealed class OverlayPanel
{
    /// <summary>Screen space: top edge, and the horizontal centre of the panel.</summary>
    public PointF TopCenter;
    public float FontPx = 16;
    public Color Accent = Palette.Border;
    public List<LabelLine> Header = new();
    /// <summary>Per table column: right-aligned (numbers).</summary>
    public bool[] RightAligned = Array.Empty<bool>();
    public List<OverlayRow> Rows = new();
    public List<LabelLine> Footer = new();
}

public sealed class OverlayRow
{
    public string[] Cells = Array.Empty<string>();
    public Color Color = Palette.Text;
    /// <summary>Optional colour per cell; null entries use <see cref="Color"/>.</summary>
    public Color?[]? CellColors;
    public bool Bold;
}

internal static class Palette
{
    public static readonly Color Background = Color.FromArgb(236, 14, 12, 10);
    public static readonly Color Border = Color.FromArgb(150, 120, 120, 120);
    public static readonly Color Text = Color.FromArgb(225, 225, 225);
    public static readonly Color Dim = Color.FromArgb(150, 150, 150);
    public static readonly Color Gold = Color.FromArgb(255, 205, 80);
    public static readonly Color Best = Color.FromArgb(90, 225, 110);
    public static readonly Color Warn = Color.FromArgb(255, 165, 60);
    public static readonly Color Bad = Color.FromArgb(255, 85, 85);
    public static readonly Color Up = Color.FromArgb(120, 210, 120);
    public static readonly Color Down = Color.FromArgb(230, 120, 120);
}

internal static class Fmt
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Number(double v) => v switch
    {
        >= 1000 => v.ToString("#,0", Inv),
        >= 100 => v.ToString("0", Inv),
        >= 10 => v.ToString("0.#", Inv),
        >= 1 => v.ToString("0.##", Inv),
        >= 0.01 => v.ToString("0.###", Inv),
        >= 0.001 => v.ToString("0.####", Inv),
        > 0 => v.ToString("0.######", Inv),
        _ => "0",
    };

    public static string Price(double divine, PriceDb db, AppSettings s)
    {
        var text = $"{Number(divine)} div  ·  {Number(divine * db.ChaosPerDivine)} c";
        if (s.ShowExalted && db.ExaltedPerDivine > 0) text += $"  ·  {Number(divine * db.ExaltedPerDivine)} ex";
        return text;
    }

    /// <summary>"lo – hi", or a single number when both ends are (almost) equal.</summary>
    public static string Range(double a, double b, bool signed = false)
    {
        var lo = Math.Min(a, b);
        var hi = Math.Max(a, b);
        string F(double v) => signed ? SignedNumber(v) : Number(v);
        return Math.Abs(hi - lo) < Math.Max(0.0005, Math.Abs(hi) * 0.005) ? F(hi) : $"{F(lo)} \u2013 {F(hi)}";
    }

    public static string SignedNumber(double v) => (v >= 0 ? "+" : "\u2212") + Number(Math.Abs(v));

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";

    public static string Age(DateTime utc)
    {
        var minutes = (int)(DateTime.UtcNow - utc).TotalMinutes;
        return minutes < 1 ? "just now" : minutes < 120 ? $"{minutes} min old" : $"{minutes / 60} h old";
    }
}

internal static class LabelBuilder
{
    public static List<OverlayBar> BarsForDetections(IReadOnlyList<Detection> detections, PriceDb db, AppSettings s)
    {
        var max = detections.Count == 0 ? 0 : detections.Max(d => d.TotalDivine);
        var distinct = detections.Select(d => d.Entry).Distinct().Count();
        // Nothing worth taking: say so instead of starring the least bad.
        var roll = detections.Count > 0 && max * db.ChaosPerDivine < s.RollBelowChaos;
        var bars = new List<OverlayBar>();
        foreach (var d in detections)
        {
            var isBest = !roll && distinct >= 2 && max > 0 && d.TotalDivine >= max;
            var text = d.Quantity > 1 ? $"{d.Quantity}\u00d7 = {Fmt.Price(d.TotalDivine, db, s)}" : Fmt.Price(d.Entry.Divine, db, s);
            var (tag, tagColor) = ShortTrade(d.Entry, s);
            bars.Add(new OverlayBar
            {
                Anchor = d.Bounds,
                TextHeight = d.TextHeight,
                Fraction = max > 0 ? d.TotalDivine / max : 0,
                Text = isBest ? "\u2605 " + text : text,
                Fill = isBest ? Palette.Best : d.TotalDivine >= 1 ? Palette.Gold : Color.FromArgb(200, 160, 90),
                Tag = tag,
                TagColor = tagColor,
            });
        }
        if (roll)
        {
            // One row above the first choice, in the same column as the bars.
            var rows = detections.OrderBy(d => d.Bounds.Top).ToList();
            var pitch = rows.Zip(rows.Skip(1), (a, b) => b.Bounds.Top - a.Bounds.Top).Where(g => g > 5).DefaultIfEmpty(rows[0].TextHeight * 2.2f).Min();
            var anchor = rows[0].Bounds;
            anchor.Offset(0, -pitch);
            bars.Insert(0, new OverlayBar
            {
                Anchor = anchor,
                TextHeight = rows[0].TextHeight,
                Fraction = 1,
                Text = $"\u21bb ROLL  \u00b7  best is only {Fmt.Number(max * db.ChaosPerDivine)} c",
                Fill = Palette.Warn,
            });
        }
        return bars;
    }

    public static OverlayLabel ForItem(Point cursor, string title, PriceEntry entry, List<PriceEntry> variants,
        int quantity, PriceDb db, AppSettings s)
    {
        var label = new OverlayLabel
        {
            Anchor = new RectangleF(cursor.X + 12, cursor.Y - 12, 8, 24),
            HighlightAnchor = false,
            Accent = ValueColor(entry.Divine * quantity),
        };
        label.Lines.Add(new LabelLine { Text = title, Color = Palette.Text, Bold = true, Size = 0.9f });
        AddPriceLines(label, entry, variants, quantity, db, s, unitFirst: true);
        return label;
    }

    public static OverlayLabel Message(RectangleF anchor, string title, string? detail)
    {
        var label = new OverlayLabel { Anchor = anchor, HighlightAnchor = false, Accent = Palette.Warn };
        label.Lines.Add(new LabelLine { Text = title, Color = Palette.Text, Bold = true });
        if (!string.IsNullOrEmpty(detail)) label.Lines.Add(new LabelLine { Text = detail, Color = Palette.Dim, Size = 0.82f });
        return label;
    }

    static void AddPriceLines(OverlayLabel label, PriceEntry e, List<PriceEntry> variants, int quantity, PriceDb db, AppSettings s,
        bool unitFirst = false)
    {
        if (unitFirst)
        {
            // Hovered item: price per piece is the headline, the stack total comes second.
            label.Lines.Add(new LabelLine { Text = Fmt.Price(e.Divine, db, s) + "  /buc", Color = Palette.Gold, Bold = true });
            if (quantity > 1)
                label.Lines.Add(new LabelLine { Text = $"stack {quantity} = {Fmt.Price(e.Divine * quantity, db, s)}", Color = Palette.Text, Size = 0.85f });
        }
        else if (quantity > 1)
        {
            label.Lines.Add(new LabelLine { Text = $"{quantity}× = {Fmt.Price(e.Divine * quantity, db, s)}", Color = Palette.Gold, Bold = true });
            label.Lines.Add(new LabelLine { Text = "each " + Fmt.Price(e.Divine, db, s), Color = Palette.Dim, Size = 0.82f });
        }
        else
        {
            label.Lines.Add(new LabelLine { Text = Fmt.Price(e.Divine, db, s), Color = Palette.Gold, Bold = true });
        }

        if (e.IsUnique)
        {
            var others = variants.Where(v => v != e && v.IsUnique).ToList();
            if (others.Count > 0)
            {
                var max = others.Max(v => v.Divine);
                label.Lines.Add(new LabelLine { Text = $"+{others.Count} variant(s), up to {Fmt.Number(max)} div", Color = Palette.Dim, Size = 0.82f });
            }
        }

        var (trade, tradeColor) = TradeInfo(e, s);
        var change = e.Change7d is { } c && Math.Abs(c) >= 5 ? $"7d {(c > 0 ? "+" : "")}{c.ToString("0", CultureInfo.InvariantCulture)}%" : null;
        if (trade != null)
        {
            label.Lines.Add(new LabelLine { Text = change == null ? trade : $"{trade}  ·  {change}", Color = tradeColor, Size = 0.82f });
        }
        else if (change != null)
        {
            var big = Math.Abs(e.Change7d!.Value) >= 15;
            label.Lines.Add(new LabelLine { Text = change, Color = !big ? Palette.Dim : e.Change7d > 0 ? Palette.Up : Palette.Down, Size = 0.82f });
        }
    }

    static (string? Text, Color Color) ShortTrade(PriceEntry e, AppSettings s)
    {
        if (e.IsAggregate) return (e.IsUnique ? $"random \u00b7 median of {e.Volume:0}" : $"any level \u00b7 median of {e.Volume:0}", Palette.Dim);
        if (e.IsUnique) return e.Volume < s.LowListings ? ("\u26a0 few listings", Palette.Warn) : (null, Palette.Dim);
        if (e.Volume < s.VeryLowTradeVolume) return ("\u26a0 very low trade", Palette.Bad);
        if (e.Volume < s.LowTradeVolume) return ("\u26a0 low trade", Palette.Warn);
        return (null, Palette.Dim);
    }

    static (string? Text, Color Color) TradeInfo(PriceEntry e, AppSettings s)
    {
        if (e.IsUnique)
            return e.Volume < s.LowListings ? ($"⚠ few listings ({e.Volume:0})", Palette.Warn) : (null, Palette.Dim);
        if (e.Volume < s.VeryLowTradeVolume)
            return ($"⚠ very low trade ({Fmt.Number(e.Volume)} div vol) – unreliable", Palette.Bad);
        if (e.Volume < s.LowTradeVolume)
            return ($"⚠ low trade ({Fmt.Number(e.Volume)} div vol)", Palette.Warn);
        return (null, Palette.Dim);
    }

    static Color ValueColor(double divine) => divine switch
    {
        >= 1 => Palette.Gold,
        >= 0.1 => Color.FromArgb(200, 200, 200),
        _ => Color.FromArgb(110, 110, 110),
    };
}

internal static class OverlayRenderer
{
    /// <summary>
    /// Draws labels onto a surface whose top-left corner is at <paramref name="origin"/> in screen space.
    /// <paramref name="obstacles"/> (screen space) is other on-screen text the labels should try not to cover.
    /// </summary>
    public static void Draw(Graphics g, Point origin, Size surface, IReadOnlyList<OverlayLabel> labels,
        IReadOnlyList<RectangleF>? obstacles = null)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var format = StringFormat.GenericTypographic;
        var placed = new List<RectangleF>();
        var avoid = (obstacles ?? Array.Empty<RectangleF>())
            .Concat(labels.Where(l => l.HighlightAnchor).Select(l => l.Anchor))
            .Select(r => { r.Offset(-origin.X, -origin.Y); return RectangleF.Inflate(r, 3, 2); })
            .ToList();

        foreach (var label in labels)
        {
            var anchor = label.Anchor;
            anchor.Offset(-origin.X, -origin.Y);
            var textHeight = label.TextHeight > 0 ? label.TextHeight : anchor.Height;
            var basePx = Math.Clamp(textHeight * 0.8f, 15f, 24f);

            var fonts = label.Lines.Select(l => new Font("Segoe UI", basePx * l.Size, l.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel)).ToList();
            try
            {
                var tagFonts = label.Lines.Select(l => l.Tag == null ? null : new Font("Segoe UI", basePx * l.Size * 0.85f, FontStyle.Bold, GraphicsUnit.Pixel)).ToList();
                fonts.AddRange(tagFonts.OfType<Font>()); // disposed with the rest
                var tagGap = basePx * 0.6f;
                var textSizes = label.Lines.Select((l, i) => g.MeasureString(l.Text, fonts[i], PointF.Empty, format)).ToList();
                var tagSizes = label.Lines.Select((l, i) => l.Tag == null ? SizeF.Empty : g.MeasureString(l.Tag, tagFonts[i]!, PointF.Empty, format)).ToList();
                var sizes = textSizes.Select((t, i) => label.Lines[i].Tag == null ? t
                    : new SizeF(t.Width + tagGap + tagSizes[i].Width, Math.Max(t.Height, tagSizes[i].Height))).ToList();
                var pad = basePx * 0.4f;
                var lineGap = basePx * 0.12f;
                var width = sizes.Max(sz => sz.Width) + pad * 2;
                var height = sizes.Sum(sz => sz.Height) + lineGap * (sizes.Count - 1) + pad * 2;

                var r = Place(anchor, new SizeF(width, height), surface, avoid, placed);
                placed.Add(r);

                if (label.HighlightAnchor)
                {
                    var hl = RectangleF.Inflate(anchor, 3, 2);
                    using var hlPen = new Pen(Color.FromArgb(170, label.Accent), 1.5f);
                    using var hlPath = Rounded(hl, 3);
                    g.DrawPath(hlPen, hlPath);
                }

                using (var path = Rounded(r, pad * 0.8f))
                using (var bg = new SolidBrush(Palette.Background))
                using (var border = new Pen(label.Accent, 1.8f))
                {
                    g.FillPath(bg, path);
                    g.DrawPath(border, path);
                }

                var y = r.Y + pad;
                for (var i = 0; i < label.Lines.Count; i++)
                {
                    var line = label.Lines[i];
                    using var brush = new SolidBrush(line.Color);
                    g.DrawString(line.Text, fonts[i], brush, r.X + pad, y + sizes[i].Height - textSizes[i].Height, format);
                    if (line.Tag != null)
                    {
                        using var tagBrush = new SolidBrush(line.TagColor);
                        g.DrawString(line.Tag, tagFonts[i]!, tagBrush, r.X + pad + textSizes[i].Width + tagGap,
                            y + (sizes[i].Height - tagSizes[i].Height) * 0.75f, format);
                    }
                    y += sizes[i].Height + lineGap;
                }
            }
            finally
            {
                foreach (var f in fonts) f.Dispose();
            }
        }
    }

    /// <param name="obstacles">Other on-screen text (screen space); a bar stops short of text to its right.</param>
    public static void DrawBars(Graphics g, Point origin, Size surface, IReadOnlyList<OverlayBar> bars,
        IReadOnlyList<RectangleF>? obstacles = null)
    {
        if (bars.Count == 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var format = StringFormat.GenericTypographic;

        var heights = bars.Select(b => b.TextHeight > 0 ? b.TextHeight : b.Anchor.Height).OrderBy(h => h).ToList();
        var fontPx = Math.Clamp(heights[heights.Count / 2] * 0.9f, 15f, 24f);
        using var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
        using var tagFont = new Font("Segoe UI", fontPx * 0.8f, FontStyle.Bold, GraphicsUnit.Pixel);
        var pad = fontPx * 0.55f;
        var gap = fontPx * 0.6f;
        var barH = fontPx * 1.6f;

        var textSize = bars.Select(b => g.MeasureString(b.Text, font, PointF.Empty, format)).ToList();
        var tagSize = bars.Select(b => b.Tag == null ? SizeF.Empty : g.MeasureString(b.Tag, tagFont, PointF.Empty, format)).ToList();
        var need = bars.Select((b, i) => textSize[i].Width + (b.Tag == null ? 0 : gap + tagSize[i].Width) + pad * 2 + 4).ToList();
        var trackW = Math.Max(fontPx * 17, need.Max());
        var others = (obstacles ?? Array.Empty<RectangleF>()).Select(r => { r.Offset(-origin.X, -origin.Y); return r; }).ToList();

        // Rows of one panel overlap horizontally: give them a shared start x so the bars form one column.
        var anchors = bars.Select(b => { var a = b.Anchor; a.Offset(-origin.X, -origin.Y); return a; }).ToList();
        var parent = Enumerable.Range(0, bars.Count).ToArray();
        int Root(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        for (var i = 0; i < bars.Count; i++)
        for (var j = i + 1; j < bars.Count; j++)
            if (anchors[i].Left < anchors[j].Right && anchors[j].Left < anchors[i].Right)
                parent[Root(i)] = Root(j);

        var startX = new Dictionary<int, float>();
        foreach (var group in Enumerable.Range(0, bars.Count).GroupBy(Root))
        {
            var right = group.Max(i => anchors[i].Right);
            var left = group.Min(i => anchors[i].Left);
            var x = right + gap;
            if (x + trackW > surface.Width) x = left - gap - trackW;
            startX[group.Key] = Math.Max(0, x);
        }

        using var trackBrush = new SolidBrush(Color.FromArgb(215, 10, 9, 8));
        using var shadow = new SolidBrush(Color.FromArgb(230, 0, 0, 0));
        using var textBrush = new SolidBrush(Color.FromArgb(245, 245, 245));
        for (var i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            var a = anchors[i];
            var track = new RectangleF(startX[Root(i)], a.Top + a.Height / 2 - barH / 2, trackW, barH);
            var blocking = others
                .Where(o => o.Left > track.Left && o.Top < track.Bottom && o.Bottom > track.Top && !o.IntersectsWith(a))
                .Select(o => o.Left - 6)
                .DefaultIfEmpty(float.MaxValue)
                .Min();
            track.Width = Math.Clamp(blocking - track.Left, need[i], trackW);
            g.FillRectangle(trackBrush, track);

            var fillW = Math.Max(4f, (track.Width - 4) * (float)Math.Clamp(b.Fraction, 0, 1));
            using (var fill = new SolidBrush(Color.FromArgb(120, b.Fill)))
                g.FillRectangle(fill, track.X + 4, track.Y, fillW, track.Height);
            using (var edge = new SolidBrush(b.Fill))
                g.FillRectangle(edge, track.X, track.Y, 4, track.Height);

            var tx = track.X + 4 + pad;
            var ty = track.Y + (barH - textSize[i].Height) / 2;
            g.DrawString(b.Text, font, shadow, tx + 1, ty + 1, format);
            g.DrawString(b.Text, font, textBrush, tx, ty, format);
            if (b.Tag != null)
            {
                var gx = tx + textSize[i].Width + gap;
                var gy = track.Y + (barH - tagSize[i].Height) / 2;
                using var tagBrush = new SolidBrush(b.TagColor);
                g.DrawString(b.Tag, tagFont, shadow, gx + 1, gy + 1, format);
                g.DrawString(b.Tag, tagFont, tagBrush, gx, gy, format);
            }
        }
    }

    public static void DrawPanel(Graphics g, Point origin, Size surface, OverlayPanel p)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var format = StringFormat.GenericTypographic;
        var fonts = new Dictionary<(float, bool), Font>();
        Font F(float size, bool bold)
        {
            if (!fonts.TryGetValue((size, bold), out var f))
                fonts[(size, bold)] = f = new Font("Segoe UI", p.FontPx * size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            return f;
        }
        try
        {
            var pad = p.FontPx * 0.8f;
            var colGap = p.FontPx * 1.2f;
            var lineGap = p.FontPx * 0.2f;
            var tagGap = p.FontPx * 0.6f;
            SizeF Measure(string text, Font f) => g.MeasureString(text, f, PointF.Empty, format);
            SizeF LineSize(LabelLine l)
            {
                var t = Measure(l.Text, F(l.Size, l.Bold));
                if (l.Tag == null) return t;
                var tag = Measure(l.Tag, F(l.Size * 0.9f, true));
                return new SizeF(t.Width + tagGap + tag.Width, Math.Max(t.Height, tag.Height));
            }

            var header = p.Header.Select(LineSize).ToList();
            var footer = p.Footer.Select(LineSize).ToList();
            var cols = p.Rows.Count == 0 ? 0 : p.Rows.Max(r => r.Cells.Length);
            var colW = new float[cols];
            foreach (var row in p.Rows)
                for (var i = 0; i < row.Cells.Length; i++)
                    colW[i] = Math.Max(colW[i], Measure(row.Cells[i], F(0.92f, row.Bold)).Width);
            var rowH = Measure("Ag", F(0.92f, false)).Height + lineGap * 1.5f;
            var tableW = colW.Sum() + colGap * Math.Max(0, cols - 1);
            var sep = p.FontPx * 0.6f;

            var width = Math.Max(tableW, header.Concat(footer).Select(sz => sz.Width).DefaultIfEmpty(0).Max()) + pad * 2;
            var height = pad * 2 + header.Sum(sz => sz.Height + lineGap) + footer.Sum(sz => sz.Height + lineGap) +
                         (p.Rows.Count > 0 ? sep * 2 + rowH * p.Rows.Count : 0);
            var x = Math.Clamp(p.TopCenter.X - origin.X - width / 2, 0, Math.Max(0, surface.Width - width));
            var y = Math.Clamp(p.TopCenter.Y - origin.Y, 0, Math.Max(0, surface.Height - height));
            var r = new RectangleF(x, y, width, height);

            using (var path = Rounded(r, pad * 0.6f))
            using (var bg = new SolidBrush(Palette.Background))
            using (var border = new Pen(p.Accent, 1.8f))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }

            var cy = r.Y + pad;
            void Lines(List<LabelLine> lines, List<SizeF> sizes)
            {
                for (var i = 0; i < lines.Count; i++)
                {
                    var l = lines[i];
                    var font = F(l.Size, l.Bold);
                    var t = Measure(l.Text, font);
                    using (var brush = new SolidBrush(l.Color))
                        g.DrawString(l.Text, font, brush, r.X + pad, cy + sizes[i].Height - t.Height, format);
                    if (l.Tag != null)
                    {
                        var tagFont = F(l.Size * 0.9f, true);
                        using var tagBrush = new SolidBrush(l.TagColor);
                        g.DrawString(l.Tag, tagFont, tagBrush, r.X + pad + t.Width + tagGap, cy + sizes[i].Height - Measure(l.Tag, tagFont).Height, format);
                    }
                    cy += sizes[i].Height + lineGap;
                }
            }

            Lines(p.Header, header);
            if (p.Rows.Count > 0)
            {
                using var line = new Pen(Color.FromArgb(90, 160, 160, 160), 1f);
                cy += sep / 2;
                g.DrawLine(line, r.X + pad, cy, r.Right - pad, cy);
                cy += sep / 2;
                using var stripe = new SolidBrush(Color.FromArgb(16, 255, 255, 255));
                for (var ri = 0; ri < p.Rows.Count; ri++)
                {
                    var row = p.Rows[ri];
                    if (ri % 2 == 1) g.FillRectangle(stripe, r.X + pad * 0.5f, cy, r.Width - pad, rowH);
                    var font = F(0.92f, row.Bold);
                    var cx = r.X + pad;
                    for (var i = 0; i < row.Cells.Length; i++)
                    {
                        var text = row.Cells[i];
                        var w = Measure(text, font).Width;
                        var right = i < p.RightAligned.Length && p.RightAligned[i];
                        using var brush = new SolidBrush(row.CellColors is { } cc && i < cc.Length && cc[i] is { } c ? c : row.Color);
                        g.DrawString(text, font, brush, right ? cx + colW[i] - w : cx, cy + lineGap * 0.75f, format);
                        cx += colW[i] + colGap;
                    }
                    cy += rowH;
                }
                cy += sep / 2;
                g.DrawLine(line, r.X + pad, cy, r.Right - pad, cy);
                cy += sep / 2;
            }
            Lines(p.Footer, footer);
        }
        finally
        {
            foreach (var f in fonts.Values) f.Dispose();
        }
    }

    /// <summary>Try spots around the text and keep the one that covers the least other text / labels.</summary>
    static RectangleF Place(RectangleF a, SizeF size, Size surface, List<RectangleF> avoid, List<RectangleF> placed)
    {
        const float gap = 8;
        var cy = a.Top + a.Height / 2 - size.Height / 2;
        var candidates = new List<PointF>
        {
            new(a.Right + gap, cy),
            new(a.Left, a.Bottom + 4),
            new(a.Left, a.Top - 4 - size.Height),
            new(a.Left - gap - size.Width, cy),
        };
        for (var k = 1; k <= 4; k++)
        {
            candidates.Add(new(a.Right + gap, cy + k * size.Height * 0.6f));
            candidates.Add(new(a.Right + gap, cy - k * size.Height * 0.6f));
            candidates.Add(new(a.Left, a.Bottom + 4 + k * size.Height * 0.6f));
        }

        var best = RectangleF.Empty;
        var bestScore = double.MaxValue;
        for (var i = 0; i < candidates.Count; i++)
        {
            var p = candidates[i];
            var x = Math.Clamp(p.X, 0, Math.Max(0, surface.Width - size.Width));
            var y = Math.Clamp(p.Y, 0, Math.Max(0, surface.Height - size.Height));
            var r = new RectangleF(x, y, size.Width, size.Height);

            double score = i * 40;
            if (Math.Abs(x - p.X) > 0.5f || Math.Abs(y - p.Y) > 0.5f) score += 300;
            foreach (var o in avoid) score += Overlap(r, o);
            foreach (var o in placed) score += Overlap(r, o) * 4;
            if (score < bestScore)
            {
                bestScore = score;
                best = r;
            }
        }
        return best;
    }

    static double Overlap(RectangleF a, RectangleF b)
    {
        var i = RectangleF.Intersect(a, b);
        return i.IsEmpty ? 0 : i.Width * i.Height;
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = Math.Max(1f, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>Click-through, never-focused, per-pixel-alpha window drawn above the game.</summary>
internal sealed class OverlayForm : Form
{
    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Text = "poe2verlay overlay";
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW |
                          Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Keep our own labels out of the screen captures we OCR.
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_DPICHANGED) return; // geometry is managed in physical pixels
        base.WndProc(ref m);
    }

    public void EnsureHandle() => _ = Handle;

    public void ShowLabels(Rectangle area, IReadOnlyList<OverlayLabel> labels, IReadOnlyList<RectangleF>? obstacles = null,
        IReadOnlyList<OverlayBar>? bars = null, OverlayPanel? panel = null)
    {
        using var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            OverlayRenderer.Draw(g, area.Location, area.Size, labels, obstacles);
            if (bars != null) OverlayRenderer.DrawBars(g, area.Location, area.Size, bars, obstacles);
            if (panel != null) OverlayRenderer.DrawPanel(g, area.Location, area.Size, panel);
        }

        if (!Visible)
        {
            Bounds = area;
            Show();
        }
        else if (Bounds != area)
        {
            Bounds = area;
        }
        Push(bmp, area.Location);
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    void Push(Bitmap bmp, Point location)
    {
        var screenDc = Native.GetDC(IntPtr.Zero);
        var memDc = Native.CreateCompatibleDC(screenDc);
        var hBitmap = IntPtr.Zero;
        var old = IntPtr.Zero;
        try
        {
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            old = Native.SelectObject(memDc, hBitmap);
            var size = new Native.SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new Native.POINT();
            var dst = new Native.POINT { X = location.X, Y = location.Y };
            var blend = new Native.BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA };
            if (!Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA))
                Log.Write("UpdateLayeredWindow failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }
        finally
        {
            if (old != IntPtr.Zero) Native.SelectObject(memDc, old);
            if (hBitmap != IntPtr.Zero) Native.DeleteObject(hBitmap);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
