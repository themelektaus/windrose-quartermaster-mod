# Windrose Quartermaster

Configurator + build pipeline for data-pak mods for
[Windrose](https://www.nexusmods.com/windrose). A small desktop GUI
(WPF + WebView2, or a plain browser tab) lets you edit profiles and
bake them into a single `_P.pak` that drops into the `~mods` folder.

A profile bundles tweaks across multiple domains:

- **Stack sizes** - per-item or global multiplier / absolute caps
- **Loot tables** - per-category Min/Max multipliers
- **Pickup radius** - auto-pickup magnet range, free 1.0-10.0x slider
- **Fast-travel bells & signal fires** - raise the placement caps
- **Building stability** - structures hold longer cantilevers / taller towers
- **Minimap range** - foot + ship reveal range, 1.0-5.0x slider
- **Bonfire radius** - building-center influence sphere, 1.0-5.0x slider
- **No smoke** - hide smoke / flame Niagara FX on campfires, furnaces, kilns
- **Mods tab** - inspect `~mods/`, recycle-bin old Quartermaster builds

Vanilla values are extracted directly from the game's main pak file
(`pakchunk0-WindowsServer.pak` or `pakchunk0-Windows.pak`) - no external
reference mod needed. The resulting pak is pure data, so no UE4SS / SML
dependency, works in singleplayer / dedicated server / co-op alike.

---

## Prerequisites

- **.NET 10 SDK** (or newer preview) - everything is C# now.
- **Windrose installed via Steam** - auto-detected via the registry +
  `libraryfolders.vdf`. Dedicated-server / non-Steam installs work too,
  you just need to point at a pak path.
- **Git** on the PATH - the configurator transparently runs
  `git submodule update --init Tools/CUE4Parse` on first use to pull the
  CUE4Parse reader the icon extractor needs. (No need to do it yourself,
  but the binary has to be reachable.)
- **A UE5 `*.usmap` file** - only needed when running from the source
  tree. The single-file EXE ships an embedded copy and seeds it into
  `QuartermasterData\` automatically, so end users don't need this. For
  game updates (UE-version bump), regenerate one with UE4SS Keybinds
  (Ctrl+Num6 in-game) and drop it into the data root - newest mtime
  wins, so it transparently supersedes the embedded copy.

`repak.exe` is auto-downloaded (pinned v0.2.3, SHA256-verified) on first
use. There are no PowerShell scripts left - everything runs through the
GUI or the headless CLI shim.

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

Produces a single self-contained `Quartermaster.exe` (~94 MB, all .NET +
WebView2 native libs + frontend + a default UE5 `.usmap` + the
CUE4Parse-backed icon extractor bundled, compressed) at
`GUI\App\bin\Publish\Quartermaster.exe`. You can drop it **anywhere** -
desktop, USB stick, `C:\Tools\`, doesn't matter. On first run a sibling
`QuartermasterData\` folder is created **next to the EXE** so the data
travels with it (USB-stick portable):

```
<wherever>\Quartermaster.exe
<wherever>\QuartermasterData\
  .webview2\                     <- WebView2 cache/cookies
  Profiles\<id>.json             <- profiles you create (empty on first run)
  Sources\Vanilla\               <- extracted by setup (1097 item JSONs)
  Icons\                         <- extracted by setup (1097 PNGs)
  Tools\IconExtractor\publish\   <- seeded from embedded zip on first run
  Tools\repak.exe                <- auto-downloaded from GitHub on first setup
  *.usmap                        <- seeded from embedded resource on first run;
                                    drop a newer one here after game updates
```

When you launch the EXE from inside the source repo (or any ancestor
folder containing `Tools\QuartermasterCore\QuartermasterCore.csproj`),
it stays in "dev mode" and reads/writes against the repo paths instead.
That way the standard `dotnet run` workflow uses the tracked profiles
under `Profiles\` as the source of truth.

> **End-user prerequisites for the portable EXE**: the .NET **10 desktop
> runtime** must be installed. The WPF host bundles its own runtime, but
> the icon extractor is a framework-dependent net10.0 process and needs
> the runtime available on the machine. The installer is a free
> ~80 MB download from <https://dotnet.microsoft.com/download/dotnet/10.0>.
> No SDK, Git, or CUE4Parse source needed any more.

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
- **Loot Tables tab** - per-category Min/Max multipliers applied to
  every entry in matching tables.
- **Misc tab** - cards for pickup radius, fast-travel bell caps,
  building stability, minimap range, bonfire radius and no-smoke FX.
  Each card has its own toggle / slider; nothing is bundled into the
  pak unless the corresponding card is enabled.
- **Mods tab** - lists every `.pak` currently in your `~mods` folder,
  marks Quartermaster builds, and recycles old ones with one click.
  Also exposes a button that re-opens the first-run setup dialog so
  you can re-dump vanilla JSONs / icons after a game update.

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
