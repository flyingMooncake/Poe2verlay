using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Poe2Verlay;

public enum RumourKind { Grand, UniqueMap, Boss }

/// <summary>One island rumour, as listed under "Island Rumours" when hovering uncharted waters with a logbook.</summary>
public sealed class Rumour
{
    /// <summary>The line as the game prints it ("Somethin' fishy...").</summary>
    public string Text = "";
    /// <summary>Other spellings (the rating sheet's), compared after normalising.</summary>
    public string[] Aliases = Array.Empty<string>();
    public RumourKind Kind;
    public string Map = "";
    public string Mods = "";
    public string Rating = "";
    /// <summary>Why it is rated so, known problems.</summary>
    public string? Note;
    /// <summary>Tablet / saga advice for this island.</summary>
    public string? Tip;
    /// <summary>poe.ninja items worth pricing next to it (chase drops, the tablet or saga it wants).</summary>
    public string[] Prices = Array.Empty<string>();
    /// <summary>Islands with a chase drop: what to do, step by step (shown as a "do this" box).</summary>
    public string[] Todo = Array.Empty<string>();
    /// <summary>Special maps tab: why this island is worth knowing about (null = not listed there).</summary>
    public string? Why;
    /// <summary>Special maps tab: where the information comes from.</summary>
    public string? Source;
    /// <summary>Other names the charted island may show on the ocean map besides <see cref="Map"/>.</summary>
    public string[] MapAliases = Array.Empty<string>();

    public bool Special => Todo.Length > 0;

    public double Points => RumourBook.Points(Rating);
    public string KindText => Kind switch { RumourKind.Grand => "GE", RumourKind.UniqueMap => "unique map", _ => "boss" };
    public string ShortText => Text.TrimEnd('.', '\u2026', ' ');

    public Rumour Copy() => (Rumour)MemberwiseClone();
}

/// <summary>
/// What each rumour means and how good it is. Ratings follow Jestra1220's sheet (the one linked from Palsteron's
/// expedition guide), re-downloaded every few hours; notes are from poe.gnejs.app and the guides it credits.
/// </summary>
internal static class RumourBook
{
    public const string DefaultSheet = "https://docs.google.com/spreadsheets/d/16YU8mSS7TdLPdmOunVjiPn_NrKVGfcnMkuMQDy8jgZA";

    static readonly HttpClient Http = CreateHttp();
    static string? _appliedUrl;
    static bool _refreshing;

    static string _sourceName = "built-in (Jestra1220's sheet, Oct 2026)";
    static DateTime? _sourceTimeUtc;

    public static List<Rumour> All { get; private set; } = BuiltIn();

    /// <summary>
    /// Aldur's Saga only drops in the Runes of Aldur league (0.5.5 patch notes); in Forbidden Rites and Standard its
    /// mods roll on Grand Expeditions by themselves, so there is no saga to decide about.
    /// </summary>
    public static bool AldursSagaDrops { get; set; }
    public static string Source => _sourceTimeUtc is { } t ? $"{_sourceName}, {Fmt.Age(t)}" : _sourceName;

