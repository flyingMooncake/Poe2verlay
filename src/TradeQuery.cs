using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Poe2Verlay;

/// <summary>One modifier line of a copied item, in the trade site's wording ("# to maximum Life").</summary>
/// <param name="Tier">From Ctrl+Alt+C ("Tier: 1" = the best); null when the copy has no tiers.</param>
/// <param name="Affix">"Prefix" / "Suffix" from Ctrl+Alt+C, else null.</param>
internal sealed record TradeMod(string Line, string Key, double[] Values, string Group, int? Tier = null, string? Affix = null);

/// <summary>An item as the game copies it (Ctrl+C or Ctrl+Alt+C), split into what a trade search needs.</summary>
internal sealed partial class TradeItem
{
    public string ItemClass = "", Rarity = "", Name = "";
    public string? BaseType;
    public int? ItemLevel, GemLevel, Quality, AreaLevel;
    public string? UltimatumHint;
    public bool Corrupted, Unidentified;
    public double Armour, Evasion, EnergyShield, Spirit, AttacksPerSecond, PhysicalDps, ElementalDps;
    public readonly List<TradeMod> Mods = new();

    static readonly string[] Tags = { "implicit", "rune", "enchant", "crafted", "fractured", "desecrated" };

    public bool IsUnique => Rarity == "Unique";
    public bool IsGem => ItemClass.Contains("Gem", StringComparison.OrdinalIgnoreCase) || Rarity == "Gem";
    public string Category => TradeCategories.Of(ItemClass);
    public bool IsArmourPiece => Category.StartsWith("armour.", StringComparison.Ordinal) && Category != "armour.quiver";

