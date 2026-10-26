using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Poe2Verlay;

public sealed class OcrScanner
{
    readonly OcrEngine _engine;
    /// <summary>The engine runs one recognition at a time ("Another RecognizeAsync operation is already running"):
    /// a hotkey scan arriving during the rumour watcher's read waits for it instead of failing.</summary>
    readonly SemaphoreSlim _gate = new(1, 1);

    OcrScanner(OcrEngine engine) => _engine = engine;

    public string Language => _engine.RecognizerLanguage.LanguageTag;

    /// <summary>Tilt (degrees, clockwise) Windows OCR thought the last image had; 0 = none.</summary>
    public double LastTextAngle { get; private set; }

    public static OcrScanner? TryCreate(out string error)
    {
        error = "";
        try
        {
            // Item names are English, so prefer the en-US recognizer over the user's profile language.
            var english = new Windows.Globalization.Language("en-US");
            var engine = OcrEngine.IsLanguageSupported(english) ? OcrEngine.TryCreateFromLanguage(english) : null;
            engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine != null) return new OcrScanner(engine);
            error = "Windows OCR is not installed. Settings > Time & language > Language > add English (United States).";
        }
        catch (Exception ex)
        {
            error = "Windows OCR failed to start: " + ex.Message;
        }
        return null;
    }

    /// <summary>Automatic upscale: OCR reads small UI text much better when the capture is ~2160 px tall.</summary>
    public static double AutoScale(int captureHeight, double target) =>
        Math.Clamp(target / Math.Max(1, captureHeight), 1.0, 2.5);

    /// <summary>OCR the bitmap; returned rectangles are in screen coordinates (bitmap origin = <paramref name="origin"/>).</summary>
    public async Task<List<TextSegment>> RecognizeAsync(Bitmap source, double scale, Point origin)
    {
        var (segments, angle) = await RecognizeTiledAsync(source, scale, origin, 0);
        LastTextAngle = angle;
        return segments;
    }

    /// <summary>
    /// Game text is never tilted. When the engine is fooled into a big tilt (a diagonal shoreline across the
    /// screen: 21 degrees, and almost nothing gets read), read two overlapping halves on their own instead, each
    /// half keeping the text centred in its own half; left/right first, then top/bottom.
    /// </summary>
    async Task<(List<TextSegment> Segments, double Angle)> RecognizeTiledAsync(Bitmap source, double scale, Point origin, int depth)
    {
        var (segments, angle) = await RecognizeOnceAsync(source, scale, origin);
        if (Math.Abs(angle) < 8 || depth >= 2 || source.Width < 240 || source.Height < 240) return (segments, angle);

        var across = depth == 0;
        var size = across ? source.Width : source.Height;
        var tiled = new List<TextSegment>();
        foreach (var (from, to, ownFrom, ownTo) in new[] { (0, (int)(size * 0.6), 0, size / 2), ((int)(size * 0.4), size, size / 2, size) })
        {
            var tile = across ? new Rectangle(from, 0, to - from, source.Height) : new Rectangle(0, from, source.Width, to - from);
            using var part = source.Clone(tile, source.PixelFormat);
            var (partSegments, _) = await RecognizeTiledAsync(part, scale, new Point(origin.X + tile.X, origin.Y + tile.Y), depth + 1);
            tiled.AddRange(partSegments.Where(sg =>
            {
                var centre = across ? sg.Bounds.Left + sg.Bounds.Width / 2 - origin.X : sg.Bounds.Top + sg.Bounds.Height / 2 - origin.Y;
                return centre >= ownFrom && centre < ownTo;
            }));
        }
        return (tiled, angle);
    }

    async Task<(List<TextSegment> Segments, double Angle)> RecognizeOnceAsync(Bitmap source, double scale, Point origin)
    {
        double max = OcrEngine.MaxImageDimension;
        scale = Math.Min(scale, Math.Min(max / source.Width, max / source.Height));

        var (pixels, w, h) = await Task.Run(() => ToBgra(source, scale));
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, w, h,
            BitmapAlphaMode.Premultiplied);
        OcrResult result;
        await _gate.WaitAsync();
        try
        {
            result = await _engine.RecognizeAsync(bitmap);
        }
        finally
        {
            _gate.Release();
        }

        // Windows OCR sometimes decides a game screen is tilted (diagonal streaks in the scene, ~5 degrees) and
        // returns the boxes of the straightened image: everything left of centre lands a row too low, right of it
        // too high. Turn the box centres back by that angle so they sit on the text again.
        var tilt = result.TextAngle ?? 0;
        var angle = Math.Abs(tilt) > 0.05 ? tilt * Math.PI / 180 : 0;
        double cos = Math.Cos(angle), sin = Math.Sin(angle), cx = w / 2.0, cy = h / 2.0;

        var segments = new List<TextSegment>();
        foreach (var line in result.Lines)
        {
            var words = line.Words.Select(word =>
            {
                var r = word.BoundingRect;
                double mx = r.X + r.Width / 2, my = r.Y + r.Height / 2;
                if (angle != 0)
                {
                    var dx = mx - cx;
                    var dy = my - cy;
                    mx = cx + dx * cos - dy * sin;
                    my = cy + dx * sin + dy * cos;
                }
                return new OcrWord
                {
                    Text = word.Text,
                    Rect = new RectangleF(
                        (float)((mx - r.Width / 2) / scale + origin.X),
                        (float)((my - r.Height / 2) / scale + origin.Y),
                        (float)(r.Width / scale),
                        (float)(r.Height / scale)),
                };
            }).ToList();
            segments.AddRange(SplitLine(words));
        }
        return (segments, tilt);
    }

    static (byte[] Pixels, int Width, int Height) ToBgra(Bitmap source, double scale)
    {
        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));
        Bitmap? scaled = null;
        try
        {
            var bmp = source;
            if (w != source.Width || h != source.Height)
            {
                scaled = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(scaled);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, w, h));
                bmp = scaled;
            }

            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var pixels = new byte[w * h * 4];
                for (var y = 0; y < h; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * w * 4, w * 4);
                for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                return (pixels, w, h);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    /// <summary>Windows OCR happily joins side-by-side columns into one line; split them at large gaps.</summary>
    static IEnumerable<TextSegment> SplitLine(List<OcrWord> words)
    {
        if (words.Count == 0) yield break;
        words.Sort((a, b) => a.Rect.X.CompareTo(b.Rect.X));
        var height = words.Max(w => w.Rect.Height);

        var current = new List<OcrWord>();
        foreach (var word in words)
        {
            if (current.Count > 0 && word.Rect.Left - current[^1].Rect.Right > height * 1.5f)
            {
                yield return Segment(current);
                current = new List<OcrWord>();
            }
            current.Add(word);
        }
        yield return Segment(current);
    }

    static TextSegment Segment(List<OcrWord> words)
    {
        var bounds = words[0].Rect;
        foreach (var w in words.Skip(1)) bounds = RectangleF.Union(bounds, w.Rect);
        return new TextSegment { Words = words, Bounds = bounds };
    }
}

