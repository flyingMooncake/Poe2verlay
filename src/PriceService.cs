using System.Net;
using System.Text.Json;

namespace Poe2Verlay;

public sealed class PriceEntry
{
    public string Name { get; set; } = "";
    public string? BaseType { get; set; }
    public string Category { get; set; } = "";
    /// <summary>Price of one item, in divine orbs.</summary>
    public double Divine { get; set; }
    /// <summary>Exchange items: traded volume (in divines). Uniques: number of listings.</summary>
    public double Volume { get; set; }
    public bool IsUnique { get; set; }
    public bool Corrupted { get; set; }
    /// <summary>Uniques: poe.ninja item category ("Ring", "Body Armour", "One Hand Mace", ...).</summary>
    public string? ItemCategory { get; set; }
    /// <summary>Synthetic "Unique Ring" entry: the median price of every unique in that category.</summary>
    public bool IsAggregate { get; set; }
    public double? Change7d { get; set; }
}

public sealed class PriceDb
{
    /// <summary>Bumped when cached files miss data the current code needs.</summary>
    public const int CurrentVersion = 2;
    public int Version { get; set; }
    public string League { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
    public double ChaosPerDivine { get; set; }
    public double ExaltedPerDivine { get; set; }
    public List<PriceEntry> Entries { get; set; } = new();
}

public sealed class PriceService
{
    static readonly string[] ExchangeTypes =
    {
        "Currency", "Fragments", "Abyss", "UncutGems", "LineageSupportGems", "Essences", "SoulCores",
        "Idols", "Runes", "Ritual", "Expedition", "Delirium", "Breach", "Verisium",
    };

    static readonly string[] UniqueTypes =
    {
        "UniqueWeapons", "UniqueArmours", "UniqueAccessories", "UniqueFlasks", "UniqueCharms",
        "UniqueJewels", "UniqueSanctumRelics", "UniqueTablets",
    };

    const string BaseUrl = "https://poe.ninja/poe2/api/economy/";

    readonly HttpClient _http;

    public PriceService()
    {
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("poe2verlay/1.0 (personal PoE2 price overlay)");
    }

    public async Task<List<string>> GetLeaguesAsync()
    {
        var json = await _http.GetStringAsync(BaseUrl + "leagues").ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("id").GetString())
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();
    }

    public async Task<PriceDb> FetchAsync(string league, bool includeUniques)
    {
        var db = new PriceDb { Version = PriceDb.CurrentVersion, League = league, UpdatedUtc = DateTime.UtcNow };
        var lg = Uri.EscapeDataString(league);
        var failures = new List<string>();

        foreach (var type in ExchangeTypes)
        {
            try
            {
                var json = await _http.GetStringAsync($"{BaseUrl}exchange/current/overview?league={lg}&type={type}")
                    .ConfigureAwait(false);
                ParseExchange(json, type, db);
            }
            catch (Exception ex)
            {
                failures.Add(type);
                Log.Write($"poe.ninja {type}: {ex.Message}");
            }
            await Task.Delay(150).ConfigureAwait(false);
        }

        if (includeUniques)
        {
            foreach (var type in UniqueTypes)
            {
                try
                {
                    var json = await _http.GetStringAsync($"{BaseUrl}stash/current/item/overview?league={lg}&type={type}")
                        .ConfigureAwait(false);
                    ParseUniques(json, type, db);
                }
                catch (Exception ex)
                {
                    failures.Add(type);
                    Log.Write($"poe.ninja {type}: {ex.Message}");
                }
                await Task.Delay(150).ConfigureAwait(false);
            }
        }

        if (db.Entries.Count == 0)
            throw new InvalidOperationException($"poe.ninja returned no prices for '{league}'");
        if (db.ChaosPerDivine <= 0)
            throw new InvalidOperationException("poe.ninja response had no chaos/divine rate");
        if (failures.Count > 0)
            Log.Write($"price fetch for {league}: {db.Entries.Count} entries, failed: {string.Join(", ", failures)}");
        return db;
    }

