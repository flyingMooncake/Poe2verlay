using System.Text.Json;
using System.Text.RegularExpressions;

namespace Poe2Verlay;

/// <summary>What a modifier is good for. Combos only count between mods that serve the same kind of build.</summary>
internal enum ModKind
{
    /// <summary>Life, energy shield, resistances, rarity, movement speed, spirit...: good on any build.</summary>
    Universal,
    Minion,
    Spell,
    Attack,
    /// <summary>The player's own crit / generic damage: counts for spell and attack builds, not for minions.</summary>
    PlayerDamage,
}

internal sealed record GradedMod(TradeMod Mod, ModKind Kind, double Weight, double Score);

internal sealed class ItemGrade
{
    /// <summary>S, A, B or C; empty = garbage (nothing valuable for this kind of item).</summary>
    public string Grade = "";
    public double Score;
    /// <summary>The build the item is for: minion, spell, attack, or "defence / utility".</summary>
    public string Type = "";
    public readonly List<GradedMod> Valuable = new();
    /// <summary>Good-looking mods that don't fit the item ("Adds Lightning damage to Attacks" on a sceptre) or its build.</summary>
    public readonly List<TradeMod> Ignored = new();
    /// <summary>Why the grade is capped ("no +minion levels" on a sceptre), or null.</summary>
    public string? Missing;

    public bool Garbage => Valuable.Count == 0;

    static int Rank(string grade) => grade switch { "S" => 4, "A" => 3, "B" => 2, "C" => 1, _ => 0 };
    public static bool AtLeast(string grade, string min) => Rank(grade) >= Rank(string.IsNullOrEmpty(min) ? "A" : min);
}

/// <summary>
/// Rates a magic / rare item by what the market pays for: each mod has an importance (skill levels and movement speed
/// on top, then damage, spirit, crit, speed, life / ES, defences, rarity, resistances; attributes and leech at the
/// bottom) scaled by how close its roll is to the best roll of that mod on that kind of item (game data from Path of
/// Building, assets/mod-max.json); the tier from Ctrl+Alt+C only when the mod isn't in the table. Resistances count as
/// one total. Only mods that fit the item count (a sceptre is a minion / aura weapon,
/// a wand a spell weapon, a bow an attack weapon), and on items that can go any way (rings, gloves...) only the
/// dominant kind plus the universal mods: +minion levels next to cast speed is not a combo. Two or three strong mods
/// of the same kind are worth more than the sum of their parts.
/// </summary>
internal static partial class ItemGrader
{
    /// <summary>Importance per mod, first match wins (0..10).</summary>
    static readonly (Regex Pattern, double Weight)[] Importance =
    {
        (new(@"Level of all .*(Skills|Gems)"), 12),
        (new(@"^Minions have .*(Movement Speed|Area of Effect|Immobilisation|Evasion|Armour|Accuracy)|^Offering|Revive"), 4),
        (new(@"Movement Speed"), 9),
        (new(@"Command Skills"), 10),
        (new(@"^Minions deal #% increased Damage|^Allies in your Presence deal #% increased Damage$"), 9),
        (new(@"Puppet Master"), 8),
        (new(@"Spirit"), 8),
        (new(@"Spell Damage|Damage as Extra|increased (Fire|Cold|Lightning|Chaos|Elemental|Physical) Damage"), 7),
        (new(@"Critical"), 7),
        (new(@"increased (Cast|Attack|Skill) Speed"), 7),
        (new(@"^Minions have .*(Life|Resistance)|^Allies in your Presence have .*(Life|Resistance)|Regenerate"), 5),
        (new(@"maximum (Life|Energy Shield)$|increased maximum (Life|Energy Shield)"), 7),
        (new(@"increased (Global )?(Energy Shield|Armour|Evasion Rating)$|increased (Global )?Armour and|increased (Global )?Evasion and|increased (Global )?Armour, Evasion"), 6),
        (new(@"Adds # to # .*Damage"), 6),
        (new(@"Cooldown Recovery"), 6),
        (new(@"Rarity of Items"), 6),
        (new(@"Chaos Resistance"), 5),
        (new(@"Resistance"), 6),
        (new(@"^Minions |Allies in your Presence"), 5),
        (new(@"Strength|Dexterity|Intelligence|all Attributes"), 3),
        (new(@"Mana|Regenerat|Leech|Accuracy|Stun|Light Radius|Thorns|Gold|Killed|Charm|Flask|Rally|Recoup|Attribute Requirements"), 2),
    };

    [GeneratedRegex(@"Minion|Allies in your Presence|Command Skill|Puppet Master|Offering|Companion|Reviv")]
    private static partial Regex MinionRegex();

    [GeneratedRegex(@"Attack|Accuracy|Projectile|Melee|Bow|Crossbow|Arrow|Bolt|Weapon Range|Strike|Physical Damage$|Spear|Quarterstaff|Mace|Axe|Sword|Dagger|Claw|Flail|Talisman|Block")]
    private static partial Regex AttackRegex();