internal static class GameWindow
{
    static IntPtr _lastHwnd;
    static bool _lastResult;

    public static bool IsPoe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (hwnd == _lastHwnd) return _lastResult;

        var result = false;
        var sb = new StringBuilder(256);
        Native.GetClassName(hwnd, sb, sb.Capacity);
        if (sb.ToString() == "POEWindowClass")
        {
            result = true;
        }
        else
        {
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            try
            {
                using var p = Process.GetProcessById((int)pid);
                result = p.ProcessName.StartsWith("PathOfExile", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                result = false;
            }
        }

        _lastHwnd = hwnd;
        _lastResult = result;
        return result;
    }

    public static IntPtr ForegroundPoe()
    {
        var hwnd = Native.GetForegroundWindow();
        return IsPoe(hwnd) ? hwnd : IntPtr.Zero;
    }

    public static IntPtr FindPoe()
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.ProcessName.StartsWith("PathOfExile", StringComparison.OrdinalIgnoreCase) &&
                    p.MainWindowHandle != IntPtr.Zero)
                    return p.MainWindowHandle;
            }
            catch
            {
                // access denied etc.
            }
            finally
            {
                p.Dispose();
            }
        }
        return IntPtr.Zero;
    }

    public static Rectangle? ClientRect(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || Native.IsIconic(hwnd) || !Native.GetClientRect(hwnd, out var r)) return null;
        var pt = new Native.POINT();
        if (!Native.ClientToScreen(hwnd, ref pt)) return null;
        var rect = new Rectangle(pt.X, pt.Y, r.Right - r.Left, r.Bottom - r.Top);
        return rect.Width > 100 && rect.Height > 100 ? rect : null;
    }

    /// <summary>Screen pixels; <paramref name="layered"/> also takes layered windows (our overlay) when they allow capture.</summary>
    public static Bitmap Capture(Rectangle r, bool layered = false)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Location, Point.Empty, r.Size,
            layered ? CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt : CopyPixelOperation.SourceCopy);
        return bmp;
    }
}