    /// <summary>
    /// Values come in units of core.primary (divine today). rates[x] = amount of x per one primary unit.
    /// Returns the factor that converts a primary-unit value into divines.
    /// </summary>
    static double ReadCore(JsonElement root, PriceDb db)
    {
        if (!root.TryGetProperty("core", out var core)) return 1;
        var primary = core.TryGetProperty("primary", out var p) ? p.GetString() ?? "divine" : "divine";
        var rates = new Dictionary<string, double> { [primary] = 1 };
        if (core.TryGetProperty("rates", out var r) && r.ValueKind == JsonValueKind.Object)
            foreach (var prop in r.EnumerateObject())
                if (prop.Value.TryGetDouble(out var v) && v > 0) rates[prop.Name] = v;

        if (!rates.TryGetValue("divine", out var divinePerPrimary)) return 1;
        if (db.ChaosPerDivine <= 0 && rates.TryGetValue("chaos", out var chaos)) db.ChaosPerDivine = chaos / divinePerPrimary;
        if (db.ExaltedPerDivine <= 0 && rates.TryGetValue("exalted", out var ex)) db.ExaltedPerDivine = ex / divinePerPrimary;
        return divinePerPrimary;
    }

    static void ParseExchange(string json, string type, PriceDb db)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var toDivine = ReadCore(root, db);

        var names = new Dictionary<string, string>();
        if (root.TryGetProperty("items", out var items))
            foreach (var it in items.EnumerateArray())
                if (Str(it, "id") is { } id && Str(it, "name") is { } name) names[id] = name;

        foreach (var line in root.GetProperty("lines").EnumerateArray())
        {
            if (Str(line, "id") is not { } id || !names.TryGetValue(id, out var name)) continue;
            db.Entries.Add(new PriceEntry
            {
                Name = name,
                Category = type,
                Divine = Num(line, "primaryValue") * toDivine,
                Volume = Num(line, "volumePrimaryValue") * toDivine,
                Change7d = Spark(line, "sparkline"),
            });
        }
    }

    static void ParseUniques(string json, string type, PriceDb db)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var toDivine = ReadCore(root, db);

        foreach (var line in root.GetProperty("lines").EnumerateArray())
        {
            if (Str(line, "name") is not { } name) continue;
            db.Entries.Add(new PriceEntry
            {
                Name = name,
                BaseType = Str(line, "baseType"),
                Category = type,
                Divine = Num(line, "primaryValue") * toDivine,
                Volume = Num(line, "listingCount"),
                IsUnique = true,
                ItemCategory = CategoryName(Str(line, "category")),
                Corrupted = line.TryGetProperty("corrupted", out var c) && c.ValueKind == JsonValueKind.True,
                Change7d = Spark(line, "sparkLine"),
            });
        }
    }

    // "[Mace|One Hand Mace]" -> "One Hand Mace", "Ring" -> "Ring"
    static string? CategoryName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var name = raw.Trim('[', ']', ' ').Split('|').Last().Trim();
        return name.Length > 0 ? name : null;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    static double? Spark(JsonElement e, string name) =>
        e.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.Object &&
        s.TryGetProperty("totalChange", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetDouble()
            : null;
}

internal static class PriceCache
{
    static string PathFor(string league)
    {
        var safe = string.Concat(league.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return Path.Combine(AppSettings.Dir, "cache", safe + ".json");
    }

    public static PriceDb? Load(string league)
    {
        try
        {
            var path = PathFor(league);
            if (!File.Exists(path)) return null;
            var db = JsonSerializer.Deserialize<PriceDb>(File.ReadAllText(path));
            return db is { Entries.Count: > 0, ChaosPerDivine: > 0, Version: >= PriceDb.CurrentVersion } ? db : null;
        }
        catch (Exception ex)
        {
            Log.Write("cache load failed: " + ex.Message);
            return null;
        }
    }

    public static void Save(PriceDb db)
    {
        try
        {
            var path = PathFor(db.League);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(db));
        }
        catch (Exception ex)
        {
            Log.Write("cache save failed: " + ex.Message);
        }
    }
}