    static List<Rumour> BuiltIn() => new()
    {
        // Grand Expeditions
        new() { Text = "Fallen stars...", Kind = RumourKind.Grand, Map = "Moor", Rating = "S+",
            Mods = "Runestones: one huge remnant with 8-10 runes",
            Note = "the old Visions of Paradise trick (run it twice) is gone: since 0.5.5d tablets can't be Ctrl-clicked into Grand Expeditions",
            Prices = new[] { "Perfect Flux" },
            MapAliases = new[] { "Moor of Fallen Skies" },
            Why = "Instead of many small remnants, the whole island is built around one huge one that rolls 8-10 runes, so a single " +
                  "Runeshape pick has far more (and bigger) combinations to choose from. It is the only S+ Grand Expedition on " +
                  "Jestra1220's sheet, and guides report Perfect Flux dropping here.",
            Source = "Jestra1220's sheet (rating), gamerblurb (Perfect Flux), 0.5.5d patch notes (tablets no longer go into Grand Expeditions)",
            Todo = new[]
            {
                "Get to the huge remnant: it rolls 8-10 runes; {scan} on its Runeshape panel shows the best pick",
                "Loot: Perfect Flux can drop",
                "No tablet tricks here any more (0.5.5d): one run per rumour",
            } },
        new() { Text = "Cold as ice...", Kind = RumourKind.Grand, Map = "Frigid Bluffs", Rating = "A+",
            Mods = "old Expedition layout, infinite revives" },
        new() { Text = "Nothin' to drink...", Aliases = new[] { "Nothing to drink" }, Kind = RumourKind.Grand, Map = "Stagnant Basin", Rating = "A",
            Mods = "Oil: oil derricks, big explosions" },
        new() { Text = "Endless cliffs...", Kind = RumourKind.Grand, Map = "Craggy Peninsula", Rating = "A",
            Mods = "rarity + Rogue Exiles, kin totem remnants" },
        new() { Text = "Sulphite!", Kind = RumourKind.Grand, Map = "Scorched Cay", Rating = "A",
            Mods = "remnants with increased rarity" },
        new() { Text = "Unknown ruins...", Kind = RumourKind.Grand, Map = "Exhumed Ruins", Rating = "B",
            Mods = "Precursor Leylines: chart new islands for free",
            Note = "can land where no new area fits, which is why the leylines sometimes don't show up",
            Why = "Completing the Precursor Leylines inside charts new islands around it without using a logbook, so it pays you in " +
                  "free areas (and free rumours to fish through) rather than in loot.",
            Source = "Jestra1220's sheet, poe.gnejs.app, gamerblurb" },
        new() { Text = "Somethin' fishy...", Aliases = new[] { "Something fishy" }, Kind = RumourKind.Grand, Map = "Bleached Shoals", Rating = "B",
            Mods = "gold, all-res amulet remnants" },
        new() { Text = "Warm but risky...", Aliases = new[] { "It's warm", "It's warm but risky" }, Kind = RumourKind.Grand, Map = "Lush Island", Rating = "B", MapAliases = new[] { "Lush Isle" },
            Mods = "experience, Beyond monsters, hoards" },
        new() { Text = "Bleak and awful...", Kind = RumourKind.Grand, Map = "Barren Atoll", Rating = "B",
            Mods = "Strongboxes (gold chest remnants)",
            Note = "had an immortal / invisible enemies bug, said to be fixed in 0.5.4b" },
        new() { Text = "It's dry at least...", Kind = RumourKind.Grand, Map = "Sloughed Gully", Rating = "D",
            Mods = "more monster effectiveness",
            Note = "bad layout: remnant monsters can spawn on cliffs out of reach and brick the map; fewer explosives since 0.5.4" },
        new() { Text = "Wild roaming free...", Aliases = new[] { "Wild, roaming free" }, Kind = RumourKind.Grand, Map = "Grazed Prairie", Rating = "D",
            Mods = "Azmeri spirits",
            Note = "fewer explosives since 0.5.4 hurt this one, avoid for now" },

        // Unique maps
        new() { Text = "Reflective waters...", Kind = RumourKind.UniqueMap, Map = "Lake of Kalandra", Rating = "A", MapAliases = new[] { "The Fractured Lake" },
            Mods = "ring bases, mirrored rare monsters",
            Why = "A unique map full of mirrored rare monsters, rated A for the ring bases it drops. One guide claims a Fragmented Mirror " +
                  "can drop here; nobody else confirms it.",
            Source = "Jestra1220's sheet, gamerblurb (Fragmented Mirror, unconfirmed)" },
        new() { Text = "All that glitters...", Kind = RumourKind.UniqueMap, Map = "Castaway", Rating = "A",
            Mods = "lots of gold: drops turn into gold, buried treasure",
            Why = "A gold map: item drops turn into gold (500-1000% more gold) and there is buried treasure. Gold pays for the " +
                  "Expedition gambles; don't expect items from it.",
            Source = "Jestra1220's sheet, gamerblurb" },
        new() { Text = "Almost paradise.", Kind = RumourKind.UniqueMap, Map = "Untainted Paradise", Rating = "C",
            Mods = "experience, no item drops",
            Why = "200-400% more experience, but nothing drops. Only worth it when you are levelling.",
            Source = "Jestra1220's sheet, gamerblurb" },
        new() { Text = "A good fellow...", Kind = RumourKind.UniqueMap, Map = "Moment of Zen", Rating = "C",
            Mods = "Seer: a travelling unique trader",
            Why = "A travelling merchant (the Seer) with his own stock of uniques. Worth a look when you have currency to spend.",
            Source = "Jestra1220's sheet, gamerblurb" },

        // Bosses
        new() { Text = "Crazed Chieftain...", Kind = RumourKind.Boss, Map = "Jade Isles", Rating = "S+",
            Mods = "not an Expedition boss; can drop Rakiata's Flow",
            Prices = new[] { "Rakiata's Flow", "Visions of Paradise" },
            MapAliases = new[] { "The Jade Isles" },
            Why = "A boss island that is not one of the four Expedition bosses: a Manoki chieftain (guides mention several Manoki fights). " +
                  "Its boss can drop Rakiata's Flow, a lineage support gem and the most valuable single drop you can get from a rumour. " +
                  "No saga forces this island, so the only way in is spotting its rumour: worth trying Visions of Paradise for a second kill.",
            Source = "poe.gnejs.app (S+, Rakiata's Flow), gamerblurb (Manoki fights), Visions of Paradise tested in game on the Secluded Temple only",
            Todo = new[]
            {
                "Try Visions of Paradise first (Ctrl+click into the map device): it works on the Secluded Temple boss island, untested here. If it takes, finishing the island irradiates it instead = a second kill",
                "Kill the chieftain (a Manoki boss, not an Expedition one)",
                "Loot: Rakiata's Flow (lineage support gem) can drop",
            } },
        new() { Text = "Origin of the fall...", Kind = RumourKind.Boss, Map = "Obscure Island", Rating = "A",
            Mods = "Olroth" },
        new() { Text = "Stardrinker...", Kind = RumourKind.Boss, Map = "Secluded Temple", Rating = "A",
            Mods = "Uhtred",
            Note = "Depleted Mana Rune: about 2-3% per kill (community estimate, some needed 40-60 kills), once per character, and a quest item (can't be traded)",
            Tip = "Uhtred's Saga forces this island; Visions of Paradise gives it a second run",
            Prices = new[] { "Runeseeker's Call", "Uhtred's Saga", "Visions of Paradise" },
            Why = "Uhtred's island, one of the four Expedition boss islands. Uhtred can drop the Depleted Mana Rune, the quest item for " +
                  "Runeseeker's Call, the most expensive unique weapon of the league. Two ways to more tries: Visions of Paradise still " +
                  "works on this island (tested in Forbidden Rites after 0.5.5d), so every Secluded Temple is two Uhtred kills; and " +
                  "Uhtred's Saga forces the island onto the next logbook (several boss sagas can be active at once; they replace " +
                  "regular islands before Grand Expeditions).",
            Source = "poe.gnejs.app (Depleted Mana Rune, sagas), Palsteron's \"Secret Rune Quest\" video, mobalytics / note.com (drop rate), poe2wiki (Overseer tablet mods), Visions of Paradise tested in game",
            Todo = new[]
            {
                "Before opening it: Ctrl+click Visions of Paradise into the map device. Finishing the temple then irradiates it instead of completing it: run it again = a second Uhtred kill",
                "Kill Uhtred and pick up the Depleted Mana Rune if it drops: the quest item for Runeseeker's Call",
                "Want more tries? Uhtred's Saga (before the logbook) forces this island",
                "Other slots: an Overseer tablet with more Quantity / Rarity of items from Map Bosses may help Uhtred's other drops (untested); the rune itself is a quest item, don't count on it",
            } },
        new() { Text = "The last to fall...", Aliases = new[] { "Last to fall" }, Kind = RumourKind.Boss, Map = "Mournful Cliffside", Rating = "B",
            Mods = "Vorana" },
        new() { Text = "End of the circle...", Kind = RumourKind.Boss, Map = "Sprawling Jungle", Rating = "B", MapAliases = new[] { "Strange Jungle" },
            Mods = "Medved" },
    };