    public static TradeItem? Parse(string text)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).ToList();
        var sections = new List<List<string>> { new() };
        foreach (var line in lines)
        {
            if (line.StartsWith("--------", StringComparison.Ordinal)) sections.Add(new());
            else if (line.Length > 0) sections[^1].Add(line);
        }

        var item = new TradeItem();
        var header = sections[0];
        var r = header.FindIndex(l => l.StartsWith("Rarity:", StringComparison.Ordinal));
        if (r < 0 || r + 1 >= header.Count) return null;
        item.Rarity = header[r]["Rarity:".Length..].Trim();
        item.Name = header[r + 1];
        if (r + 2 < header.Count) item.BaseType = header[r + 2];
        var cls = header.FirstOrDefault(l => l.StartsWith("Item Class:", StringComparison.Ordinal));
        if (cls != null) item.ItemClass = cls["Item Class:".Length..].Trim();

        var physical = 0.0;
        var elemental = 0.0;
        foreach (var section in sections.Skip(1))
        {
            if (section[0].StartsWith("Requirements", StringComparison.Ordinal)) continue;
            // Ctrl+Alt+C: "{ Desecrated Suffix Modifier "of Calamity" (Tier: 3) — Critical }" describes the lines below it.
            string? headerGroup = null, headerAffix = null;
            int? headerTier = null;
            foreach (var raw in section)
            {
                if (raw.StartsWith('{'))
                {
                    var h = ModHeaderRegex().Match(raw);
                    headerGroup = !h.Success ? null : h.Groups[2].Value == "Implicit" || h.Groups[1].Value == "Implicit" ? "implicit"
                        : h.Groups[1].Value is { Length: > 0 } kind ? kind.ToLowerInvariant() : "explicit";
                    headerAffix = h.Success && h.Groups[2].Value is "Prefix" or "Suffix" ? h.Groups[2].Value : null;
                    headerTier = h.Success && int.TryParse(h.Groups[3].Value, out var t) ? t : null;
                    continue;
                }
                var line = RangeRegex().Replace(raw, "$1"); // Ctrl+Alt+C: "+59(50-60)" -> "+59", "+5(4)" -> "+5"
                if (line.StartsWith("Requires", StringComparison.Ordinal) ||
                    line.StartsWith("Grants Skill", StringComparison.Ordinal) || line.StartsWith("Bonded:", StringComparison.Ordinal)) continue;

                switch (line)
                {
                    case "Corrupted" or "Twice Corrupted": item.Corrupted = true; continue;
                    case "Unidentified": item.Unidentified = true; continue;
                    case "Victorious" or "Cowardly" or "Deadly": item.UltimatumHint = line; continue;
                }

                var colon = line.IndexOf(": ", StringComparison.Ordinal);
                if (colon > 0 && Property(item, line[..colon], AugmentedRegex().Replace(line[(colon + 2)..], "").Trim(), ref physical, ref elemental)) continue;
                if (!line.Any(char.IsDigit)) continue; // flavour text, descriptions

                var group = headerGroup ?? "explicit";
                var body = line;
                foreach (var tag in Tags)
                {
                    var suffix = $" ({tag})";
                    if (!body.EndsWith(suffix, StringComparison.Ordinal)) continue;
                    group = tag;
                    body = body[..^suffix.Length];
                    break;
                }
                var (key, values) = Normalize(body);
                item.Mods.Add(new TradeMod(body, key, values, group, headerTier, headerAffix));
            }
        }

        if (item.AttacksPerSecond > 0)
        {
            item.PhysicalDps = physical * item.AttacksPerSecond;
            item.ElementalDps = elemental * item.AttacksPerSecond;
        }
        return item;
    }

    /// <summary>Known "Name: value" lines. False = not a property (could be a mod with a colon).</summary>
    static bool Property(TradeItem item, string name, string value, ref double physical, ref double elemental)
    {
        double Num() => double.TryParse(NumberRegex().Match(value).Value.TrimStart('+'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        switch (name)
        {
            case "Item Level": item.ItemLevel = (int)Num(); return true;
            case "Level": if (item.IsGem) item.GemLevel = (int)Num(); return true;
            case "Quality": item.Quality = (int)Num(); return true;
            case "Area Level": item.AreaLevel = (int)Num(); return true;
            case "Armour": item.Armour = Num(); return true;
            case "Evasion Rating": item.Evasion = Num(); return true;
            case "Energy Shield": item.EnergyShield = Num(); return true;
            case "Spirit": item.Spirit = Num(); return true;
            case "Attacks per Second": item.AttacksPerSecond = Num(); return true;
            case "Physical Damage": physical += AverageOfRanges(value); return true;
            case "Fire Damage" or "Cold Damage" or "Lightning Damage" or "Elemental Damage": elemental += AverageOfRanges(value); return true;
            case "Stack Size" or "Sockets" or "Number of Trials" or "Critical Hit Chance" or "Chaos Damage" or "Ward" or "Block chance"
                or "Charm Slots" or "Radius" or "Limited to" or "Reload Time" or "Waystone Tier" or "Rarity" or "Item Class"
                or "Cost & Reservation Multiplier" or "Cast Time" or "Cooldown Time" or "Uses" or "Charges" or "Experience":
                return true;
            default: return false;
        }
    }

    /// <summary>"10-20, 5-30" -> 15 + 17.5.</summary>
    static double AverageOfRanges(string value) => DamageRangeRegex().Matches(value).Sum(m =>
        (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) / 2);

    /// <summary>
    /// "+59 to maximum Energy Shield" -> ("# to maximum Energy Shield", [59]). The trade site drops the + sign and
    /// words a decrease as a negative increase ("10% reduced X" = "#% increased X" with -10).
    /// </summary>
    public static (string Key, double[] Values) Normalize(string line)
    {
        var values = NumberRegex().Matches(line)
            .Select(m => double.Parse(m.Value.TrimStart('+'), NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
        var key = NumberRegex().Replace(line, "#");
        if (key.Contains(" reduced ", StringComparison.Ordinal) || key.Contains(" less ", StringComparison.Ordinal))
        {
            key = key.Replace(" reduced ", " increased ").Replace(" less ", " more ");
            values = values.Select(v => -v).ToArray();
        }
        return (key, values);
    }

    [GeneratedRegex(@"[+-]?\d+(?:\.\d+)?")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\(-?\d+(?:\.\d+)?(?:-\d+(?:\.\d+)?)?\)")]
    private static partial Regex RangeRegex();

    /// <summary>Groups: kind (Desecrated, Crafted, Fractured, Implicit, Unique...), Prefix/Suffix/Implicit, tier.</summary>
    [GeneratedRegex(@"^\{\s*(?:(Desecrated|Crafted|Fractured|Implicit|Unique|Corrupted|Enchant|Rune|Sanctified)\s+)?(Prefix|Suffix|Implicit)?\s*Modifier[^(}]*(?:\(Tier:\s*(\d+)\))?")]
    private static partial Regex ModHeaderRegex();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)-(\d+(?:\.\d+)?)")]
    private static partial Regex DamageRangeRegex();

    [GeneratedRegex(@"\s*\((augmented|unmet)\)")]
    private static partial Regex AugmentedRegex();
}

/// <summary>"Item Class: Body Armours" -> the trade site's category "armour.chest".</summary>
internal static class TradeCategories
{
    static readonly (string Class, string Category)[] Map =
    {
        ("Body Armour", "armour.chest"), ("Helmet", "armour.helmet"), ("Glove", "armour.gloves"), ("Boot", "armour.boots"),
        ("Quiver", "armour.quiver"), ("Shield", "armour.shield"), ("Foci", "armour.focus"), ("Focus", "armour.focus"),
        ("Buckler", "armour.buckler"), ("Amulet", "accessory.amulet"), ("Ring", "accessory.ring"), ("Belt", "accessory.belt"),
        ("Claw", "weapon.claw"), ("Dagger", "weapon.dagger"), ("One Hand Sword", "weapon.onesword"), ("One Hand Axe", "weapon.oneaxe"),
        ("One Hand Mace", "weapon.onemace"), ("Spear", "weapon.spear"), ("Flail", "weapon.flail"), ("Two Hand Sword", "weapon.twosword"),
        ("Two Hand Axe", "weapon.twoaxe"), ("Two Hand Mace", "weapon.twomace"), ("Quartersta", "weapon.warstaff"),
        ("Talisman", "weapon.talisman"), ("Crossbow", "weapon.crossbow"), ("Bow", "weapon.bow"), ("Wand", "weapon.wand"),
        ("Sceptre", "weapon.sceptre"), ("Sta", "weapon.staff"), ("Fishing Rod", "weapon.rod"), ("Support Gem", "gem.supportgem"),
        ("Meta Gem", "gem.metagem"), ("Gem", "gem.activegem"), ("Jewel", "jewel"), ("Life Flask", "flask.life"),
        ("Mana Flask", "flask.mana"), ("Charm", "flask.charm"), ("Waystone", "map.waystone"), ("Logbook", "map.logbook"),
        ("Tablet", "map.tablet"), ("Ultimatum", "map.ultimatum"), ("Breachstone", "map.breachstone"), ("Relic", "sanctum.relic"),
    };

    public static string Of(string itemClass)
    {
        foreach (var (cls, category) in Map)
            if (itemClass.Contains(cls, StringComparison.OrdinalIgnoreCase)) return category;
        return "";
    }
}

/// <summary>The trade site's stat ids by wording, per group (explicit, implicit, pseudo...).</summary>
internal sealed class TradeStats
{
    readonly Dictionary<string, Dictionary<string, string>> _groups = new();

    public int Count => _groups.Values.Sum(g => g.Count);

    public static TradeStats Parse(string json)
    {
        var stats = new TradeStats();
        using var doc = JsonDocument.Parse(json);
        foreach (var group in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            var name = group.GetProperty("id").GetString() ?? "";
            var byText = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in group.GetProperty("entries").EnumerateArray())
            {
                if (e.TryGetProperty("option", out _)) continue; // "Allocates #" and the like: picked from a list
                var text = e.GetProperty("text").GetString();
                var id = e.GetProperty("id").GetString();
                if (text != null && id != null) byText.TryAdd(text, id);
            }
            stats._groups[name] = byText;
        }
        return stats;
    }

    public string? Find(string group, string key) =>
        _groups.TryGetValue(group, out var g) && g.TryGetValue(key, out var id) ? id : null;
}

/// <summary>One stat filter of a search, with how much it matters for the price (and for the build).</summary>
internal sealed record TradeStatFilter(string Id, double Min, double Weight, string Label);

/// <summary>
/// What gets searched for an item. Uniques, gems and bases by name; magic and rare items by their most valuable stats:
/// a base importance per kind of stat (skill levels and damage over attributes and leech), scaled by the build from
/// poe.ninja (minion build: minion stats up, attack stats down; CI: no life; stats already on the build's gear up;
/// resistances up while one is under the cap). The best few become filters at a share of the rolled value.
/// </summary>
internal sealed class TradeQueryPlan
{
    public required TradeItem Item;
    public string? Name, Type, Category, Rarity;
    public readonly Dictionary<string, (double Min, string Label)> Equipment = new();
    public readonly Dictionary<string, JsonNode> TypeFilters = new();
    public readonly Dictionary<string, JsonNode> Misc = new();
    public readonly Dictionary<string, JsonNode> Map = new();
    public List<TradeStatFilter> Stats = new();
    public readonly List<string> Dropped = new();

    public bool CanRelax => Stats.Count > 1;

    /// <summary>Too few results: search again without the least important stat.</summary>
    public void Relax()
    {
        var least = Stats.MinBy(f => f.Weight)!;
        Stats.Remove(least);
        Dropped.Add(least.Label);
    }

    public IEnumerable<string> Describe()
    {
        if (Name != null) yield return Name;
        if (Type != null && Type != Name) yield return Type;
        if (Rarity == "nonunique" && Category != null) yield return "any " + Category.Split('.').Last() + " base";
        foreach (var (_, (_, label)) in Equipment) yield return label;
        foreach (var f in Stats.OrderByDescending(f => f.Weight)) yield return f.Label;
        if (Misc.ContainsKey("gem_level")) yield return $"gem level ≥ {Item.GemLevel}";
        if (TypeFilters.ContainsKey("ilvl")) yield return $"item level ≥ {Item.ItemLevel}";
        if (Map.ContainsKey("ultimatum_hint")) yield return Item.UltimatumHint!;
    }

    public string ToJson(string status)
    {
        var query = new JsonObject { ["status"] = new JsonObject { ["option"] = status } };
        if (Name != null) query["name"] = Name;
        if (Type != null) query["type"] = Type;

        var filters = new JsonObject();
        var typeFilters = new JsonObject();
        if (Category != null) typeFilters["category"] = new JsonObject { ["option"] = Category };
        if (Rarity != null) typeFilters["rarity"] = new JsonObject { ["option"] = Rarity };
        foreach (var (k, v) in TypeFilters) typeFilters[k] = v.DeepClone();
        if (typeFilters.Count > 0) filters["type_filters"] = new JsonObject { ["filters"] = typeFilters };
        if (Equipment.Count > 0)
        {
            var eq = new JsonObject();
            foreach (var (id, (min, _)) in Equipment) eq[id] = new JsonObject { ["min"] = min };
            filters["equipment_filters"] = new JsonObject { ["filters"] = eq };
        }
        if (Misc.Count > 0) filters["misc_filters"] = new JsonObject { ["filters"] = Clone(Misc) };
        if (Map.Count > 0) filters["map_filters"] = new JsonObject { ["filters"] = Clone(Map) };
        query["filters"] = filters;

        var stats = new JsonArray();
        foreach (var f in Stats) stats.Add(new JsonObject { ["id"] = f.Id, ["value"] = new JsonObject { ["min"] = f.Min } });
        query["stats"] = new JsonArray { new JsonObject { ["type"] = "and", ["filters"] = stats } };

        return new JsonObject { ["query"] = query, ["sort"] = new JsonObject { ["price"] = "asc" } }.ToJsonString();
    }

    static JsonObject Clone(Dictionary<string, JsonNode> d)
    {
        var o = new JsonObject();
        foreach (var (k, v) in d) o[k] = v.DeepClone();
        return o;
    }
}

internal static partial class TradeQueryBuilder
{
    /// <summary>Base importance per kind of stat, first match wins (0 = never searched).</summary>
    static readonly (Regex Pattern, double Weight)[] Importance =
    {
        (new(@"Level of all .*(Skills|Gems)"), 10),
        (new(@"^Minions deal #% increased Damage$|^Allies in your Presence deal #% increased Damage$"), 9),
        (new(@"Movement Speed"), 9),
        (new(@"Spirit"), 8),
        (new(@"Puppet Master"), 8),
        (new(@"Critical"), 7),
        (new(@"increased (Cast|Attack|Skill) Speed"), 7),
        (new(@"maximum (Life|Energy Shield)$"), 7),
        (new(@"increased .*Damage"), 7),
        (new(@"Cooldown Recovery|Command Skill"), 6),
        (new(@"Rarity of Items"), 6),
        (new(@"^Minions |Allies in your Presence"), 5),
        (new(@"Adds # to # .*Damage"), 5),
        (new(@"Strength|Dexterity|Intelligence|all Attributes"), 3),
        (new(@"Mana|Regenerat|Leech|Accuracy|Stun|Light Radius|Thorns|Gold|Killed|Charm|Flask|Rally|Recoup"), 2),
    };

    /// <param name="baseOf">Base type in a magic item's name ("Hale Sapphire Ring of X" -> "Sapphire Ring").</param>
    /// <param name="grade">When given, only its valuable mods are searched (and the totals they feed).</param>
    public static TradeQueryPlan? Build(TradeItem item, TradeStats stats, BuildProfile? build, AppSettings s,
        Func<string, string?> baseOf, out string? error, ItemGrade? grade = null)
    {
        error = null;
        var plan = new TradeQueryPlan { Item = item };

        if (item.ItemClass.Contains("Ultimatum", StringComparison.OrdinalIgnoreCase))
        {
            plan.Type = item.Name;
            if (item.UltimatumHint != null) plan.Map["ultimatum_hint"] = new JsonObject { ["option"] = item.UltimatumHint };
            if (item.AreaLevel is { } area) plan.Misc["area_level"] = new JsonObject { ["min"] = area };
            return plan;
        }
        if (item.IsUnique)
        {
            if (item.Unidentified || item.BaseType == null) plan.Type = item.BaseType ?? item.Name;
            else { plan.Name = item.Name; plan.Type = item.BaseType; }
            plan.Rarity = "unique";
            return plan;
        }
        if (item.IsGem)
        {
            plan.Type = item.BaseType ?? item.Name;
            if (item.GemLevel is { } level) plan.Misc["gem_level"] = new JsonObject { ["min"] = level };
            plan.Misc["corrupted"] = new JsonObject { ["option"] = item.Corrupted ? "true" : "false" };
            return plan;
        }

        var category = item.Category;
        var gear = category.Length > 0 && !category.StartsWith("map.", StringComparison.Ordinal) && category != "sanctum.relic";
        if (item.Rarity == "Normal" && gear)
        {
            // A white base: what matters is the base and its item level (crafting).
            plan.Type = item.Name;
            plan.Rarity = "normal";
            if (item.ItemLevel is { } ilvl) plan.TypeFilters["ilvl"] = new JsonObject { ["min"] = ilvl };
            return plan;
        }

        if (gear && category != "jewel")
        {
            plan.Category = category;
            plan.Rarity = "nonunique";
        }
        else
        {
            // Jewels, waystones, tablets...: the base decides what can roll on it.
            plan.Type = item.Rarity == "Magic" ? baseOf(item.Name) ?? item.Name : item.BaseType ?? item.Name;
            if (item.Rarity is "Rare" or "Magic") plan.Rarity = "nonunique";
        }

        var pct = Math.Clamp(s.TradeMinPercent, 50, 100) / 100.0;
        double Floor(double v) => v >= 10 ? Math.Floor(v * pct) : Math.Floor(v * pct * 10) / 10;

        // Graded item: search only what makes it valuable.
        var valuable = grade?.Valuable.ToDictionary(v => v.Mod, v => v.Weight);
        bool Wanted(Func<string, bool> test) => valuable == null || valuable.Keys.Any(m => test(m.Key));

        // Defences and weapon damage: the totals, not the local mods that make them up.
        if (item.IsArmourPiece && Wanted(LocalDefenceRegex().IsMatch))
        {
            if (item.EnergyShield >= 40) plan.Equipment["es"] = (Floor(item.EnergyShield), $"ES ≥ {Floor(item.EnergyShield)}");
            if (item.Armour >= 100) plan.Equipment["ar"] = (Floor(item.Armour), $"armour ≥ {Floor(item.Armour)}");
            if (item.Evasion >= 100) plan.Equipment["ev"] = (Floor(item.Evasion), $"evasion ≥ {Floor(item.Evasion)}");
        }
        if (Wanted(LocalAttackRegex().IsMatch))
        {
            if (item.PhysicalDps >= 20) plan.Equipment["pdps"] = (Floor(item.PhysicalDps), $"pDPS ≥ {Floor(item.PhysicalDps)}");
            if (item.ElementalDps >= 20) plan.Equipment["edps"] = (Floor(item.ElementalDps), $"eDPS ≥ {Floor(item.ElementalDps)}");
        }
        if (item.Spirit >= 1 && category == "weapon.sceptre" && Wanted(k => k.Contains("Spirit", StringComparison.Ordinal)))
            plan.Equipment["spirit"] = (Floor(item.Spirit), $"spirit ≥ {Floor(item.Spirit)}");

        // Small whole numbers (skill levels, +1 projectile...) are searched as they are: 4 levels is not 5.
        double Min(double v) => v <= 10 && v == Math.Floor(v) ? v : Floor(v);

        // The same stat twice (a prefix and a suffix of rarity) counts once, summed.
        var byId = new Dictionary<string, (string Key, double Value, double Weight)>();
        double fire = 0, cold = 0, lightning = 0, chaos = 0, life = 0, es = 0, speed = 0;
        foreach (var mod in item.Mods)
        {
            // Runes and enchants can be changed; implicits come with the base, and the search takes any base.
            if (mod.Group is "rune" or "enchant" or "implicit" || mod.Values.Length == 0) continue;
            var key = mod.Key;
            var v = mod.Values[0];

            // Summed into the total resistance below (a graded item: when its total is what makes it valuable).
            if (ResistanceRegex().Match(key) is { Success: true } res)
            {
                if (valuable != null && !valuable.Keys.Any(m => m.Group == ItemGrader.TotalResistanceGroup)) continue;
                var kinds = res.Groups[1].Value;
                if (kinds.Contains("all Elemental", StringComparison.Ordinal)) { fire += v; cold += v; lightning += v; }
                if (kinds.Contains("Fire", StringComparison.Ordinal)) fire += v;
                if (kinds.Contains("Cold", StringComparison.Ordinal)) cold += v;
                if (kinds.Contains("Lightning", StringComparison.Ordinal)) lightning += v;
                if (kinds.Contains("Chaos", StringComparison.Ordinal)) chaos += v;
                continue;
            }
            if (valuable != null && !valuable.ContainsKey(mod)) continue;
            if (key == "# to maximum Life") { life += v; continue; }
            if (key == "# to maximum Energy Shield" && !item.IsArmourPiece) { es += v; continue; }
            if (key == "#% increased Movement Speed") { speed += v; continue; }

            // Local mods already counted in the defence / damage totals.
            if (item.IsArmourPiece && LocalDefenceRegex().IsMatch(key)) continue;
            if (item.AttacksPerSecond > 0 && LocalAttackRegex().IsMatch(key)) continue;
            if (category == "weapon.sceptre" && key.Contains("Spirit", StringComparison.Ordinal)) continue;
            if (v < 0) continue; // a drawback, not something to search for

            var id = Lookup(stats, mod);
            if (id == null) continue;
            var value = key.Contains("# to #", StringComparison.Ordinal) && mod.Values.Length >= 2 ? (mod.Values[0] + mod.Values[1]) / 2 : v;
            var weight = valuable?[mod] ?? Weight(key, build);
            if (weight <= 0) continue;
            byId[id] = byId.TryGetValue(id, out var seen) ? (key, seen.Value + value, weight) : (key, value, weight);
        }

        var candidates = byId.Select(p => new TradeStatFilter(p.Key, Min(p.Value.Value), p.Value.Weight, Label(p.Value.Key, Min(p.Value.Value)))).ToList();

        // Resistances: one total (elemental and chaos), whichever ones make it up.
        var totalRes = fire + cold + lightning + chaos;
        if (totalRes >= 20)
        {
            var weight = valuable?.FirstOrDefault(p => p.Key.Group == ItemGrader.TotalResistanceGroup).Value is > 0 and var w
                ? w : 5 * (build?.UnderCap.Count > 0 ? 1.6 : 1);
            candidates.Add(new TradeStatFilter("pseudo.pseudo_total_resistance", Floor(totalRes), weight, $"total res ≥ {Floor(totalRes)}%"));
        }
        if (life >= 20 && build?.ChaosInoculation != true)
            candidates.Add(new TradeStatFilter("pseudo.pseudo_total_life", Floor(life), Weight("# to maximum Life", build), $"life ≥ {Floor(life)}"));
        if (es >= 15)
            candidates.Add(new TradeStatFilter("pseudo.pseudo_total_energy_shield", Floor(es), Weight("# to maximum Energy Shield", build), $"ES ≥ {Floor(es)}"));
        if (speed >= 5)
            candidates.Add(new TradeStatFilter("pseudo.pseudo_increased_movement_speed", Floor(speed), 9, $"move speed ≥ {Floor(speed)}%"));

        var max = Math.Clamp(s.TradeMaxStats, 1, 8);
        var chosen = candidates.Where(c => c.Weight >= 4).OrderByDescending(c => c.Weight).Take(max).ToList();
        if (chosen.Count == 0) chosen = candidates.OrderByDescending(c => c.Weight).Take(Math.Min(2, max)).ToList();
        plan.Stats = chosen;

        if (plan.Stats.Count == 0 && plan.Equipment.Count == 0 && plan.Type == null)
        {
            error = "nothing on it the trade site can search for";
            return null;
        }
        return plan;
    }

    /// <summary>
    /// The trade id of a mod: in its own group first, then as an explicit / crafted / desecrated / fractured mod. Most
    /// trade texts drop the + sign ("# to maximum Life"), some keep it ("+# maximum stacks of Puppet Master").
    /// </summary>
    static string? Lookup(TradeStats stats, TradeMod mod)
    {
        var signed = SignedNumberRegex().Replace(mod.Line, m => m.Value.StartsWith('+') ? "+#" : "#");
        foreach (var group in new[] { mod.Group, "explicit", "crafted", "desecrated", "fractured" }.Distinct())
            if ((stats.Find(group, mod.Key) ?? stats.Find(group, signed)) is { } id) return id;
        return null;
    }

    /// <summary>Importance of a stat for the price, scaled by the build when one is set.</summary>
    public static double Weight(string key, BuildProfile? build)
    {
        var weight = 3.0;
        foreach (var (pattern, w) in Importance)
        {
            if (!pattern.IsMatch(key)) continue;
            weight = w;
            break;
        }
        if (build == null) return weight;

        var minionStat = key.Contains("Minion", StringComparison.Ordinal) || key.Contains("Allies in your Presence", StringComparison.Ordinal) ||
                         key.Contains("Command", StringComparison.Ordinal) || key.Contains("Puppet Master", StringComparison.Ordinal);
        if (build.Minion && minionStat) weight *= 1.6;
        else if (build.Minion && key.Contains("Attack", StringComparison.Ordinal)) weight *= 0.4;
        else if (build.Minion && key.Contains("Spell", StringComparison.Ordinal)) weight *= 0.6;
        if (build.ChaosInoculation && key.Contains("maximum Life", StringComparison.Ordinal)) weight = 0;
        if (build.EnergyShield && key.Contains("Energy Shield", StringComparison.Ordinal)) weight *= 1.3;
        if (build.Rarity && key.Contains("Rarity of Items", StringComparison.Ordinal)) weight *= 1.5;
        if (build.GearStats.Contains(key)) weight *= 1.3;
        return weight;
    }

    /// <summary>"Minions deal #% increased Damage" with 32 -> "Minions deal ≥32% increased Damage".</summary>
    static string Label(string key, double min)
    {
        var text = key;
        var at = text.IndexOf('#');
        if (at >= 0) text = text[..at] + "\u2265" + Fmt.Number(min) + text[(at + 1)..];
        text = text.Replace("#", "\u2026");
        return text.Length > 46 ? text[..45] + "…" : text;
    }

    [GeneratedRegex(@"^#% to ((?:all Elemental|Fire|Cold|Lightning|Chaos)(?: and (?:Fire|Cold|Lightning|Chaos))?) Resistances?$")]
    private static partial Regex ResistanceRegex();

    [GeneratedRegex(@"[+-]?\d+(?:\.\d+)?")]
    private static partial Regex SignedNumberRegex();

    [GeneratedRegex(@"Armour|Evasion|Energy Shield|Ward|Block")]
    private static partial Regex LocalDefenceRegex();

    [GeneratedRegex(@"^Adds # to # .*Damage$|^#% increased Physical Damage$|^#% increased Attack Speed$|Critical Hit Chance$|Accuracy Rating$")]
    private static partial Regex LocalAttackRegex();
}

/// <summary>What a trade price check found.</summary>
internal sealed class TradeResult
{
    public string Url = "";
    public int Total;
    /// <summary>The cheapest listings, in divine (null = currency we have no price for).</summary>
    public readonly List<(double? Divine, string Raw)> Prices = new();
    public readonly List<string> Filters = new();
    public readonly List<string> Dropped = new();
}

/// <summary>Price checks on the trade site: build the search, relax it when nothing matches, fetch the cheapest.</summary>
internal sealed class TradeChecker : IDisposable
{
    readonly TradeApi _api = new();
    TradeStats? _stats;
    Dictionary<string, string> _currency = new();
    List<string> _bases = new();
    DateTime _dataLoaded;

    public TradeApi Api => _api;

    static string CacheFile(string name) => Path.Combine(AppSettings.Dir, "cache", $"trade-{name}.json");

    /// <summary>Stat ids and currency names: from the cache, refreshed once a day.</summary>
    async Task EnsureDataAsync()
    {
        if (_stats != null && (DateTime.UtcNow - _dataLoaded).TotalHours < 24) return;
        async Task<string> Load(string name)
        {
            var file = CacheFile(name);
            if (File.Exists(file) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(file)).TotalHours < 24) return File.ReadAllText(file);
            var r = await _api.GetAsync("data", "/api/trade2/data/" + name);
            if (r.Status != 200) throw new TradeException($"trade data '{name}': HTTP {r.Status}");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, r.Body);
            return r.Body;
        }

        _stats = TradeStats.Parse(await Load("stats"));
        var currency = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var doc = JsonDocument.Parse(await Load("static")))
        {
            foreach (var group in doc.RootElement.GetProperty("result").EnumerateArray())
                foreach (var e in group.GetProperty("entries").EnumerateArray())
                    if (e.GetProperty("id").GetString() is { } id && e.GetProperty("text").GetString() is { Length: > 0 } text)
                        currency.TryAdd(id, text);
        }
        _currency = currency;

        var bases = new HashSet<string>(StringComparer.Ordinal);
        using (var doc = JsonDocument.Parse(await Load("items")))
        {
            foreach (var group in doc.RootElement.GetProperty("result").EnumerateArray())
                foreach (var e in group.GetProperty("entries").EnumerateArray())
                    if (!e.TryGetProperty("name", out _) && e.GetProperty("type").GetString() is { Length: > 0 } type) bases.Add(type);
        }
        _bases = bases.OrderByDescending(b => b.Length).ToList(); // longest first: "Time-Lost Sapphire" before "Sapphire"
        _dataLoaded = DateTime.UtcNow;
        Log.Write($"trade data: {_stats.Count} stats, {_currency.Count} currency names, {_bases.Count} bases");
    }

    /// <param name="divineOf">poe.ninja price in divine of a currency, by its full name.</param>
    /// <param name="grade">Graded magic / rare item: search only its valuable mods.</param>
    public async Task<TradeResult> PriceCheckAsync(TradeItem item, string league, BuildProfile? build, AppSettings s, Func<string, double?> divineOf,
        ItemGrade? grade = null)
    {
        await EnsureDataAsync();
        var plan = TradeQueryBuilder.Build(item, _stats!, build, s, BaseOf, out var error, grade) ?? throw new TradeException(error ?? "can't search this item");

        var path = $"/api/trade2/search/poe2/{Uri.EscapeDataString(league)}";
        string searchId;
        List<string> hashes;
        int total;
        while (true)
        {
            var json = plan.ToJson(string.IsNullOrEmpty(s.TradeStatus) ? "available" : s.TradeStatus);
            var r = await _api.PostAsync("search", path, json);
            if (r.Status != 200) throw new TradeException("trade search: " + ErrorText(r));
            using var doc = JsonDocument.Parse(r.Body);
            searchId = doc.RootElement.GetProperty("id").GetString() ?? "";
            hashes = doc.RootElement.GetProperty("result").EnumerateArray().Select(e => e.GetString() ?? "").Where(h => h.Length > 0).ToList();
            total = doc.RootElement.TryGetProperty("total", out var t) ? t.GetInt32() : hashes.Count;
            Log.Write($"trade search {searchId}: {total} results for {json}");
            if (total >= 3 || !plan.CanRelax) break;
            plan.Relax();
        }

        var result = new TradeResult { Url = TradeWeb.SearchUrl(league, searchId), Total = total };
        result.Filters.AddRange(plan.Describe());
        result.Dropped.AddRange(plan.Dropped);
        if (hashes.Count == 0) return result;

        var fetch = await _api.GetAsync("fetch", $"/api/trade2/fetch/{string.Join(',', hashes.Take(10))}?query={searchId}&realm=poe2");
        if (fetch.Status != 200) throw new TradeException("trade fetch: " + ErrorText(fetch));
        using (var doc = JsonDocument.Parse(fetch.Body))
        {
            foreach (var entry in doc.RootElement.GetProperty("result").EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("listing", out var listing) ||
                    !listing.TryGetProperty("price", out var price) || price.ValueKind != JsonValueKind.Object) continue;
                var amount = price.GetProperty("amount").GetDouble();
                var currency = price.GetProperty("currency").GetString() ?? "";
                var name = _currency.TryGetValue(currency, out var n) ? n : currency;
                double? divine = currency == "divine" ? amount : divineOf(name) is { } each ? amount * each : null;
                result.Prices.Add((divine, $"{Fmt.Number(amount)} {currency}"));
            }
        }
        // The site sorts by its own currency rates; shown cheapest first by poe.ninja's.
        result.Prices.Sort((a, b) => (a.Divine ?? double.MaxValue).CompareTo(b.Divine ?? double.MaxValue));
        return result;
    }

    string? BaseOf(string name) => _bases.FirstOrDefault(b => name.Contains(b, StringComparison.Ordinal));

    static string ErrorText(TradeResponse r)
    {
        try
        {
            using var doc = JsonDocument.Parse(r.Body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)) return m.GetString() ?? $"HTTP {r.Status}";
        }
        catch (JsonException)
        {
            // not JSON
        }
        return $"HTTP {r.Status}";
    }

    public void Dispose() => _api.Dispose();
}
