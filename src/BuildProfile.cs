using System.Text.Json;
using System.Text.RegularExpressions;

namespace Poe2Verlay;

/// <summary>
/// What a character values, read from its poe.ninja profile (the character link in the settings): a minion build,
/// Chaos Inoculation, energy shield, rarity stacking, which resistances are under the cap, and the stats on its gear.
/// Trade price checks use it to decide which stats of an item get searched.
/// </summary>
internal sealed partial class BuildProfile
{
    public string Character = "";
    public bool Minion, ChaosInoculation, EnergyShield, Rarity;
    public readonly HashSet<string> UnderCap = new();
    /// <summary>Stats on the equipped items, in trade wording ("# to Level of all Minion Skills").</summary>
    public readonly HashSet<string> GearStats = new(StringComparer.Ordinal);

    public string Summary => string.Join(" · ", new[]
    {
        Minion ? "minions" : null, ChaosInoculation ? "CI" : null, EnergyShield ? "ES" : null, Rarity ? "rarity" : null,
        UnderCap.Count > 0 ? string.Join("/", UnderCap) + " res under cap" : null,
    }.Where(x => x != null));

    public static bool IsLink(string text) => LinkRegex().IsMatch(text);

    public static async Task<BuildProfile> LoadAsync(string link)
    {
        // https://poe.ninja/poe2/profile/{account}/{league}/character/{name}
        var m = LinkRegex().Match(link);
        if (!m.Success) throw new InvalidOperationException("not a poe.ninja character link");
        var (account, league, name) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(PriceService.UserAgent);

        // The profile page asks an event stream for the newest snapshot of the character, then loads that snapshot.
        var version = -1;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        using (var response = await http.GetAsync($"https://poe.ninja/poe2/api/events/character/{account}/{league}/{name}",
                   HttpCompletionOption.ResponseHeadersRead, timeout.Token))
        {
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                using var data = JsonDocument.Parse(line[5..]);
                if (!data.RootElement.TryGetProperty("version", out var v)) continue;
                version = v.GetInt32();
                break;
            }
        }
        if (version < 0) throw new InvalidOperationException("poe.ninja has no snapshot of this character");

        var json = await http.GetStringAsync($"https://poe.ninja/poe2/api/profile/characters/{account}/{league}/{name}/model/{version}");
        return Parse(json, Uri.UnescapeDataString(name));
    }

    public static BuildProfile Parse(string json, string name)
    {
        using var doc = JsonDocument.Parse(json);
        var model = doc.RootElement.GetProperty("charModel");
        var p = new BuildProfile { Character = name };

        var stats = model.GetProperty("defensiveStats");
        double Stat(string key) => stats.TryGetProperty(key, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0;
        p.ChaosInoculation = Names(model, "keystones").Contains("Chaos Inoculation");
        p.EnergyShield = Stat("energyShield") > Math.Max(1000, Stat("life"));
        p.Rarity = Stat("itemRarity") >= 50;
        foreach (var res in new[] { "fire", "cold", "lightning" })
            if (Stat(res + "Resistance") < Stat(res + "ResistanceMax")) p.UnderCap.Add(res);

        var minionMods = 0;
        foreach (var item in model.GetProperty("items").EnumerateArray())
        {
            if (!item.TryGetProperty("itemData", out var data)) continue;
            foreach (var list in new[] { "implicitMods", "explicitMods", "desecratedMods", "fracturedMods", "craftedMods" })
            {
                if (!data.TryGetProperty(list, out var mods) || mods.ValueKind != JsonValueKind.Array) continue;
                foreach (var mod in mods.EnumerateArray())
                {
                    var text = MarkupRegex().Replace(mod.GetString() ?? "", "$1");
                    if (text.Contains("Minion", StringComparison.Ordinal)) minionMods++;
                    p.GearStats.Add(TradeItem.Normalize(text).Key);
                }
            }
        }

        var gems = new List<string>();
        if (model.TryGetProperty("skills", out var skills))
            foreach (var skill in skills.EnumerateArray())
                if (skill.TryGetProperty("allGems", out var all))
                    gems.AddRange(all.EnumerateArray().Select(g => g.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""));
        p.Minion = minionMods >= 3 || gems.Count(g => MinionGemRegex().IsMatch(g)) >= 2;
        return p;
    }

    static HashSet<string> Names(JsonElement model, string list) =>
        model.TryGetProperty(list, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(e => e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").ToHashSet()
            : new HashSet<string>();

    [GeneratedRegex(@"poe\.ninja/poe2/profile/([^/\s]+)/([^/\s]+)/character/([^/?#\s]+)")]
    private static partial Regex LinkRegex();

    /// <summary>"[ItemRarity|Rarity of Items]" -> "Rarity of Items", "[Intelligence]" -> "Intelligence".</summary>
    [GeneratedRegex(@"\[(?:[^\]|]*\|)?([^\]|]*)\]")]
    private static partial Regex MarkupRegex();

    [GeneratedRegex(@"Skeletal|Summon|Raise|Minion|Wolf Pack|Spectre|Zombie|Navira|Kelari|Varashta")]
    private static partial Regex MinionGemRegex();
}