    /// <summary>Sheet grade to points: S+ 10, S 9, A+ 7.5, A 6.5, B+ 5, B 4, C 2, D 1, F 0.</summary>
    public static double Points(string rating)
    {
        var m = Regex.Match(rating.Trim().ToUpperInvariant(), @"^([SABCDF])\s*([+-]?)");
        if (!m.Success) return 0;
        var letter = m.Groups[1].Value[0];
        var p = letter switch { 'S' => 9, 'A' => 6.5, 'B' => 4, 'C' => 2, 'D' => 1, _ => 0.0 };
        var step = letter switch { 'S' or 'A' or 'B' => 1.0, 'C' => 0.5, 'D' => 0.2, _ => 0 };
        return m.Groups[2].Value switch { "+" => p + step, "-" => p - step / 2, _ => p };
    }

    /// <summary>
    /// The rumour an OCR'd tooltip line names, or null. The rumours are handwritten and OCR mangles them
    /// ("Uwkwoww miws..." for "Unknown ruins..."), so look-alike letters count as near misses and the closest name wins
    /// when it is clearly closer than the next one.
    /// </summary>
    public static Rumour? Find(string text) => Match(All, text, 0.52).Rumour;

    public static (Rumour? Rumour, double Ratio) Match(string text) => Match(All, text, 0.52);

    static (Rumour? Rumour, double Ratio) Match(IEnumerable<Rumour> list, string text, double maxRatio)
    {
        var n = Plain(text);
        if (n.Length < 4) return (null, 1);
        Rumour? best = null;
        double bestRatio = double.MaxValue, second = double.MaxValue;
        foreach (var r in list)
        {
            var ratio = r.Aliases.Prepend(r.Text).Min(name =>
            {
                var a = Plain(name);
                // OCR glued something onto the end of the line.
                var probe = n.Length > a.Length + 2 && n.StartsWith(a + " ", StringComparison.Ordinal) ? a : n;
                return Distance(probe, a) / Math.Max(probe.Length, a.Length);
            });
            if (ratio < bestRatio)
            {
                second = bestRatio;
                bestRatio = ratio;
                best = r;
            }
            else if (ratio < second)
            {
                second = ratio;
            }
        }
        return bestRatio <= maxRatio && (bestRatio <= 0.15 || second - bestRatio >= 0.15) ? (best, bestRatio) : (null, bestRatio);
    }

    /// <summary>Letters the handwriting makes OCR swap for each other.</summary>
    static readonly string[] LookAlike = { "nwumvrh", "iltj1", "oaecdq0", "s5", "kxy", "bh", "g9q", "fp" };

    /// <summary>Edit distance where swapping look-alike letters costs 0.4.</summary>
    static double Distance(string a, string b)
    {
        var prev = new double[b.Length + 1];
        var cur = new double[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var x = a[i - 1];
                var y = b[j - 1];
                var swap = x == y ? 0 : x == ' ' || y == ' ' ? 1 : LookAlike.Any(g => g.Contains(x) && g.Contains(y)) ? 0.4 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + swap);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>A charted island, from a line of its tooltip that is just its name ("Frigid Bluffs", "Secluded Temple").</summary>
    public static (Rumour Rumour, RectangleF Bounds)? FindIsland(IReadOnlyList<TextSegment> segments)
    {
        foreach (var s in segments.Where(s => s.Words.Count <= 5))
        {
            var n = Plain(s.Text);
            if (n.Length < 4) continue;
            foreach (var r in All)
            foreach (var name in r.MapAliases.Prepend(r.Map))
            {
                var a = Plain(name);
                if (n == a || a.Length >= 8 && Distance(n, a) <= 1.2) return (r, s.Bounds);
            }
        }
        return null;
    }

    /// <summary>Normalised and without accents (OCR reads the handwriting as "flhå").</summary>
    static string Plain(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return ItemMatcher.Normalize(sb.ToString());
    }

    // ---------------------------------------------------------------- rating sheet

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("poe2verlay/1.0 (personal PoE2 price overlay)");
        return http;
    }

    /// <summary>Google Sheets link to its CSV export (keeps the tab's gid); other http links are used as they are.</summary>
    static string? CsvUrl(string sheet)
    {
        var m = Regex.Match(sheet, @"docs\.google\.com/spreadsheets/d/([\w-]+)");
        if (m.Success)
        {
            var gid = Regex.Match(sheet, @"[#&?]gid=(\d+)");
            return $"https://docs.google.com/spreadsheets/d/{m.Groups[1].Value}/export?format=csv" + (gid.Success ? "&gid=" + gid.Groups[1].Value : "");
        }
        return sheet.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? sheet : null;
    }

