# Windrose Quartermaster

Configurator + build pipeline for data-pak mods for
[Windrose](https://www.nexusmods.com/windrose). A small desktop GUI
(WPF + WebView2, or a plain browser tab) lets you edit profiles and
bake them into a single `_P.pak` that drops into the `~mods` folder.

A profile bundles tweaks across multiple domains:

- **Stack sizes** - per-item or global multiplier / absolute caps
- **Item Creator** - clone a vanilla item, give it a new name / icon /
  category and ship it as a brand-new entry
- **Building Creator** - clone a vanilla building, rewrite display name +
  description, edit the recipe cost, swap mesh materials, attach a
  flame/torch FX preset, upload custom looping ambient audio
- **Loot tables** - per-category Min/Max multipliers
- **Buyers** - retune what NPC vendors are willing to buy from you and at
  which price (per-recipe edits with conflict-safe merging)
- **Sellers** - retune what NPC vendors offer you and at which price
- **Cooldowns** - bidirectional 0.1-3.0x sliders across eight families
  (Elixir, Medicine, Spell of Return, Ship Repair Kit, Boar Whistle,
  Ship Summon, Ranged Weapon Reload, Ship Cannon Reload)
- **Crop growth & crafting durations** - 0.1-3.0x sliders for crop grow
  times plus furnace / kiln / tannery / mill / trade-outpost recipe
  durations
- **Pickup radius** - auto-pickup magnet range, free 1.0-10.0x slider
- **Fast-travel bells & signal fires** - raise the placement caps
- **Building stability** - structures hold longer cantilevers / taller towers
- **Minimap range** - foot + ship reveal range, 1.0-5.0x slider
- **Bonfire radius** - building-center influence sphere, 1.0-5.0x slider
- **Pickaxe range** - 1.0-3.0x slider that scales the trace radius on
  every pickaxe tier
- **Light radius** - per-light AttenuationRadius multipliers for candles,
  lanterns, wall lamps + signal fires, torches + chandeliers, building-
  center fires and the belt lantern, 0.1-10x sliders
- **No smoke** - hide smoke / flame Niagara FX on campfires, furnaces, kilns
- **Equipment slots** - extra ring / necklace equipment slots (vanilla 1
  each, up to 10); new characters get them automatically, existing saves
  via the Characters tab
- **Ship slots** - bigger cargo holds + more Combat Orders slots for the
  Brig / Frigate / Ketch and their variants; new ships automatic,
  existing ships via the Characters tab
- **Ship speed** - per-ship-type motor-speed multipliers (0.1-10x) for
  every hull's drive curve (Shallow Boat / Brig / Cutter / Frigate /
  Ketch, each with player / AI / service / faction variants), scaled
  from the live vanilla motor curve, on top of an overall ship-speed
  slider
- **UI scale** - global interface scale (`Engine.ini` `ApplicationScale`),
  50-110%, written straight into your local UE config and locked
  read-only so the game keeps it
- **Save patcher (Characters tab)** - retro-fit the equipment- and
  ship-slot counts onto characters / ships already baked into your save,
  with an automatic per-target backup before each write
- **Sea Shanties** - replace any of the 10 vanilla shanty slots with your
  own audio (WAV/MP3/FLAC/OGG, auto-transcoded to BinkAudio), add extra
  tracks alongside the vanilla 10, tune per-track volume, exclude single
  slots
- **Profile import/export** - profiles serialize to a single JSON (audio
  files travel as a ZIP); drag and drop on the GUI to import, with
  overwrite confirmation on id conflicts
- **Issue Reporter** - one-click bundle of the active profile, latest
  build log and `~mods` listing into a single archive for diagnostics
- **Mods tab** - inspect `~mods/`, recycle-bin old Quartermaster builds

Vanilla values are extracted directly from the game's main pak file
(`pakchunk0-Windows.pak` for a client install, or
`pakchunk0-WindowsServer.pak` for a dedicated server). The resulting
pak is pure data, so no UE4SS / SML dependency - works in singleplayer /
dedicated server / co-op alike.

---

## Prerequisites

- **.NET 10 SDK** (or newer preview) - everything is C# now.
- **Windrose installed via Steam** - auto-detected via the registry +
  `libraryfolders.vdf`. Non-Steam installs (Epic / GOG / portable /
  dedicated server) work too: use the Mods tab's **Configure game
  install** button to point Quartermaster at your Windrose folder (the
  one with `R5\Binaries\Win64\Windrose-Win64-Shipping.exe` and a vanilla
  pak). The override is validated and saved to
  `QuartermasterData\game-install.json`; clearing it reverts to Steam
  auto-detect.
