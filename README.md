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
| **Ctrl+S** | Price of the item under the cursor (inventory, stash etc.): the price **per piece** on top, the stack total below. |
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

### Inventory scan (Ctrl+I / Ctrl+O)
The program moves the mouse over every occupied slot and presses Ctrl+C, so it reads the item's exact text, stack size included. It doesn't click and doesn't send anything to the server; at the end the mouse goes back where it was. It takes 1–5 seconds depending on how full the inventory is. Esc stops the scan.
Ctrl+O can be pressed several times in the same run: it always compares with the last Ctrl+I. Saved runs are in `%APPDATA%\poe2verlay\runs\` (one JSON per run) and in `runs.csv`. A Ctrl+I is remembered even if you restart the app.

### Expedition rumours (map device)
The game shows only 3 rumours for an area, picked at random from the area's special islands. Moving an item in the inventory (or activating a saga) rerolls the list. The overlay remembers everything it saw on the same area, so after 2–3 moves you know all the islands. If fewer than 3 show up, those are all of them. One rumour can mean 2–3 islands of the same kind, so the GE count is a minimum.
- Ratings come from **Jestra1220**'s sheet (the one from Palsteron's Expedition guide). It's downloaded every 6 hours to keep up with patches. The notes (bugs, tips) are from poe.gnejs.app.
- Aldur's Saga only drops in the Runes of Aldur league. In Forbidden Rites and Standard its mods can roll on Grand Expeditions by themselves (patch 0.5.5), so the overlay doesn't suggest the saga there.
- Tablets can no longer be used on Grand Expeditions (patch 0.5.5d), so the Visions of Paradise trick on the Moor is dead. It still works on **boss islands** (tested on Secluded Temple): after you finish it the island becomes Irradiated instead of completed and you kill the boss a second time. On normal maps Expedition Tablets stack: 1 adds an Expedition, 2 also add a Remnant plus 35% density.
- Islands with a special drop (Fallen stars, Crazed Chieftain, Stardrinker) get a green **★ DO THIS** box at the top of the label with the steps and the drop's price.
- You can use your own sheet in Settings (a Google Sheets link or a .csv file with the columns Rumor, Map, Mods, Rating). Tray → *Expedition rumours* shows the full tier list.
- It only works in the hideout (the app reads `Client.txt` from the game folder to know where you are) and only while the mouse rests on an area.

Thresholds and hotkeys can be changed in Settings.

## Requirements

- Windows 10 2004+ or Windows 11, x64.
- The game client in **English** (item names are matched against poe.ninja's English names).
- The game in **Windowed Fullscreen** or Windowed. In exclusive Fullscreen the overlay isn't visible.
- Don't run the game as Administrator, otherwise the hotkeys don't reach the overlay while the game is focused.
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
- `cache\rumours-*.csv`: the rumour ratings sheet; `debug\rumours-*.png`: the last tooltips read
- `log.txt`

Screenshots taken with the overlay hotkey go to `Pictures\poe2verlay`.

## How to compile

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). The output is a self-contained single-file `dist\poe2verlay.exe` (about 70 MB, the .NET runtime is inside), so the PC that runs it needs nothing installed.

**Windows**: double-click `compile.bat` or run it from a terminal.
```bat
winget install Microsoft.DotNet.SDK.8      :: once
winget install JRSoftware.InnoSetup        :: once, optional: also builds the installer
compile.bat                                :: -> dist\poe2verlay.exe (+ dist\poe2verlay-setup-1.0.0.exe)
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
poe2verlay.exe --smoke      C:\path\out          # hook, overlay, settings window
```
