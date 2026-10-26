using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Poe2Verlay;

public enum Trade { Ok, Low, VeryLow, FewListings, Random, NoPrice }

/// <summary>All copies of one item in a snapshot, summed.</summary>
public sealed class ValuedItem
{
    public string Key = "";
    public string Name = "";
    /// <summary>Base type or item class, shown for things that have no price.</summary>
    public string? Detail;
    public int Qty;
    public PriceEntry? Entry;
    public Trade Trade;
    public double Unit => Entry?.Divine ?? 0;
    public double Total => Unit * Qty;
}

public sealed class SnapshotValue
{
    public InventorySnapshot Snapshot = null!;
    public List<ValuedItem> Items = new();
    /// <summary>Only items with real trade volume.</summary>
    public double Min => Items.Where(i => i.Trade == Trade.Ok).Sum(i => i.Total);
    /// <summary>Everything that has a price.</summary>
    public double Max => Items.Where(i => i.Entry != null).Sum(i => i.Total);
    public int Unpriced => Items.Count(i => i.Entry == null);
}

public sealed class DiffLine
{
    public string Name = "";
    public string? Detail;
    public int Before, After;
    public PriceEntry? Entry;
    public Trade Trade;
    public int Delta => After - Before;
    public double Value => (Entry?.Divine ?? 0) * Delta;
}

public sealed class RunReport
{
    public int Number;
    public SnapshotValue Start = null!, End = null!;
    public List<DiffLine> Lines = new();
    public TimeSpan Duration => End.Snapshot.Time - Start.Snapshot.Time;
    public double ProfitMin => Lines.Where(l => l.Trade == Trade.Ok).Sum(l => l.Value);
    public double ProfitMax => Lines.Where(l => l.Entry != null).Sum(l => l.Value);
    public int UnreadSlots;
    public int SessionRuns;
    public double SessionProfitMin, SessionProfitMax;
    public TimeSpan SessionTime;
}

internal static class Valuation
{
    public static Trade TradeOf(PriceEntry? e, AppSettings s) => e switch
    {
        null => Trade.NoPrice,
        { IsAggregate: true } => Trade.Random,
        { IsUnique: true } => e.Volume < s.LowListings ? Trade.FewListings : Trade.Ok,
        _ when e.Volume < s.VeryLowTradeVolume => Trade.VeryLow,
        _ when e.Volume < s.LowTradeVolume => Trade.Low,
        _ => Trade.Ok,
    };

    public static string Text(Trade t) => t switch
    {
        Trade.Low => "low trade",
        Trade.VeryLow => "very low trade",
        Trade.FewListings => "few listings",
        Trade.Random => "median price",
        Trade.NoPrice => "no price",
        _ => "",
    };

    public static PriceEntry? Price(ItemMatcher matcher, InventoryItem item)
    {
        var entries = ItemText.Lookup(matcher, item.ToParsed());
        return entries is { Count: > 0 } ? ItemMatcher.PickPrimary(entries) : null;
    }

    public static SnapshotValue Value(InventorySnapshot snapshot, ItemMatcher matcher, AppSettings s)
    {
        var items = snapshot.Items.GroupBy(i => i.Key).Select(g =>
        {
            var first = g.First();
            var entry = Price(matcher, first);
            return new ValuedItem
            {
                Key = g.Key,
                Name = first.Name,
                Detail = first.BaseType ?? (entry == null ? first.ItemClass : null),
                Qty = g.Sum(i => i.Stack),
                Entry = entry,
                Trade = TradeOf(entry, s),
            };
        });
        return new SnapshotValue
        {
            Snapshot = snapshot,
            Items = items.OrderBy(i => i.Entry == null).ThenByDescending(i => i.Total).ThenBy(i => i.Name).ToList(),
        };
    }