- **Git** on the PATH - when building from source, an MSBuild step
  transparently runs `git submodule update --init Tools/CUE4Parse` to
  pull the CUE4Parse reader the icon extractor needs. (No need to do it
  yourself; not required for the prebuilt portable EXE, which already
  bundles it.)
- **A UE5 `*.usmap` file** - only needed when running from the source
  tree. The single-file EXE ships an embedded copy and seeds it into
  `QuartermasterData\` automatically, so end users don't need this. For
  game updates (UE-version bump), regenerate one with Dumper-7 (run
  `Tools\Dumper7Setup\run_dump.bat` with the game running, F8 to dump)
  and drop the resulting `.usmap` into the data root - newest mtime
  wins, so it transparently supersedes the embedded copy.

`repak.exe` and `retoc.exe` are auto-downloaded (pinned versions,
SHA256-verified) on first use. There are no PowerShell scripts left -
everything runs through the GUI or the headless CLI shim.

---

## Run the configurator

Two equivalent ways: a desktop window (recommended) or a browser tab.

### Desktop launcher (WPF + WebView2)

```powershell
dotnet run --project GUI\App -c Release
```

Opens a single Quartermaster window backed by Microsoft Edge WebView2.
Kestrel is hosted in-process on a free port (no fixed `:17777` collision,
multiple instances can run side-by-side). Closing the window stops the
server cleanly.

Requires the **Microsoft Edge WebView2 Runtime** - preinstalled on
Windows 11 and recent Windows 10 builds. If missing, the launcher links
to the
[evergreen installer](https://developer.microsoft.com/microsoft-edge/webview2/).

### Single-file build (one .exe to share)

```powershell
dotnet publish GUI\App -p:PublishProfile=win-x64
```

Produces a single self-contained `Quartermaster.exe` (~100 MB: all .NET +
WebView2 native libs + frontend + a default UE5 `.usmap` + the
CUE4Parse-backed icon extractor, bundled and compressed) at
`GUI\App\bin\Publish\win-x64\Quartermaster.exe`. You can drop it
**anywhere** - desktop, USB stick, `C:\Tools\`, doesn't matter. On first
run a sibling `QuartermasterData\` folder is created **next to the EXE**
so the data
travels with it (USB-stick portable):

```
<wherever>\Quartermaster.exe
<wherever>\QuartermasterData\
  .webview2\                     <- WebView2 cache/cookies
  Profiles\<id>.json             <- profiles you create (empty on first run)
  Sources\Vanilla\               <- vanilla JSONs extracted by setup
  Icons\                         <- one PNG per item icon, from setup
  Tools\repak.exe                <- auto-downloaded from GitHub on first setup
  Tools\retoc.exe                <- auto-downloaded from GitHub on first setup
  *.usmap                        <- seeded from embedded resource on first run;
                                    drop a newer one here after game updates