    [GeneratedRegex(@"Spell|Cast Speed|Infusion|Totem")]
    private static partial Regex SpellRegex();

    /// <summary>Damage of the player's own hits, spells or attacks alike.</summary>
    [GeneratedRegex(@"Critical|increased (Elemental |Fire |Cold |Lightning |Chaos )?Damage$|Damage as Extra|Penetrat|Exposure|Ailment|Ignite|Shock|Chill|Freeze|Bleed|Poison")]
    private static partial Regex PlayerDamageRegex();

    public static ModKind KindOf(string key)
    {
        if (MinionRegex().IsMatch(key)) return ModKind.Minion;
        if (AttackRegex().IsMatch(key)) return ModKind.Attack;
        if (SpellRegex().IsMatch(key)) return ModKind.Spell;
        if (PlayerDamageRegex().IsMatch(key)) return ModKind.PlayerDamage;
        return ModKind.Universal;
    }

    public static double Weight(string key)
    {
        foreach (var (pattern, weight) in Importance)
            if (pattern.IsMatch(key)) return weight;
        return 3;
    }

    /// <summary>What the item is built for: the kinds of mods that count on it.</summary>
    static ModKind[] Allowed(string category) => category switch
    {
        "weapon.sceptre" => new[] { ModKind.Minion, ModKind.Universal },
        "weapon.wand" or "weapon.staff" => new[] { ModKind.Spell, ModKind.PlayerDamage, ModKind.Universal },
        "armour.focus" => new[] { ModKind.Spell, ModKind.Minion, ModKind.PlayerDamage, ModKind.Universal },
        _ when category.StartsWith("weapon.", StringComparison.Ordinal) || category == "armour.quiver"
            => new[] { ModKind.Attack, ModKind.PlayerDamage, ModKind.Universal },
        _ => new[] { ModKind.Minion, ModKind.Spell, ModKind.Attack, ModKind.PlayerDamage, ModKind.Universal },
    };

    static Dictionary<string, Dictionary<string, double>>? _best;

    /// <summary>Best roll of each mod per trade category ("accessory.belt" -> "# to Level of all Minion Skills" -> 2).</summary>
    static Dictionary<string, Dictionary<string, double>> Best => _best ??= LoadBest();

    static Dictionary<string, Dictionary<string, double>> LoadBest()
    {
        try
        {
            using var stream = typeof(ItemGrader).Assembly.GetManifestResourceStream("mod-max.json");
            if (stream != null) return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(stream) ?? new();
        }
        catch (Exception ex)
        {
            Log.Write("mod-max.json: " + ex.Message);
        }
        return new();
    }

    static double? BestRoll(string category, string key) =>
        Best.TryGetValue(category, out var mods) && mods.TryGetValue(key, out var best) && best > 0 ? best : null;

    /// <summary>
    /// How good a roll is, 1 = the best roll of that mod on this kind of item (a sanctified roll can go over).
    /// Not in the table: from the Ctrl+Alt+C tier (T1 = the best), or an average guess.
    /// </summary>
    public static double Quality(string category, TradeMod mod)
    {
        if (BestRoll(category, mod.Key) is { } best) return Math.Min(1.2, mod.Values[0] / best);
        return mod.Tier switch { 1 => 0.95, 2 => 0.8, 3 => 0.65, null => 0.6, _ => 0.4 };
    }

    static double Factor(double quality) => quality switch
    {
        >= 0.9 => 1.0,
        >= 0.75 => 0.85,
        >= 0.6 => 0.7,
        >= 0.45 => 0.5,
        _ => 0.3,
    };

    [GeneratedRegex(@"^#% to ((?:all Elemental|Fire|Cold|Lightning|Chaos)(?: and (?:Fire|Cold|Lightning|Chaos))?) Resistances?$")]
    private static partial Regex ResistanceRegex();

    /// <summary>Universal mods that buyers chase on their own; life, ES and resistances only add to an item.</summary>
    static bool KeyUniversal(string key) =>
        key.Contains("Movement Speed", StringComparison.Ordinal) || key.Contains("Spirit", StringComparison.Ordinal) ||
        key.Contains("Rarity of Items", StringComparison.Ordinal);

    /// <summary>The mod an item of this kind is bought for; without it the item is at most a B.</summary>
    static Regex? Essential(string category) => category switch
    {
        "weapon.sceptre" => new(@"Level of all Minion Skills"),
        "weapon.wand" or "weapon.staff" => new(@"Level of all .*Spell"),
        "armour.boots" => new(@"Movement Speed"),
        _ => null,
    };

    /// <summary>Magic and rare gear and jewels; not uniques, gems, waystones, flasks...</summary>
    public static bool Applies(TradeItem item) =>
        item.Rarity is "Rare" or "Magic" && !item.IsGem &&
        TradeCategories.Of(item.ItemClass) is { Length: > 0 } c && !c.StartsWith("map.", StringComparison.Ordinal) &&
        !c.StartsWith("flask.", StringComparison.Ordinal) && c != "sanctum.relic";

