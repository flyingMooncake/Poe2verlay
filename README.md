<p align="center"><img src="assets/logo.png" width="220" alt="poe2verlay logo"></p>

# poe2verlay

Price overlay for Path of Exile 2 on Windows. Press a key on the Expedition choice screen (the meteor / Verisium Remnant) and every option gets its price in **divine** and **chaos** next to it. Prices come from [poe.ninja](https://poe.ninja/poe2/economy).

## Install

Download from [Releases](https://github.com/flyingMooncake/Poe2verlay/releases/latest):
- `poe2verlay-setup-<version>.exe`: installer. No admin rights needed; it installs to `%LOCALAPPDATA%\Programs\poe2verlay` and adds a Start menu shortcut, plus optional desktop shortcut and start with Windows. Uninstall from Windows Settings → Apps; your settings and saved runs in `%APPDATA%\poe2verlay` are kept.
- `poe2verlay.exe`: portable, nothing to install, just run it.

The exe isn't signed, so SmartScreen may warn on first start: More info → Run anyway.

## How to use it

1. Start poe2verlay. An icon appears in the system tray (it may be under the ^ arrow next to the clock).
2. On first start the settings window opens. Pick your **league** there. You can change it any time from the tray icon menu (right click → League).
3. In game:

| Key | What it does |
|---|---|
| **F2** | On the meteor panel (Runeshape Combinations): puts a **bar** next to every row with its price in `div · c` (for `3x` it shows the total). The bar is filled in proportion to the value; the best one is green with a **★**. If even the best one is worth less than 3 c, a **↻ ROLL** bar shows on top (the threshold is in Settings). Only the panel rows count, never the chat. The bars disappear when you click (you picked), press Esc, press the key again or the panel closes. |
| **Ctrl+S** | Price of the item under the cursor (inventory, stash etc.). Currency and the like: the poe.ninja price **per piece** on top, the stack total below. Everything poe.ninja doesn't price (rares, magic items, gems, white bases, Inscribed Ultimatums...) and uniques (poe.ninja only sees their listings) also get a **search on the trade site**: lowest → highest of the cheapest listings, how many are listed and the filters used (see [Trade](#trade-price-check-and-trade-browser)). |
| **Ctrl+T** | Shows/hides the **trade browser**: pathofexile.com/trade2 as an overlay on top of the game, with the Better Trading extension. Right after a price check it opens on that search. It takes the keyboard while shown; Ctrl+T or ✕ hides it and gives the game its focus back. |
| **Ctrl+I** | Scans the inventory (it must be open; don't move the mouse until it's done) and shows only the **total** on screen, as a **min – max** range. Min = only items with real trade volume, max = everything that has a price. |
| **Ctrl+O** | Scans again and opens the run **report** as an overlay on top of the game (no frame, no taskbar entry, doesn't take focus from the game): profit min – max, time played, div/hour and the farming session. **▾ details** makes it big: tabs *Inventory / Now / Difference / History* with every item; **▴ small** shrinks it back to just the sums. Drag it by the top bar; when big, resize it from the edges. **Save run** saves the run to the history. |
| **Ctrl+0** | Shows/hides the report (it stays small or big, the way you left it). *History* lists the saved runs: double-click opens the difference, Delete removes it. The **Special maps** tab lists the Expedition islands that matter (Fallen stars, Crazed Chieftain, Stardrinker, the unique maps, Unknown ruins): for each one, what it is, why it matters, what to do, current prices and sources. |
| **Ctrl+K** | With the stash open: scans the current tab (normal, Quad or special: Currency etc.) and opens the **Stash value** window: the value of every scanned tab, the stash total and the items sorted by value (for the selected tab or all of them). Press Ctrl+K on every tab you want valued; rescanning the same tab updates it. F2 in the window renames the tab, Delete removes it from the list. |
| **Ctrl+F3** | Starts/stops a **farming session**. Time only counts while you play: it pauses by itself when the game isn't in front (alt-tab) or after 60 s without any key or mouse movement (pause menu, food), and on pause it also subtracts the seconds since the last input. Session profit = the runs finished with Ctrl+O; maps are counted automatically from `Client.txt`. When you stop it you see the total and div/hour; it's saved to `sessions.csv`. |
| **Ctrl+F2** | Toggles auto-scan (the bars appear by themselves, uses more CPU). |
| **Ctrl+F12** | Screenshot **with the overlay** on it, saved to `Pictures\poe2verlay`. Normal screenshots (PrintScreen, Steam, Snipping Tool) don't show the overlay, because it hides itself from screen captures so the OCR never reads its own labels. |
| *(hover)* | **Expedition:** in the hideout, at the map device, rest the mouse on an uncharted area (the *Uncharted Waters → Island Rumours* tooltip). Next to the tooltip you get how good the area is (**GOATED / good / ok / skip**), the rating of each island, what it contains, how many Grand Expeditions it has and, in the Runes of Aldur league, whether **Aldur's Saga** is worth it. Hovering a charted island shows its rating and notes too. |
| *(hold Ctrl)* | On the same label: why each island is good, the prices of the things that matter (Perfect Flux, Rakiata's Flow, Runeseeker's Call, Uhtred's Saga...), plus which sagas and tablets to use. |

Warnings:
- `⚠ low trade`: little was traded on poe.ninja (under 5 div volume), so the price is a rough guide.
- `⚠ very low trade`: under 0.5 div volume, don't trust the price.
- `random · median of N`: for rewards like "Unique Ring", "Unique Belt" etc. The value is the median of the uniques in that category, i.e. what you usually get.

### Trade price check and trade browser
The trade site sits behind Cloudflare, which turns away plain programs. So poe2verlay talks to it through the Edge WebView2 runtime that comes with Windows: the price checks run as fetch() calls inside a hidden page of pathofexile.com, and the trade browser is the same engine with the same cookies. The site's own browser check passes by itself in a few seconds; if it ever asks for a click, open the trade browser and do it there once. Logging in (in the trade browser) is optional: it raises the site's search limits and lets you whisper / buy.

**Magic and rare gear get graded first** (Ctrl+S copies the item with Ctrl+Alt+C, which includes the mod tiers):
- Every mod gets an importance (skill levels and movement speed on top, then command / minion / spell / attack damage, spirit, crit, speed, life / ES, defences, rarity; attributes, mana, leech and minor minion stats like their movement speed don't count) scaled by how close its roll is to the best roll of that mod on that kind of item (from the game data in Path of Building, so +1 minion level on a belt is half of the best, +2 the best). Rolls under 60% of the best don't count. Resistances count as one total resistance, and the trade search asks for the total, not each resistance.
- Mods must fit the item: a sceptre is a minion / aura weapon (Adds Lightning Damage to Attacks on a sceptre is worth nothing), wands and staves are spell weapons, bows, crossbows and the like attack weapons, a focus goes spell or minion. On items that can go any way (rings, amulets, armour) only the dominant kind counts: +minion levels next to cast speed is not a combo, the cast speed is listed as ignored.
- Two or three strong mods of the same kind make a combo and count extra; life, ES and resistances add to an item but don't make a combo.
- Without the mod the item is bought for (minion levels on a sceptre, spell levels on a wand / staff, movement speed on boots) it is at most a B.
- Result: **GARBAGE → VENDOR** (nothing valuable, no search) or a grade S / A / B / C with the mods that make it. Then it searches the trade site **with only those mods** (from grade C, Settings).
- Verdict: **SELL** when the median of the cheapest listings like it is worth at least 5 chaos (Settings), else **VENDOR**. The median, because the very cheapest listing is often a mistake or bait.

What a price check searches:
- **Uniques**: name and base. **Gems**: name, level, corrupted or not. **White bases**: base and item level. **Inscribed Ultimatums**: the fate on them (Victorious / Cowardly / Deadly) and the area level.
- **Magic and rare items**: any base of the same kind (gloves, ring...) with the item's most important stats, each at least 85% of the rolled value (Settings). Defences and weapon damage are searched as totals (ES ≥ ..., pDPS ≥ ...), resistances as total elemental resistance, life / ES / movement speed as totals. Runes, enchants and implicits are left out (they can be changed or come with the base). Skill levels stay exact: +4 is not +5.
- **Which stats count** (the build priorities): each kind of stat has a base importance (skill levels and movement speed first, then damage, spirit, crit, speed, life / ES, rarity, resistances, ... attributes, leech and the like last). With a **poe.ninja character link** in Settings, the build changes that: a minion build ranks minion and "allies in your presence" stats up and attack stats down, Chaos Inoculation ignores life and chaos resistance, an ES build ranks ES up, a rarity build ranks rarity up, stats already on your gear count more, and resistances count more while one of yours is under the cap. The best ones (4 by default) become filters.
- If fewer than 3 items match, it searches again without the least important stat and says so on the label.

The label shows lowest → highest of the 10 cheapest listings (in exalted orbs when they're all under half a divine), the listings themselves, how many match, and the filters. Ctrl+T opens exactly that search in the trade browser to refine it. Requests keep to the rate limits the trade site announces (5 searches per 10 s and so on); when it says to wait, the label says how long.

Better Trading (bookmarks, equivalent prices, search history...) is downloaded from the Chrome Web Store the first time the trade browser opens and runs inside it; turn it off in Settings. Your login, cookies and Better Trading's bookmarks stay in `%APPDATA%\poe2verlay\webview`.

### Inventory scan (Ctrl+I / Ctrl+O)
The program moves the mouse over every occupied slot and presses Ctrl+C, so it reads the item's exact text, stack size included. It doesn't click and doesn't send anything to the server; at the end the mouse goes back where it was. It takes 1–5 seconds depending on how full the inventory is. Esc stops the scan.
Ctrl+O can be pressed several times in the same run: it always compares with the last Ctrl+I. Saved runs are in `%APPDATA%\poe2verlay\runs\` (one JSON per run) and in `runs.csv`. A Ctrl+I is remembered even if you restart the app.

### Expedition rumours (map device)
The game shows only 3 rumours for an area, picked at random from the area's special islands. Moving an item in the inventory (or activating a saga) rerolls the list. The overlay remembers everything it saw on the same area, so after 2–3 moves you know all the islands. If fewer than 3 show up, those are all of them. One rumour can mean 2–3 islands of the same kind, so the GE count is a minimum.
- Ratings are **Jestra1220**'s (the ones from Palsteron's Expedition guide), read from [poe.gnejs.app](https://poe.gnejs.app/poe2/expedition/rumours) every 6 hours to keep up with patches. His Google Sheet is no longer updated. The notes (bugs, tips) are checked against the 0.5.5 patch notes and guides; a rating belongs to the island, so it stays right even where sources disagree about which rumour leads to which island.
- Aldur's Saga only drops in the Runes of Aldur league. In Forbidden Rites and Standard its mods can roll on Grand Expeditions by themselves (patch 0.5.5), so the overlay doesn't suggest the saga there.
- Tablets can no longer be used on Grand Expeditions (patch 0.5.5d), so the Visions of Paradise trick on the Moor is dead. It still works on **boss islands** (tested on Secluded Temple): after you finish it the island becomes Irradiated instead of completed and you kill the boss a second time. On normal maps Expedition Tablets stack: 1 adds an Expedition, 2 also add a Remnant plus 35% density.
- Islands with a special drop (Fallen stars, Crazed Chieftain, Stardrinker) get a green **★ DO THIS** box at the top of the label with the steps and the drop's price.
- You can use your own ratings in Settings (a Google Sheets link or a .csv file with the columns Rumor, Map, Mods, Rating). Tray → *Expedition rumours* shows the full tier list.
- It only works in the hideout (the app reads `Client.txt` from the game folder to know where you are) and only while the mouse rests on an area.

Thresholds and hotkeys can be changed in Settings.

## Requirements

- Windows 10 2004+ or Windows 11, x64.
- The game client in **English** (item names are matched against poe.ninja's English names).
- The game in **Windowed Fullscreen** or Windowed. In exclusive Fullscreen the overlay isn't visible.
- Don't run the game as Administrator, otherwise the hotkeys don't reach the overlay while the game is focused.
- Microsoft Edge WebView2 Runtime for the trade features. It ships with Windows 11 and current Windows 10; if it's missing: microsoft.com/edge/webview2.
- Windows OCR for English (en-US). It's usually installed already. If it's missing: Settings → Time & language → Language → add English (United States).
- Screen positions are measured at 1920x1080 and scale with the window height; other resolutions are untested.

## Files

Everything lives in `%APPDATA%\poe2verlay`:
- `settings.json`: the settings (including the scanned area, `RegionLeft/Top/Right/Bottom`, as fractions of the game window)
- `cache\`: downloaded prices. Refreshed every 30 minutes; poe.ninja updates PoE2 roughly every hour.
- `debug\`: the last 5 F2 scans (the capture, the same capture with the bars drawn and a .txt with what OCR read). Useful when something isn't recognized.
- `runs\` + `runs.csv`: saved runs
- `session.json`: the farming session in progress (kept across app restarts); `sessions.csv`: finished sessions
- `stash.json`: scanned stash tabs (prices are applied when shown, so they stay current)
- `cache\rumours-*.csv`: the downloaded rumour ratings; `debug\rumours-*.png`: the last tooltips read
- `webview\`: the trade browser's profile (pathofexile.com login, cookies, Better Trading's data); `extensions\`: the downloaded Better Trading; `cache\trade-*.json`: the trade site's stat list, currency names and bases (refreshed daily)
- `log.txt`

Screenshots taken with the overlay hotkey go to `Pictures\poe2verlay`.

## How to compile

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). The output is a self-contained single-file `dist\poe2verlay.exe` (about 70 MB, the .NET runtime is inside), so the PC that runs it needs nothing installed.

**Windows**: double-click `compile.bat` or run it from a terminal.
```bat
winget install Microsoft.DotNet.SDK.8      :: once
winget install JRSoftware.InnoSetup        :: once, optional: also builds the installer
compile.bat                                :: -> dist\poe2verlay.exe (+ dist\poe2verlay-setup-1.0.1.exe)
compile.bat 1.0.1                          :: same, with another version number
```

**Linux / WSL** (cross-compiles the Windows exe; the installer can only be built on Windows):
```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0   # once, installs to ~/.dotnet
./compile.sh                                                              # -> dist/poe2verlay.exe
./compile.sh 1.0.1
```

**Releases**: every push to `main` builds the exe and the installer on GitHub Actions. Pushing a tag `vX.Y.Z` also publishes them as a GitHub release:
```bash
git tag v1.0.1 && git push origin v1.0.1
```

Headless tests, run on Windows:

```
poe2verlay.exe --selftest   C:\path\out          # synthetic reward panel
poe2verlay.exe --scan-image shot.png C:\path\out # any screenshot
poe2verlay.exe --scan-game  C:\path\out          # capture of the game window
poe2verlay.exe --inv-grid   C:\path\out          # where it thinks the inventory grid is (doesn't move the mouse)
poe2verlay.exe --smoke      C:\path\out          # hook, overlay, settings window, trade browser and label
poe2verlay.exe --trade-probe C:\path\out         # trade site data through the hidden WebView2 page
poe2verlay.exe --trade-check C:\path\out item.txt [poe.ninja character link]   # real price checks of copied item texts
```