```

When you launch the EXE from inside the source repo (or any ancestor
folder containing `Tools\QuartermasterCore\QuartermasterCore.csproj`),
it stays in "dev mode" and reads/writes against the repo paths instead.
That way the standard `dotnet run` workflow uses the tracked profiles
under `Profiles\` as the source of truth.

> **End-user prerequisites for the portable EXE**: none. The WPF host is
> published self-contained (single-file, compressed) and the icon
> extractor is linked in-process, so the EXE runs on a vanilla Windows
> machine without any .NET runtime, SDK, Git, or CUE4Parse source.

### Browser

```powershell
dotnet run --project GUI\Web -c Release
```

Then open <http://localhost:17777>.

**On first start the GUI is empty** until the vanilla item JSONs +
icons are extracted. The setup overlay does that for you: when it
detects a missing piece (no `Sources\Vanilla`, no `Icons\*.png`) it
auto-runs the dump + icon-extraction pipeline and streams the live
log into the page. ~30-90 seconds total. Subsequent launches skip
straight into the configurator.

Click **+** (New) in the header to create a profile, or the duplicate
icon to clone an existing one. For each profile you can:

- **Items tab** - pick a **global stack-size mode**: None,
  `vanilla * Multiplier` (with optional Cap), or a flat `Absolute`
  value. Set **per-item overrides** that win over the global policy,
  even for items that are normally locked at stack=1 (Equipment, NPCs,
  Ship cannons, Quest tokens).
- **Item Creator tab** - clone any vanilla item as a brand-new entry,
  rewrite name / icon / category, ship it alongside the original in
  the same pak.
- **Building Creator tab** - clone a vanilla building, rewrite display
  name + description, edit the recipe cost, swap mesh materials,
  optionally attach a flame/torch FX preset and upload looping ambient
  audio (range + volume sliders).
- **Loot Tables tab** - per-category Min/Max multipliers applied to
  every entry in matching tables.
- **Buyers tab** - retune what NPC vendors are willing to buy from you
  and at which price; per-recipe edits with conflict-safe merging.
- **Sellers tab** - retune what NPC vendors offer you and at which
  price; works alongside the Buyers tab without overwriting each other.
- **Cooldowns tab** - bidirectional 0.1-3.0x sliders across the eight
  cooldown families (Elixir, Medicine, Spell of Return, Ship Repair
  Kit, Boar Whistle, Ship Summon, Ranged Weapon Reload, Ship Cannon
  Reload), plus crop grow times and crafting / processing durations
  (furnace, kiln, tannery, mill, trade outpost).
- **Basic tab** - cards for pickup radius, fast-travel bell caps,
  building stability, minimap range, bonfire radius, pickaxe range,
  overall light radius, overall ship speed, no-smoke FX, equipment slots
  (ring / necklace) and ship slots (cargo / combat orders). Each card has its own toggle /
  slider; nothing is bundled into the pak unless the corresponding card
  is enabled. A **UI Scale** card additionally writes the global
  interface scale straight into your local `Engine.ini`
  (`ApplicationScale`) via an Apply button and locks the file read-only
  so the game keeps the value across launches.
- **Lighting tab** - per-light AttenuationRadius overrides (overrides
  the overall multiplier from the Basic Light Radius card on a per-light
  basis).
- **Ship Speed tab** - per-ship-type motor-speed overrides grouped by
  hull (Shallow Boat / Brig / Cutter / Frigate / Ketch, with player /
  AI / service / faction variants); overrides the overall multiplier
  from the Basic Ship Speed card on a per-curve basis.
- **Characters tab** - a save patcher that retro-fits the profile's
  equipment- and ship-slot counts onto characters / ships already in
  your save (the Basic-tab sliders themselves only affect newly created
  ones). Each row shows the current vs. target count with a Patch button
  that appears only when they differ; a per-target backup is written
  first. Close the game and disable Steam Cloud Sync for Windrose before
  patching, or the cloud overwrites the patched save on next launch.
- **Sea Shanties tab** - upload your own audio to replace any of the 10
  vanilla shanty slots, add extra tracks alongside the vanilla 10, tune
  per-track volume, exclude single slots. WAV/MP3/FLAC/OGG inputs are
  auto-transcoded to BinkAudio.
- **Mods tab** - lists every `.pak` currently in your `~mods` folder,
  marks Quartermaster builds, and recycles old ones with one click.
  Also exposes a button that re-opens the first-run setup dialog so
  you can re-dump vanilla JSONs / icons after a game update, plus a
  **Game install** status card whose **Configure game install** button
  sets or clears a manual install path for non-Steam setups (persisted
  to `game-install.json`).

The header has a **Report** button that bundles the active profile, the
latest build log and your `~mods` folder listing into a single archive
for quick diagnostics, and a **Play** button that launches the game
directly from the detected install. Profiles also drag-and-drop in (ZIP
for audio profiles, plain JSON otherwise) with overwrite confirmation on
id conflicts.

Press **Build** to run the patch + pack pipeline. The finished `_P.pak`
lands directly in the game's `~mods` folder, ready to play.

Profiles persist as `Profiles\<id>.json` (gitignored).

## Headless CLI

Same pipeline without the browser:

```powershell
# One-time setup (dump vanilla JSONs + extract icons). Skips steps that
# are already done; pass --force to re-run everything.
dotnet run --project GUI\Web -- --setup

# Build a profile (by id or name)
dotnet run --project GUI\Web -- --test-patcher --profile "My Profile"

# Direct multiplier without a profile
dotnet run --project GUI\Web -- --test-patcher --multiplier 4 --build-pak
```

## Install a pak

Builds from the GUI land directly in `<Windrose>\R5\Content\Paks\~mods\`,
nothing to copy. CLI builds (`--build-pak`) still write to the `Builds\`
folder so smoke tests don't touch the live game; copy from there manually
if you want a CLI-built pak in-game.

Only **one** `Quartermaster_*.pak` per `~mods` folder - remove any older
one first (the **Mods** tab handles this with a single click).