    public static ItemGrade Grade(TradeItem item)
    {
        var grade = new ItemGrade();
        var category = item.Category;
        var allowed = Allowed(category);
        var candidates = new List<GradedMod>();
        var totalRes = 0.0;
        foreach (var mod in item.Mods)
        {
            if (mod.Group is "rune" or "enchant" or "implicit" || mod.Values.Length == 0 || mod.Values[0] < 0) continue;

            // Resistances are worth their total, not one by one.
            if (ResistanceRegex().Match(mod.Key) is { Success: true } res)
            {
                var kinds = res.Groups[1].Value;
                totalRes += mod.Values[0] * (kinds.Contains("all Elemental", StringComparison.Ordinal) ? 3 : kinds.Contains(" and ", StringComparison.Ordinal) ? 2 : 1);
                continue;
            }

            var weight = Weight(mod.Key);
            var quality = Quality(category, mod);
            if (weight < 6 || quality < 0.6) continue; // not something buyers pay for, or a low roll of it
            var kind = KindOf(mod.Key);
            if (!allowed.Contains(kind))
            {
                grade.Ignored.Add(mod);
                continue;
            }
            var score = weight * Factor(quality) * (kind == ModKind.Universal && !KeyUniversal(mod.Key) ? 0.6 : 1.0);
            candidates.Add(new GradedMod(mod, kind, weight, score));
        }

        // Total resistance: two best single resistances of this kind of item count as a full roll.
        if (totalRes > 0)
        {
            var single = new[] { "Fire", "Cold", "Lightning" }.Select(e => BestRoll(category, $"#% to {e} Resistance") ?? 0).DefaultIfEmpty(0).Max();
            var quality = single > 0 ? Math.Min(1.2, totalRes / (2 * single)) : 0.6;
            if (quality >= 0.6)
            {
                var total = new TradeMod($"+{Fmt.Number(totalRes)}% total Resistance", "+#% total Resistance", new[] { totalRes }, TotalResistanceGroup);
                candidates.Add(new GradedMod(total, ModKind.Universal, 6, 6 * Factor(quality) * 0.6));
            }
        }

        // The build the item serves: the kind with the most value. The others don't combine with it.
        var dominant = new[] { ModKind.Minion, ModKind.Spell, ModKind.Attack }
            .Select(k => (Kind: k, Sum: candidates.Where(c => c.Kind == k || (k != ModKind.Minion && c.Kind == ModKind.PlayerDamage)).Sum(c => c.Score)))
            .Where(x => candidates.Any(c => c.Kind == x.Kind))
            .OrderByDescending(x => x.Sum)
            .Select(x => (ModKind?)x.Kind)
            .FirstOrDefault();
        foreach (var c in candidates)
        {
            var fits = c.Kind == ModKind.Universal || c.Kind == dominant ||
                       (c.Kind == ModKind.PlayerDamage && dominant is ModKind.Spell or ModKind.Attack or null);
            if (fits) grade.Valuable.Add(c);
            else grade.Ignored.Add(c.Mod);
        }
        grade.Type = dominant switch
        {
            ModKind.Minion => "minion",
            ModKind.Spell => "spell",
            ModKind.Attack => "attack",
            _ => grade.Valuable.Any(v => v.Kind == ModKind.PlayerDamage) ? "damage" : "defence / utility",
        };
        if (grade.Garbage) return grade;

        // Combos: strong mods of the item's build (life, ES and resistances don't make a combo).
        var strong = grade.Valuable.Count(v => v.Weight >= 7 && (v.Kind != ModKind.Universal || KeyUniversal(v.Mod.Key)));
        var combo = strong >= 3 ? 1.5 : strong == 2 ? 1.25 : 1.0;
        grade.Score = grade.Valuable.Sum(v => v.Score) * combo;
        grade.Grade = grade.Score switch
        {
            >= 30 => "S",
            >= 18 => "A",
            >= 10 => "B",
            _ => "C",
        };
        // One top skill-level roll alone makes an item worth a search.
        if (grade.Grade is "B" or "C" && grade.Valuable.Any(v => v.Weight >= 12 && Quality(category, v.Mod) >= 0.75)) grade.Grade = "A";
        if (Essential(category) is { } essential && !grade.Valuable.Any(v => essential.IsMatch(v.Mod.Key)) && grade.Grade is "S" or "A")
        {
            grade.Grade = "B";
            grade.Missing = category switch
            {
                "weapon.sceptre" => "no +minion levels",
                "armour.boots" => "no movement speed",
                _ => "no +spell levels",
            };
        }
        return grade;
    }

    /// <summary>Group of the made-up "total Resistance" mod: searched as the trade site's pseudo total.</summary>
    public const string TotalResistanceGroup = "pseudo-resistance";
}