    public static List<DiffLine> Diff(SnapshotValue before, SnapshotValue after)
    {
        var a = before.Items.ToDictionary(i => i.Key);
        var b = after.Items.ToDictionary(i => i.Key);
        return a.Keys.Union(b.Keys)
            .Select(key =>
            {
                a.TryGetValue(key, out var x);
                b.TryGetValue(key, out var y);
                var any = (y ?? x)!;
                return new DiffLine
                {
                    Name = any.Name,
                    Detail = any.Detail,
                    Before = x?.Qty ?? 0,
                    After = y?.Qty ?? 0,
                    Entry = any.Entry,
                    Trade = any.Trade,
                };
            })
            .Where(l => l.Delta != 0)
            .OrderBy(l => l.Entry == null)
            .ThenByDescending(l => Math.Abs(l.Value))
            .ThenBy(l => l.Name)
            .ToList();
    }
}

/// <summary>Ctrl+I snapshot vs Ctrl+O snapshot, both valued at the same poe.ninja prices.</summary>
internal sealed class RunTracker
{
    static string CurrentPath => Path.Combine(AppSettings.Dir, "current-run.json");

    readonly List<RunReport> _session = new();
    readonly bool _persist;

    public InventorySnapshot? Start { get; private set; }
    public RunReport? LastReport { get; private set; }

    /// <param name="persist">false = keep everything in memory (self-test), never touch the user's run files.</param>
    public RunTracker(bool persist = true)
    {
        _persist = persist;
        if (!persist) return;

        // Survive an app restart in the middle of a run.
        try
        {
            if (File.Exists(CurrentPath) &&
                JsonSerializer.Deserialize<InventorySnapshot>(File.ReadAllText(CurrentPath)) is { } snap &&
                DateTime.Now - snap.Time < TimeSpan.FromHours(12))
                Start = snap;
        }
        catch (Exception ex)
        {
            Log.Write("current run load failed: " + ex.Message);
        }
    }

    public int SessionRuns => _session.Count;

    public void Begin(InventorySnapshot snapshot)
    {
        Start = snapshot;
        LastReport = null;
        if (!_persist) return;
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllText(CurrentPath, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex)
        {
            Log.Write("current run save failed: " + ex.Message);
        }
    }

    public void ResetSession() => _session.Clear();

    /// <summary>
    /// Compare with the Ctrl+I snapshot. The start is kept, so pressing Ctrl+O again later just updates
    /// the same run (in the session and in runs.csv) instead of adding a new one.
    /// </summary>
    public RunReport Finish(InventorySnapshot end, ItemMatcher matcher, AppSettings s)
    {
        var start = Start ?? throw new InvalidOperationException("no run started");
        var report = new RunReport
        {
            Start = Valuation.Value(start, matcher, s),
            End = Valuation.Value(end, matcher, s),
            UnreadSlots = start.UnreadCells + end.UnreadCells,
        };
        report.Lines = Valuation.Diff(report.Start, report.End);

        var existing = _session.FindIndex(r => r.Start.Snapshot.Time == start.Time);
        if (existing >= 0)
        {
            report.Number = _session[existing].Number;
            _session[existing] = report;
        }
        else
        {
            report.Number = _session.Count == 0 ? 1 : _session.Max(r => r.Number) + 1;
            _session.Add(report);
        }
        report.SessionRuns = _session.Count;
        report.SessionProfitMin = _session.Sum(r => r.ProfitMin);
        report.SessionProfitMax = _session.Sum(r => r.ProfitMax);
        report.SessionTime = TimeSpan.FromTicks(_session.Sum(r => r.Duration.Ticks));

        LastReport = report;
        return report;
    }

}