    static string CachePath(string url) => Path.Combine(AppSettings.Dir, "cache",
        "rumours-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..8].ToLowerInvariant() + ".csv");

    /// <summary>
    /// Apply the rating sheet: a local .csv, or a sheet link downloaded at most every 6 hours.
    /// Empty = the built-in ratings. Rows the sheet has update the built-in rumours; new rows are added.
    /// </summary>
    public static async Task RefreshAsync(string sheet, bool force = false)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            sheet = sheet.Trim();
            if (sheet == "")
            {
                All = BuiltIn();
                _sourceName = "built-in";
                _sourceTimeUtc = null;
                _appliedUrl = sheet;
                return;
            }
            if (File.Exists(sheet))
            {
                if (force || _appliedUrl != sheet) Apply(await File.ReadAllTextAsync(sheet), Path.GetFileName(sheet), File.GetLastWriteTimeUtc(sheet));
                _appliedUrl = sheet;
                return;
            }
            if (CsvUrl(sheet) is not { } url)
            {
                Log.Write("rumour sheet: not a link or an existing .csv file: " + sheet);
                return;
            }

            var name = sheet == DefaultSheet ? "Jestra1220's sheet" : "your sheet";
            var cache = CachePath(url);
            var age = File.Exists(cache) ? DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) : TimeSpan.MaxValue;
            if (_appliedUrl != url && File.Exists(cache))
            {
                Apply(await File.ReadAllTextAsync(cache), name, File.GetLastWriteTimeUtc(cache));
                _appliedUrl = url;
            }
            if (!force && age < TimeSpan.FromHours(6)) return;

            var csv = await Http.GetStringAsync(url);
            if (Apply(csv, name, DateTime.UtcNow))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                await File.WriteAllTextAsync(cache, csv);
                _appliedUrl = url;
            }
        }
        catch (Exception ex)
        {
            Log.Write("rumour sheet: " + ex.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }

    static bool Apply(string csv, string name, DateTime timeUtc)
    {
        var merged = Merge(BuiltIn(), csv, out var matched, out var added);
        if (matched < 5)
        {
            Log.Write($"rumour sheet ({name}): only {matched} known rumours, ignored");
            return false;
        }
        All = merged;
        _sourceName = name;
        _sourceTimeUtc = timeUtc;
        Log.Write($"rumour sheet ({name}): {matched} rated, {added} new");
        return true;
    }

    /// <summary>
    /// Sheet layout (Jestra1220's): "Rumor, Map Type, Mods, Rating"; a row with only the first cell starts a
    /// section ("Unique Maps", "Bosses", "Sagas"); rows before the first section are Grand Expeditions.
    /// </summary>
    internal static List<Rumour> Merge(List<Rumour> builtIn, string csv, out int matched, out int added)
    {
        matched = added = 0;
        var list = builtIn.Select(r => r.Copy()).ToList();
        RumourKind? kind = RumourKind.Grand;
        var first = true;
        foreach (var raw in Csv(csv))
        {
            var cells = raw.Select(c => c.Trim()).Concat(Enumerable.Repeat("", 4)).Take(4).ToArray();
            if (first)
            {
                first = false;
                if (ItemMatcher.Normalize(cells[0]) is "rumor" or "rumour" or "rumors" or "rumours") continue;
            }
            if (cells.All(c => c == "")) continue;
            if (cells.Skip(1).All(c => c == ""))
            {
                var h = ItemMatcher.Normalize(cells[0]);
                kind = h.Contains("saga") ? null : h.Contains("unique") ? RumourKind.UniqueMap : h.Contains("boss") ? RumourKind.Boss : RumourKind.Grand;
                continue;
            }
            if (kind == null || cells[3] == "") continue;

            var r = Match(list, cells[0], 0.25).Rumour;
            if (r == null)
            {
                r = new Rumour { Text = cells[0], Map = cells[1], Mods = cells[2] };
                list.Add(r);
                added++;
            }
            else
            {
                matched++;
                // Keep our spelling unless the sheet moved the rumour to another island.
                if (cells[1] != "" && ItemMatcher.Levenshtein(ItemMatcher.Normalize(cells[1]), ItemMatcher.Normalize(r.Map), 2) < 0)
                    r.Map = cells[1];
                if (r.Mods == "") r.Mods = cells[2];
            }
            r.Kind = kind.Value;
            r.Rating = cells[3];
        }
        return list;
    }

    static IEnumerable<List<string>> Csv(string text)
    {
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch is '\n' or '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString());
                cell.Clear();
                yield return row;
                row = new List<string>();
            }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            yield return row;
        }
    }
}

/// <summary>The "Uncharted Waters" tooltip read off the screen.</summary>
internal sealed class RumourTooltip
{
    sealed class Slot
    {
        public float Y;
        public readonly List<string> Reads = new();
    }

    readonly List<Slot> _slots = new();
    float _k = 1;

    public readonly List<Rumour> Rumours = new();
    public readonly List<string> Unknown = new();
    public int LineCount => _slots.Count;
    /// <summary>Everything the tooltip covers, so the label goes next to it.</summary>
    public RectangleF Bounds;
    /// <summary>The rumour lines (screen): worth reading again bigger.</summary>
    public RectangleF Box;
    /// <summary>Centre of the "Island Rumours" header: the same area shows its tooltip at the same spot.</summary>
    public PointF Where;

