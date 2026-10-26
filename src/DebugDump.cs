using System.Drawing.Imaging;
using System.Globalization;
using System.Text;

namespace Poe2Verlay;

/// <summary>Saves what a scan saw (capture + OCR boxes + labels) so OCR problems can be diagnosed.</summary>
internal static class DebugDump
{
    const int Keep = 5;

    public static string Dir => Path.Combine(AppSettings.Dir, "debug");

    public static void Save(Bitmap capture, Rectangle region, IReadOnlyList<TextSegment> segments,
        IReadOnlyList<Detection> detections, IReadOnlyList<OverlayBar> bars, string info)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var stem = Path.Combine(Dir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
            Write(stem, capture, region, segments, detections, bars, info);
            foreach (var old in Directory.GetFiles(Dir, "*.txt").OrderByDescending(f => f).Skip(Keep))
            {
                var oldStem = Path.ChangeExtension(old, null);
                foreach (var file in new[] { old, oldStem + ".png", oldStem + "-labels.jpg" })
                    if (File.Exists(file)) File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Log.Write("debug dump failed: " + ex.Message);
        }
    }

    /// <summary>Raw screenshot for calibrating grids (stash), keeps the last few per prefix.</summary>
    public static void SaveRaw(Bitmap capture, string prefix)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            capture.Save(Path.Combine(Dir, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.png"), ImageFormat.Png);
            foreach (var old in Directory.GetFiles(Dir, prefix + "-*.png").OrderByDescending(f => f).Skip(3))
                File.Delete(old);
        }
        catch (Exception ex)
        {
            Log.Write("raw capture save failed: " + ex.Message);
        }
    }

    public static void Write(string stem, Bitmap capture, Rectangle region, IReadOnlyList<TextSegment> segments,
        IReadOnlyList<Detection> detections, IReadOnlyList<OverlayBar> bars, string info)
    {
        // Raw capture (can be re-scanned with --scan-image) plus a small annotated copy to look at.
        capture.Save(stem + ".png", ImageFormat.Png);
        using (var annotated = new Bitmap(capture))
        {
            using (var g = Graphics.FromImage(annotated))
            {
                using var pen = new Pen(Color.FromArgb(160, 0, 200, 255), 1f);
                foreach (var seg in segments)
                {
                    var b = seg.Bounds;
                    g.DrawRectangle(pen, b.X - region.X, b.Y - region.Y, b.Width, b.Height);
                }
                OverlayRenderer.DrawBars(g, region.Location, region.Size, bars, segments.Select(sg => sg.Bounds).ToList());
            }
            annotated.Save(stem + "-labels.jpg", ImageFormat.Jpeg);
        }

        var sb = new StringBuilder();
        sb.AppendLine(info);
        sb.AppendLine($"region {region}");
        sb.AppendLine();
        sb.AppendLine($"== {detections.Count} matches");
        foreach (var d in detections)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"\"{d.OcrText}\" -> {d.Entry.Name} [{d.Entry.Category}] x{d.Quantity} dist={d.Distance} {d.Entry.Divine:0.####} div vol={d.Entry.Volume:0.##} @ {Rect(d.Bounds)}"));
        sb.AppendLine();
        sb.AppendLine($"== {segments.Count} OCR segments");
        foreach (var seg in segments)
            sb.AppendLine($"{Rect(seg.Bounds),-28} {seg.Text}");
        File.WriteAllText(stem + ".txt", sb.ToString());
    }

    static string Rect(RectangleF r) => string.Create(CultureInfo.InvariantCulture, $"({r.X:0},{r.Y:0} {r.Width:0}x{r.Height:0})");
}