/// <summary>A run as stored in the history (and as shown in the Difference tab).</summary>
public sealed class SavedRun
{
    public int Number { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string League { get; set; } = "";
    public double ChaosPerDivine { get; set; }
    public double ProfitMin { get; set; }
    public double ProfitMax { get; set; }
    public double StartMin { get; set; }
    public double StartMax { get; set; }
    public double EndMin { get; set; }
    public double EndMax { get; set; }
    public int UnreadSlots { get; set; }
    public List<SavedLine> Lines { get; set; } = new();

    public TimeSpan Duration => End - Start;

    public static SavedRun From(RunReport r, PriceDb db) => new()
    {
        Number = r.Number,
        Start = r.Start.Snapshot.Time,
        End = r.End.Snapshot.Time,
        League = db.League,
        ChaosPerDivine = db.ChaosPerDivine,
        ProfitMin = r.ProfitMin,
        ProfitMax = r.ProfitMax,
        StartMin = r.Start.Min,
        StartMax = r.Start.Max,
        EndMin = r.End.Min,
        EndMax = r.End.Max,
        UnreadSlots = r.UnreadSlots,
        Lines = r.Lines.Select(l => new SavedLine
        {
            Name = l.Name, Detail = l.Detail, Before = l.Before, After = l.After, Unit = l.Entry?.Divine, Trade = l.Trade,
        }).ToList(),
    };
}

public sealed class SavedLine
{
    public string Name { get; set; } = "";
    public string? Detail { get; set; }
    public int Before { get; set; }
    public int After { get; set; }
    /// <summary>Price of one, in divines; null = no price.</summary>
    public double? Unit { get; set; }
    public Trade Trade { get; set; }
    public int Delta => After - Before;
    public double Value => (Unit ?? 0) * Delta;
}

/// <summary>Saved runs: one JSON per run in %APPDATA%\poe2verlay\runs, mirrored into runs.csv for spreadsheets.</summary>
internal static class RunStore
{
    const string CsvHeader = "start,end,league,minutes,profit_min_div,profit_max_div,profit_max_chaos,start_min_div,start_max_div,end_min_div,end_max_div,top_changes";

    static string Dir => Path.Combine(AppSettings.Dir, "runs");
    public static string CsvPath => Path.Combine(AppSettings.Dir, "runs.csv");

    static string FileFor(SavedRun run) => Path.Combine(Dir, run.Start.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".json");

    /// <summary>Saving the same run again (same Ctrl+I) overwrites it.</summary>
    public static void Save(SavedRun run)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FileFor(run), JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
        WriteCsv();
    }

    public static void Delete(SavedRun run)
    {
        var path = FileFor(run);
        if (File.Exists(path)) File.Delete(path);
        WriteCsv();
    }

    public static List<SavedRun> LoadAll()
    {
        var runs = new List<SavedRun>();
        if (!Directory.Exists(Dir)) return runs;
        foreach (var file in Directory.GetFiles(Dir, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<SavedRun>(File.ReadAllText(file)) is { } run) runs.Add(run);
            }
            catch (Exception ex)
            {
                Log.Write($"run file {Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return runs.OrderByDescending(r => r.Start).ToList();
    }

    static void WriteCsv()
    {
        try
        {
            if (File.Exists(CsvPath) && File.ReadLines(CsvPath).FirstOrDefault() != CsvHeader)
                File.Move(CsvPath, Path.Combine(AppSettings.Dir, $"runs-old-{DateTime.Now:yyyyMMdd-HHmmss}.csv"));

            var inv = CultureInfo.InvariantCulture;
            var lines = new List<string> { CsvHeader };
            foreach (var r in LoadAll().OrderBy(r => r.Start))
            {
                var top = string.Join("; ", r.Lines.Where(l => l.Unit != null).Take(6).Select(l => $"{l.Name} {l.Delta:+0;-0}"));
                lines.Add(string.Join(",",
                    r.Start.ToString("yyyy-MM-dd HH:mm:ss", inv),
                    r.End.ToString("yyyy-MM-dd HH:mm:ss", inv),
                    Csv(r.League),
                    r.Duration.TotalMinutes.ToString("0.0", inv),
                    r.ProfitMin.ToString("0.####", inv),
                    r.ProfitMax.ToString("0.####", inv),
                    (r.ProfitMax * r.ChaosPerDivine).ToString("0.##", inv),
                    r.StartMin.ToString("0.####", inv),
                    r.StartMax.ToString("0.####", inv),
                    r.EndMin.ToString("0.####", inv),
                    r.EndMax.ToString("0.####", inv),
                    Csv(top)));
            }
            File.WriteAllLines(CsvPath, lines);
        }
        catch (Exception ex)
        {
            Log.Write("runs.csv write failed: " + ex.Message);
        }
    }

    static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}
