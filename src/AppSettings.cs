using System.Text.Json;

namespace Poe2Verlay;

public sealed class AppSettings
{
    /// <summary>poe.ninja league id. Empty = the current challenge league (first in poe.ninja's list).</summary>
    public string League { get; set; } = "";

    public string ScanHotkey { get; set; } = "F2";
    public string ItemHotkey { get; set; } = "Ctrl+S";
    public string AutoScanHotkey { get; set; } = "Ctrl+F2";
    public string RunStartHotkey { get; set; } = "Ctrl+I";
    public string RunEndHotkey { get; set; } = "Ctrl+O";
    public string InventoryWindowHotkey { get; set; } = "Ctrl+0";
    public string StashHotkey { get; set; } = "Ctrl+K";
    public string SessionHotkey { get; set; } = "Ctrl+F3";
    /// <summary>Saves a screenshot of the game with the overlay on it (normal screenshots don't show the overlay).</summary>
    public string ScreenshotHotkey { get; set; } = "Ctrl+F12";
    /// <summary>Farming session: seconds without keyboard / mouse input after which the clock stops.</summary>
    public int SessionIdleSeconds { get; set; } = 60;
    /// <summary>Last position of the inventory value window ("x,y,w,h", empty = default).</summary>
    public string InventoryWindowBounds { get; set; } = "";
    /// <summary>Run report overlay: small (just the sums) or big (tabs with every item).</summary>
    public bool RunWindowCompact { get; set; } = true;
    public bool HotkeysOnlyInGame { get; set; } = true;

    public bool AutoScan { get; set; }
    public int AutoScanIntervalMs { get; set; } = 1500;

    /// <summary>Upscale factor applied before OCR. 0 = pick automatically from the capture height.</summary>
    public double OcrScale { get; set; }

    /// <summary>Part of the game window that gets scanned, as fractions (0..1).</summary>
    public double RegionLeft { get; set; }
    public double RegionTop { get; set; }
    public double RegionRight { get; set; } = 1;
    public double RegionBottom { get; set; } = 1;

    public int OverlaySeconds { get; set; } = 20;

    /// <summary>Reward panel: when even the best choice is worth less than this (chaos), show ROLL instead of a best pick.</summary>
    public double RollBelowChaos { get; set; } = 3;

    /// <summary>poe.ninja traded volume (in divines) under which a price gets a "low trade" warning.</summary>
    public double LowTradeVolume { get; set; } = 5;
    public double VeryLowTradeVolume { get; set; } = 0.5;
    /// <summary>Uniques: listing count under which a price gets a "few listings" warning.</summary>
    public int LowListings { get; set; } = 5;

    /// <summary>How long the cursor rests on a slot before Ctrl+C during an inventory scan.</summary>
    public int InventoryHoverDelayMs { get; set; } = 50;
    /// <summary>Main inventory grid at 1080p: grid left edge measured from the window's right edge, top, slot size.</summary>
    public double InventoryRightOffset { get; set; } = 647;
    public double InventoryTop { get; set; } = 588;
    public double InventoryCell { get; set; } = 52.6;
    /// <summary>Stash grid at 1080p: left and top edge, and the side of the (square) grid.</summary>
    public double StashLeft { get; set; } = 17;
    public double StashTop { get; set; } = 128;
    public double StashSize { get; set; } = 632;
    /// <summary>"auto", "normal" (12x12), "quad" (24x24) or "special" (currency, fragments...).</summary>
    public string StashLayoutMode { get; set; } = "auto";
    public string StashWindowBounds { get; set; } = "";

    /// <summary>Rate the island rumours of uncharted waters (Expedition logbooks) while hovering them in a hideout.</summary>
    public bool ExpeditionRumours { get; set; } = true;
    /// <summary>Rumour ratings: a Google Sheets link or a local .csv (Rumor, Map, Mods, Rating). Empty = built-in.</summary>
    public string RumourSheet { get; set; } = RumourBook.DefaultSheet;

    public bool ShowExalted { get; set; }
    public bool IncludeUniques { get; set; } = true;
    public bool SaveDebug { get; set; } = true;
    public int PriceRefreshMinutes { get; set; } = 30;

    /// <summary>Bumped when defaults saved by an older version must be replaced.</summary>
    public int Version { get; set; }
    const int CurrentVersion = 2;

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "poe2verlay");

    static string FilePath => Path.Combine(Dir, "settings.json");
    public static bool Exists => File.Exists(FilePath);

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
                if (s.Version < 2)
                {
                    // 1.x guessed the stash grid 32 px too low (measured since: top 128 at 1080p).
                    var fresh = new AppSettings();
                    s.StashLeft = fresh.StashLeft;
                    s.StashTop = fresh.StashTop;
                    s.StashSize = fresh.StashSize;
                }
                s.Version = CurrentVersion;
                return s;
            }
        }
        catch (Exception ex)
        {
            Log.Write("settings load failed: " + ex.Message);
        }
        return new() { Version = CurrentVersion };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            Log.Write("settings save failed: " + ex.Message);
        }
    }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;
}

internal static class Log
{
    static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.Dir);
                var path = Path.Combine(AppSettings.Dir, "log.txt");
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }
}

internal static class Hotkey
{
    public static Keys Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Keys.None;
        var result = Keys.None;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": result |= Keys.Control; break;
                case "shift": result |= Keys.Shift; break;
                case "alt": result |= Keys.Alt; break;
                default:
                    if (part.Length == 1 && char.IsDigit(part[0])) result |= Keys.D0 + (part[0] - '0');
                    else if (Enum.TryParse<Keys>(part, true, out var key)) result |= key;
                    else return Keys.None;
                    break;
            }
        }
        return (result & Keys.KeyCode) == Keys.None ? Keys.None : result;
    }

    public static string Format(Keys keys)
    {
        if (keys == Keys.None) return "";
        var parts = new List<string>();
        if (keys.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (keys.HasFlag(Keys.Shift)) parts.Add("Shift");
        if (keys.HasFlag(Keys.Alt)) parts.Add("Alt");
        var code = keys & Keys.KeyCode;
        parts.Add(code is >= Keys.D0 and <= Keys.D9 ? ((int)(code - Keys.D0)).ToString() : code.ToString());
        return string.Join("+", parts);
    }
}