    public static RumourTooltip? Parse(IReadOnlyList<TextSegment> segments, float k)
    {
        static float Centre(TextSegment s) => s.Bounds.Left + s.Bounds.Width / 2;
        static bool Has(TextSegment s, params string[] words) => ItemMatcher.Normalize(s.Text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(w => words.Any(t => ItemMatcher.Levenshtein(w, t, 1) >= 0));

        var header = segments.FirstOrDefault(s => s.Words.Count <= 3 && Has(s, "rumours", "rumors"));
        if (header == null) return null;
        var cx = Centre(header);
        // "Requires: Expedition Logbook" in game ("Consumes:" in older screenshots) closes the list.
        var end = segments
            .Where(s => s.Bounds.Top > header.Bounds.Bottom && Math.Abs(Centre(s) - cx) < 250 * k && Has(s, "requires", "consumes"))
            .OrderBy(s => s.Bounds.Top)
            .FirstOrDefault();
        var bottom = end?.Bounds.Top ?? header.Bounds.Bottom + 130 * k;

        var t = new RumourTooltip
        {
            _k = k,
            Where = new PointF(cx, header.Bounds.Top + header.Bounds.Height / 2),
            Box = RectangleF.FromLTRB(cx - 140 * k, header.Bounds.Bottom + 2, cx + 140 * k, bottom - 2),
        };
        var bounds = RectangleF.Union(header.Bounds, t.Box);
        // "Uncharted Waters" / "Use a logbook to chart the area" above the header, the logbook line under the end.
        foreach (var s in segments.Where(s => s.Bounds.Bottom <= header.Bounds.Top + 2 && s.Bounds.Bottom > header.Bounds.Top - 110 * k &&
                                              Math.Abs(Centre(s) - cx) < 220 * k))
            bounds = RectangleF.Union(bounds, s.Bounds);
        if (end != null) bounds = RectangleF.Union(bounds, new RectangleF(end.Bounds.Left, end.Bounds.Top, end.Bounds.Width, 70 * k));
        // OCR sees the text; the parchment frame around it is wider.
        t.Bounds = RectangleF.Inflate(bounds, 30 * k, 8 * k);
        t.AddRead(segments);
        return t.LineCount > 0 ? t : null;
    }

    /// <summary>A tooltip listing these lines (tests).</summary>
    public static RumourTooltip Of(params string[] lines)
    {
        var t = new RumourTooltip { Box = new RectangleF(0, 0, 300, 40 * lines.Length) };
        t.AddRead(lines.Select((line, i) =>
        {
            var r = new RectangleF(10, i * 40 + 10, 200, 20);
            return new TextSegment { Words = { new OcrWord { Text = line, Rect = r } }, Bounds = r };
        }).ToList());
        return t;
    }

    /// <summary>Another OCR pass (the whole area or just <see cref="Box"/>): each line keeps its best-matching read.</summary>
    public void AddRead(IReadOnlyList<TextSegment> segments)
    {
        var inBox = segments.Where(s =>
        {
            var cy = s.Bounds.Top + s.Bounds.Height / 2;
            return cy > Box.Top && cy < Box.Bottom && s.Bounds.Right > Box.Left && s.Bounds.Left < Box.Right;
        }).OrderBy(s => s.Bounds.Top + s.Bounds.Height / 2).ToList();

        // OCR sometimes splits one handwritten line into pieces ("A" + "fellCK..."): join pieces at the same height.
        var lines = new List<List<TextSegment>>();
        foreach (var s in inBox)
        {
            var cy = s.Bounds.Top + s.Bounds.Height / 2;
            var line = lines.LastOrDefault();
            if (line != null && Math.Abs(line[0].Bounds.Top + line[0].Bounds.Height / 2 - cy) < 10 * _k) line.Add(s);
            else lines.Add(new List<TextSegment> { s });
        }
        foreach (var line in lines)
        {
            var text = string.Join(" ", line.OrderBy(s => s.Bounds.Left).Select(s => s.Text)).Trim();
            if (text.Count(char.IsLetter) < 3) continue;
            var y = line.Average(s => s.Bounds.Top + s.Bounds.Height / 2);
            var slot = _slots.FirstOrDefault(sl => Math.Abs(sl.Y - y) < 12 * _k);
            if (slot == null) _slots.Add(slot = new Slot { Y = y });
            slot.Reads.Add(text);
        }

        Rumours.Clear();
        Unknown.Clear();
        foreach (var slot in _slots.OrderBy(sl => sl.Y))
        {
            var best = slot.Reads.Select(RumourBook.Match).Where(m => m.Rumour != null).OrderBy(m => m.Ratio).FirstOrDefault();
            if (best.Rumour == null) Unknown.Add(slot.Reads[0]);
            else if (!Rumours.Contains(best.Rumour)) Rumours.Add(best.Rumour);
        }
    }
}

/// <summary>
/// Everything seen on one uncharted area. The game lists 3 of its special islands at random; moving an item in
/// the inventory (or activating a saga) re-rolls which 3, so the union over a few looks is the real picture.
/// </summary>
internal sealed class RumourArea
{
    public PointF Where;
    public readonly List<Rumour> Seen = new();
    public readonly List<string> Unknown = new();
    public int LastLineCount;
    public int Looks;
    public DateTime LastSeenUtc;
    List<Rumour> _lastShown = new();

    public int GrandCount => Seen.Count(r => r.Kind == RumourKind.Grand);

    /// <summary>True when this look showed something new.</summary>
    public bool Add(RumourTooltip t)
    {
        var changed = false;
        foreach (var r in t.Rumours.Where(r => !Seen.Contains(r)))
        {
            Seen.Add(r);
            changed = true;
        }
        foreach (var u in t.Unknown.Where(u => !Unknown.Contains(u)))
        {
            Unknown.Add(u);
            changed = true;
        }
        if (!t.Rumours.SequenceEqual(_lastShown)) Looks++;
        _lastShown = t.Rumours.ToList();
        LastLineCount = t.LineCount;
        LastSeenUtc = DateTime.UtcNow;
        return changed;
    }
}

internal static class RumourLabels
{
    public static (string Text, Color Color) Verdict(RumourArea a)
    {
        var best = a.Seen.Count == 0 ? 0 : a.Seen.Max(r => r.Points);
        var ge = a.GrandCount;
        if (best >= 10 || ge >= 4) return ("GOATED", Palette.Best);
        if (best >= 6.5 || ge >= 3) return ("good", Palette.Gold);
        if (best >= 4) return ("ok", Palette.Text);
        return ("skip", Palette.Bad);
    }

    static Color RatingColor(Rumour r) => r.Points switch
    {
        >= 9 => Palette.Best,
        >= 6 => Palette.Gold,
        >= 3.5 => Palette.Text,
        >= 1.5 => Palette.Dim,
        _ => Palette.Bad,
    };

    /// <summary>Compact: verdict + one line per island. <paramref name="details"/> (Ctrl held): why, prices, sagas, tablets.</summary>
    public static OverlayLabel Build(RumourArea area, RectangleF tooltip, float textHeight, bool details, ItemMatcher? matcher, string scanKey)
    {
        var (verdict, color) = Verdict(area);
        var ge = area.GrandCount;
        var label = new OverlayLabel { Anchor = tooltip, HighlightAnchor = false, Accent = color, TextHeight = textHeight };
        void Add(string text, Color c, float size = 0.82f, bool bold = false, string? tag = null, Color? tagColor = null) =>
            label.Lines.Add(new LabelLine { Text = text, Color = c, Size = size, Bold = bold, Tag = tag, TagColor = tagColor ?? Palette.Dim });
        // Long text over several lines: the prefix ("why:", a bullet) only on the first one.
        void Wrapped(string indent, string prefix, string text, Color c)
        {
            var first = true;
            foreach (var line in Wrap(text, 60))
            {
                Add(indent + (first ? prefix : new string(' ', prefix.Length + 1)) + line, c, 0.78f);
                first = false;
            }
        }

        Add($"Expedition: {verdict}", color, 1f, bold: true, tag: ge > 0 ? $"{ge}+ GE" : null, tagColor: color);
        foreach (var r in area.Seen.Where(r => r.Special).OrderByDescending(r => r.Points)) AddTodo(label, r, matcher, scanKey);
        foreach (var r in area.Seen.OrderByDescending(r => r.Points))
        {
            Add($"{r.Rating}   {r.ShortText}  \u2192  {r.Map}", RatingColor(r), 0.9f, bold: details, tag: r.KindText);
            if (!details)
            {
                Add("        " + r.Mods, Palette.Dim, 0.74f);
                continue;
            }
            Wrapped("        ", "", r.Mods, Palette.Text);
            if (r.Note != null) Wrapped("        ", "why: ", r.Note, Palette.Dim);
            if (r.Special) continue; // tip and prices are in its "do this" box
            if (r.Tip != null) Wrapped("        ", "tip: ", r.Tip, Palette.Gold);
            foreach (var p in r.Prices.Select(n => PriceText(n, matcher)).OfType<string>())
                Add("        " + p, Palette.Gold, 0.78f);
        }
        foreach (var u in area.Unknown) Add($"?     \"{u}\" (not in the rating sheet)", Palette.Dim, 0.8f);

        Add(area.LastLineCount >= 3
            ? $"the game shows 3 at a time: move an inventory item to see the rest  ({area.Seen.Count + area.Unknown.Count} seen)"
            : "that's every special island here (one rumour can still be 2-3 islands)", Palette.Dim, 0.74f);

        if (RumourBook.AldursSagaDrops)
        {
            var (aldur, aldurColor) = ge >= 4 ? ($"Aldur's Saga: YES, {ge}+ GE \u2192 right-click it, then use the logbook here", Palette.Best)
                : ge == 3 && area.LastLineCount >= 3 ? ("Aldur's Saga: maybe, look again for a 4th GE", Palette.Warn)
                : ($"Aldur's Saga: no ({ge} GE, wants 4-5+)", Palette.Dim);
            Add(aldur, aldurColor, 0.85f, bold: ge >= 4);
        }
        else if (ge > 0)
        {
            Add("no Aldur's Saga in this league: its mods can roll on each GE by themselves", Palette.Dim, 0.74f);
        }

        if (!details)
        {
            Add("hold Ctrl: why + sagas / tablets", Palette.Dim, 0.74f);
            return label;
        }

        Add("Sagas (activate before using the logbook)", Palette.Text, 0.85f, bold: true);
        if (RumourBook.AldursSagaDrops)
        {
            Wrapped("  ", "\u2022 ", "Aldur's: right-click it (red border = active), then use the logbook. No boss, no fight: every GE island gets a remnant mod (all remnants 5-8 runes, one remnant 7-9, lucky runes); hover the islands to see which. Only worth it with 4-5+ GE.", Palette.Text);
            Wrapped("  ", "\u2022 ", "Boss sagas force their boss island; next to Aldur's they can replace a GE (rare since 0.5.4b).", Palette.Text);
        }
        else
        {
            Wrapped("  ", "\u2022 ", "Aldur's Saga only drops in Runes of Aldur. Here its mods (all remnants 5-8 runes, one remnant 7-9, lucky runes) can roll on Grand Expeditions by themselves: hover the islands to see them.", Palette.Text);
            Wrapped("  ", "\u2022 ", "Boss sagas (Olroth, Uhtred, Medved, Vorana) force their boss island; they replace regular islands before GEs.", Palette.Text);
        }
        Wrapped("  ", "\u2022 ", "Global remnant mods (all remnants / lucky) beat one-remnant ones; 10-rune remnants are the Aldur's Legacy chase.", Palette.Text);
        var uhtred = PriceText("Uhtred's Saga", matcher);
        var call = PriceText("Runeseeker's Call", matcher);
        Wrapped("  ", "\u2022 ", $"{uhtred ?? "Uhtred's Saga"}: forces Stardrinker, chance at the Depleted Mana Rune for {call ?? "Runeseeker's Call"}.", Palette.Text);
        Add("Tablets", Palette.Text, 0.85f, bold: true);
        Wrapped("  ", "\u2022 ", "None in Grand Expeditions since 0.5.5d (no more Visions of Paradise on the Moor). Boss islands still take Visions of Paradise: tested on the Secluded Temple, it irradiates the island so you kill the boss twice.", Palette.Text);
        Wrapped("  ", "\u2022 ", "For your normal maps, Expedition Tablets stack: 1 = adds an Expedition, 2 = +1 Verisium Remnant and +35% density, 3 = +1 Expedition, 4 = +1 Remnant and +35% more. Look for \"more Logbooks from Runic Monsters\": that's how you get logbooks.", Palette.Text);
        Add("ratings: " + RumourBook.Source, Palette.Dim, 0.7f);
        return label;
    }

    /// <summary>Hovering a charted island that has a chase drop: just its "do this" box.</summary>
    /// <summary>Hovering a charted island: its "do this" box when it has a chase drop, otherwise what it is and how good.</summary>
    public static OverlayLabel Island(Rumour r, RectangleF anchor, float textHeight, ItemMatcher? matcher, string scanKey)
    {
        var label = new OverlayLabel { Anchor = anchor, HighlightAnchor = false, Accent = RatingColor(r), TextHeight = textHeight };
        if (r.Special)
        {
            AddTodo(label, r, matcher, scanKey);
            label.Lines.Add(new LabelLine { Text = $"{r.Rating}   {r.Map} ({r.KindText}): {r.Mods}", Color = Palette.Dim, Size = 0.74f });
            return label;
        }
        label.Lines.Add(new LabelLine { Text = $"{r.Rating}   {r.Map}", Color = RatingColor(r), Bold = true, Tag = r.KindText, TagColor = Palette.Dim });
        label.Lines.Add(new LabelLine { Text = $"rumour \"{r.ShortText}\": {r.Mods}", Color = Palette.Text, Size = 0.85f });
        foreach (var line in Wrap(r.Note ?? "", 60)) label.Lines.Add(new LabelLine { Text = line, Color = Palette.Warn, Size = 0.78f });
        foreach (var line in Wrap(r.Tip ?? "", 60)) label.Lines.Add(new LabelLine { Text = line, Color = Palette.Gold, Size = 0.78f });
        foreach (var p in r.Prices.Select(n => PriceText(n, matcher)).OfType<string>())
            label.Lines.Add(new LabelLine { Text = p, Color = Palette.Gold, Size = 0.78f });
        return label;
    }

    /// <summary>Green box at the top: what to do for an island with a chase drop, and what the drop is worth now.</summary>
    /// <param name="scanKey">Replaces "{scan}" in the steps: the user's price-scan hotkey.</param>
    static void AddTodo(OverlayLabel label, Rumour r, ItemMatcher? matcher, string scanKey)
    {
        label.Accent = Palette.Best;
        label.Lines.Add(new LabelLine { Text = $"\u2605 DO THIS: {r.ShortText} \u2192 {r.Map}", Color = Palette.Best, Bold = true, Tag = r.Rating, TagColor = Palette.Best });
        foreach (var step in r.Todo)
        {
            var first = true;
            foreach (var line in Wrap(step.Replace("{scan}", scanKey), 62))
            {
                label.Lines.Add(new LabelLine { Text = (first ? "   \u25b8 " : "       ") + line, Color = Palette.Gold, Size = 0.84f });
                first = false;
            }
        }
        var prices = r.Prices.Select(n => PriceText(n, matcher)).OfType<string>().ToList();
        if (prices.Count > 0)
            label.Lines.Add(new LabelLine { Text = "       " + string.Join("   \u00b7   ", prices), Color = Palette.Text, Size = 0.8f, Bold = true });
    }

    internal static string? PriceText(string name, ItemMatcher? matcher)
    {
        var entries = matcher?.Lookup(name);
        if (entries == null || entries.Count == 0) return null;
        return $"{name} \u2248 {Fmt.Range(entries.Min(e => e.Divine), entries.Max(e => e.Divine))} div";
    }

    static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }
}

/// <summary>Follows the game's Client.txt to know the current area (hideout, map, logbook island...).</summary>
internal sealed class ClientLog
{
    static readonly Regex AreaLine = new(@"Generating level \d+ area ""([^""]+)""", RegexOptions.Compiled);

    string? _path;
    long _offset;
    DateTime _nextLookup;

    /// <summary>Internal area id ("HideoutCanal", "MapRugosa", "ExpeditionLogBook_Prairie"), null until known.</summary>
    public string? Area { get; private set; }
    public event Action<string>? AreaChanged;

    /// <summary>Somewhere with a map device. Unknown (no log found) counts as yes.</summary>
    public bool MapDevicePossible => Area == null ||
        Area.StartsWith("Hideout", StringComparison.OrdinalIgnoreCase) ||
        Area.Contains("town", StringComparison.OrdinalIgnoreCase) ||
        Area.Contains("refuge", StringComparison.OrdinalIgnoreCase);

    public void Poll()
    {
        try
        {
            if (_path == null)
            {
                if (DateTime.UtcNow < _nextLookup) return;
                _nextLookup = DateTime.UtcNow.AddSeconds(30);
                _path = FindLog();
                if (_path == null) return;
                _offset = Math.Max(0, new FileInfo(_path).Length - 512 * 1024);
                Log.Write("client log: " + _path);
            }

            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _offset) _offset = 0; // log was cleared
            if (fs.Length == _offset) return;
            fs.Seek(_offset, SeekOrigin.Begin);
            var buffer = new byte[Math.Min(fs.Length - _offset, 4 * 1024 * 1024)];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = fs.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }
            var end = Array.LastIndexOf(buffer, (byte)'\n', Math.Max(0, read - 1));
            if (end < 0) return; // half a line so far
            _offset += end + 1;

            string? area = null;
            foreach (Match m in AreaLine.Matches(Encoding.UTF8.GetString(buffer, 0, end + 1))) area = m.Groups[1].Value;
            if (area != null && area != Area)
            {
                Area = area;
                AreaChanged?.Invoke(area);
            }
        }
        catch (Exception ex)
        {
            Log.Write("client log: " + ex.Message);
            _path = null;
        }
    }

    static string? FindLog()
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!p.ProcessName.StartsWith("PathOfExile", StringComparison.OrdinalIgnoreCase)) continue;
                if (p.MainModule?.FileName is { } exe &&
                    Path.Combine(Path.GetDirectoryName(exe)!, "logs", "Client.txt") is var log && File.Exists(log))
                    return log;
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
        foreach (var guess in new[]
                 {
                     @"C:\Program Files (x86)\Steam\steamapps\common\Path of Exile 2\logs\Client.txt",
                     @"C:\Program Files (x86)\Grinding Gear Games\Path of Exile 2\logs\Client.txt",
                 })
            if (File.Exists(guess)) return guess;
        return null;
    }
}

/// <summary>Image clean-up for the handwritten rumour lines (dark ink on parchment).</summary>
internal static class RumourOcr
{
    /// <summary>
    /// The handwriting reads better bigger: OCR just the rumour lines again (as they are, then cleaned up) until every
    /// line is recognised; each line keeps its best read. <paramref name="region"/> = where <paramref name="capture"/> is on screen.
    /// </summary>
    public static async Task RereadAsync(OcrScanner ocr, RumourTooltip tooltip, Bitmap capture, Rectangle region, float k)
    {
        var box = Rectangle.Round(tooltip.Box);
        box.Offset(-region.X, -region.Y);
        box.Intersect(new Rectangle(Point.Empty, capture.Size));
        if (box.Width < 40 || box.Height < 10) return;
        using var crop = capture.Clone(box, capture.PixelFormat);
        var origin = new Point(region.X + box.X, region.Y + box.Y);
        tooltip.AddRead(await ocr.RecognizeAsync(crop, 3.0 / k, origin));
        if (tooltip.Unknown.Count == 0) return;
        using var clean = Clean(crop, binarize: false);
        tooltip.AddRead(await ocr.RecognizeAsync(clean, 3.0 / k, origin));
        if (tooltip.Unknown.Count == 0) return;
        tooltip.AddRead(await ocr.RecognizeAsync(clean, 4.0 / k, origin));
    }

    /// <summary>Grayscale, contrast stretched; with <paramref name="binarize"/> Otsu-thresholded to black ink on white.</summary>
    public static Bitmap Clean(Bitmap source, bool binarize)
    {
        var w = source.Width;
        var h = source.Height;
        var data = source.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var px = new byte[w * h * 4];
        try
        {
            for (var y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, px, y * w * 4, w * 4);
        }
        finally
        {
            source.UnlockBits(data);
        }

        var lum = new byte[w * h];
        var hist = new int[256];
        for (var i = 0; i < lum.Length; i++)
        {
            lum[i] = (byte)((px[i * 4] * 11 + px[i * 4 + 1] * 59 + px[i * 4 + 2] * 30) / 100);
            hist[lum[i]]++;
        }
        // Stretch between the 1st and 99th percentile.
        int lo = 0, hi = 255, acc = 0;
        for (var v = 0; v < 256; v++) { acc += hist[v]; if (acc >= lum.Length / 100) { lo = v; break; } }
        acc = 0;
        for (var v = 255; v >= 0; v--) { acc += hist[v]; if (acc >= lum.Length / 100) { hi = v; break; } }
        var range = Math.Max(1, hi - lo);
        var threshold = binarize ? Otsu(hist, lum.Length) : -1;

        for (var i = 0; i < lum.Length; i++)
        {
            byte v;
            if (threshold >= 0) v = lum[i] > threshold ? (byte)255 : (byte)0;
            else v = (byte)Math.Clamp((lum[i] - lo) * 255 / range, 0, 255);
            px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = v;
            px[i * 4 + 3] = 255;
        }
        var result = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var outData = result.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(px, y * w * 4, outData.Scan0 + y * outData.Stride, w * 4);
        }
        finally
        {
            result.UnlockBits(outData);
        }
        return result;
    }

    static int Otsu(int[] hist, int total)
    {
        double sum = 0;
        for (var i = 0; i < 256; i++) sum += i * hist[i];
        double sumB = 0, best = -1;
        int wB = 0, threshold = 127;
        for (var t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            var wF = total - wB;
            if (wF == 0) break;
            sumB += t * hist[t];
            var mB = sumB / wB;
            var mF = (sum - sumB) / wF;
            var between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best)
            {
                best = between;
                threshold = t;
            }
        }
        return threshold;
    }
}
