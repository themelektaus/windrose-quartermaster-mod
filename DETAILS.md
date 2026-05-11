# Windrose Quartermaster - Details

Configurator + build pipeline for JSON-based Windrose mods (item stack
sizes etc.) from a vanilla snapshot extracted out of the game's main pak.
The end product is `_P.pak` files that you copy into the `~mods` folder
of your server or client.

---

## 1. Prerequisites

| What | Why | Required? |
|---|---|---|
| **.NET 10 SDK** (preview) | Builds the GUI + QuartermasterCore + IconExtractor | yes |
| **Internet access** (one-time) | Auto-downloads `repak.exe` v0.2.3 from [trumank/repak](https://github.com/trumank/repak/releases) and clones the CUE4Parse submodule on first use | yes (first run only) |
| **Git** on PATH | Used by the icon-extractor preflight to pull the CUE4Parse submodule transparently (`git submodule update --init Tools/CUE4Parse`) | yes (icons only) |
| **Windrose game/server install** | Source for the vanilla pak | yes |
| **UE4SS** in the game | Generate the `.usmap` file CUE4Parse needs | yes (icons only) |

The Steam install of Windrose is auto-detected via the Windows registry
(`HKCU\Software\Valve\Steam\SteamPath` /
`HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath`) and Steam's
`libraryfolders.vdf`. Dedicated-server installs (where the pak lives
outside Steam) are supported via an explicit pak path.

The pak is AES-encrypted; the public game key is hardcoded in
`Tools\QuartermasterCore\WindroseGameSecrets.cs` and is identical to the
one used by every other Windrose modding tool.

---

## 2. Setup after cloning

```powershell
git clone <repo-url> 'E:\Windrose\Mods\Quartermaster'
cd 'E:\Windrose\Mods\Quartermaster'
```

That's it. The CUE4Parse submodule is fetched automatically on first
icon-extraction run - no need for `git submodule update --init` by
hand. **No PowerShell scripts to run** - the first time you start the
GUI it auto-runs the dump + icon extraction:

```powershell
dotnet run --project GUI\Web -c Release
# -> http://localhost:17777
```

If the mod root is not yet set up the GUI shows a full-screen overlay
that streams live progress over Server-Sent Events. It will fail
gracefully and explain the next step if a piece is missing (e.g. no
`*.usmap` in the mod root, or Windrose isn't installed via Steam).

For headless / CI use, `dotnet run --project GUI\Web -- --setup` runs
the same pipeline and prints to stdout. `--setup --force` re-runs every
step regardless of cached state.

The pipeline auto-resolves its tooling on first use:

- **Vanilla pak**: walked from the Steam install path
  (`SteamLocator.cs`).
- **`repak.exe`**: pinned v0.2.3, downloaded + SHA256-verified to
  `repak.exe` in the mod root (`RepakResolver.cs`).
- **`IconExtractor.exe`**: in dev mode, `dotnet publish`'d into
  `Tools\IconExtractor\publish\IconExtractor.exe` from the CUE4Parse
  submodule on first use (`IconExtractorBuilder.cs`). For deployed EXEs
  the full `publish/` folder is shipped as an embedded zip resource and
  extracted into `<DataRoot>\Tools\IconExtractor\publish\` on first run
  (see `SeedIconExtractorIfMissing`) - so an end-user machine doesn't
  need .NET SDK / git / CUE4Parse source, just the .NET 8 desktop
  runtime to run the framework-dependent extractor binary.
- **`*.usmap`**: located via newest-mtime in the DataRoot
  (`UsmapLocator.cs`). The single-file EXE ships an embedded copy that
  gets seeded into `<DataRoot>\<filename>.usmap` on first run if the
  data root has no usmap yet. After a game version bump, drop a fresh
  Ctrl+Num6 dump into the data root - the newer mtime wins, so it
  silently supersedes the embedded fallback without any code change.

Layout (relative to the **DataRoot** - see Section 3 for resolution rules):

```
<DataRoot>\Sources\Vanilla         ~1097 vanilla item JSONs       (gitignored)
<DataRoot>\Icons                   per-item PNG + JSON sidecars   (gitignored)
<DataRoot>\Profiles\<id>.json      profiles                       (gitignored)
<DataRoot>\Builds                  legacy CLI .pak output         (gitignored, GUI builds go
                                                                   direct to the game's ~mods/)
<DataRoot>\.build-tmp              scratch dir for in-flight builds (gitignored)
<DataRoot>\.webview2               WebView2 cache + cookies       (gitignored)
<DataRoot>\Tools\IconExtractor\..  CUE4Parse-backed extractor     (built from source in dev mode;
                                                                   seeded from embedded zip when
                                                                   DataRoot=QuartermasterData\)
<DataRoot>\*.usmap            UE5 mappings for CUE4Parse     (seeded from embedded on first
                                                              run; user dump wins by mtime)
```

---

## 3. Run the GUI

Two equivalent entry points share the same Kestrel host + frontend:

```powershell
# (a) Desktop launcher (WPF + WebView2). Recommended for end users.
dotnet run --project GUI\App -c Release

# (b) Browser. Recommended when remote-developing or hacking the frontend.
dotnet run --project GUI\Web -c Release
# -> http://localhost:17777

# (c) Single-file build (one .exe, ~74 MB, .NET + WebView2 native libs
#     bundled). Output: GUI\App\bin\Publish\Quartermaster.exe.
dotnet publish GUI\App -p:PublishProfile=win-x64
```

The desktop launcher (`Quartermaster.exe`) hosts Kestrel **in-process** on
a free TCP port (`http://127.0.0.1:0` -> OS-picked port), then opens a
single WPF window with a WebView2 control navigated to that port. Closing
the window calls `IHost.StopAsync` with a 2-second drain, then exits. No
fixed-port collision, multiple instances can run side-by-side.

Requires the **Microsoft Edge WebView2 Runtime** - preinstalled on
Windows 11 and most Windows 10 builds. The launcher pops a clear error
dialog (with the [evergreen installer URL](https://developer.microsoft.com/microsoft-edge/webview2/))
if the runtime is missing.

Both entry points share the exact same `Program.CreateWebApp` builder -
the WPF App project just links the Web project, calls into it, and
delegates DataRoot resolution to `Program.ResolveDataRoot()`. The
resolver walks up from `AppContext.BaseDirectory` looking for a
`Tools\QuartermasterCore\QuartermasterCore.csproj` marker. If found,
that's a dev/repo run and the matching ancestor folder becomes the
DataRoot directly. If nothing matches up to the filesystem root, the
EXE has been deployed somewhere outside its source tree, and DataRoot
falls through to a sibling `QuartermasterData\` folder right next to
the EXE - so the data travels with the EXE (USB-stick portable, no
per-user state hidden in `%APPDATA%`).

We seed from `AppContext.BaseDirectory` rather than
`Environment.CurrentDirectory` / ContentRoot on purpose: the latter
follow whoever invoked the EXE (e.g. starting from a shell that lives
inside the repo would give a false positive on the marker even after
deploy). For a single-file EXE, `AppContext.BaseDirectory` is the
launch directory (where the .exe physically sits), not the self-extract
temp dir, which is exactly where we want `QuartermasterData\` to live.

When `isDeployed=true`, the embedded `*.usmap` resource is written to
the DataRoot if no `*.usmap` is already there, so a fresh EXE drop can
run setup without the user first dumping one via UE4SS. A newer
user-supplied dump (post game-update) is preserved (UsmapLocator picks
the newest by mtime). In dev mode (`isDeployed=false`) we skip the
seed: the on-disk file is the source of truth and you're likely
editing it.

The frontend is similarly embedded:
`GUI/Web/Quartermaster.Web.csproj` strips `wwwroot\**` from the
default `<Content>` set and re-adds it as `<EmbeddedResource>`, with
`<GenerateEmbeddedFilesManifest>true</GenerateEmbeddedFilesManifest>`
so a `ManifestEmbeddedFileProvider("/wwwroot")` resolves the original
paths at runtime. `CreateWebApp` first probes
`<ContentRoot>\wwwroot\index.html` - if that exists (= `dotnet run
--project GUI/Web`), it serves from disk so live CSS/JS edits show up
on refresh. Otherwise it falls back to the embedded provider, which
is the codepath every published EXE takes.

The repo-root `*.usmap` file is embedded into Web.csproj alongside
the wwwroot, with `<LogicalName>Usmap.<filename>.usmap`.
`SeedUsmapIfMissing()` reads that single resource on deployed runs.

The CUE4Parse-backed icon extractor is embedded the same way - but
instead of a single resource we ship the entire publish output as a
zip. `Quartermaster.Web.csproj` declares a `PublishIconExtractorForEmbed`
target gated on `_IsPublishing`: during `dotnet publish` it runs
`dotnet publish` against `Tools/IconExtractor/IconExtractor.csproj`,
zips the resulting `publish/` folder into `IconExtractor.publish.zip`,
and adds it as an `EmbeddedResource` from inside the same target (a
top-level `<EmbeddedResource Condition="Exists(...)">` would evaluate
the condition at project-load time, before the target had a chance to
create the zip). On a deployed run, `SeedIconExtractorIfMissing` opens
the zip from the assembly via `ZipArchive` and unpacks it into
`<DataRoot>\Tools\IconExtractor\publish\` - the existing
`IconExtractorBuilder.Resolve()` short-circuits on the
`IconExtractor.exe` file check and never tries to spawn `dotnet publish`
on the end-user's machine. Plain `dotnet build` skips the pre-publish
target entirely (so no SDK churn for everyday dev builds), and the
in-tree `IconExtractorBuilder` keeps working as before whenever the
source is available.

The single-file publish profile lives at
`GUI/App/Properties/PublishProfiles/win-x64.pubxml`. It enables
`PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract` (so the
WebView2 native loader gets bundled into the .exe and self-extracted to
a temp dir on first run) + `EnableCompressionInSingleFile` (~30%
smaller .exe at the cost of slightly slower cold start). A
post-publish target strips a few inevitable leftover files
(WebView2 XML docs, Web project's deps.json/runtimeconfig.json) so the
publish folder ends up with exactly **one** `Quartermaster.exe`.

Top-level layout:

| Pane | What |
|---|---|
| Header | Profile dropdown + New / Duplicate / Rename / Save / Delete / Build |
| Left | Stack-size globals (None / Multiplier / Absolute, with optional Cap) and a live status block (total / overrides / will-be-modified / promoted) |
| Right | Filterable item list. Each row: icon, localized name, computed target, inline override input |
| Footer | Build log |

Resolver rule (per item, for `MaxCountInSlot`):

```
overrides[itemId].stackSize
?? globals.stackSize.absolute
?? vanilla * globals.stackSize.multiplier (clamped at cap)
?? null      // skip -> stays vanilla in-game
```

For items with `vanillaStack <= 1`, globals only apply when the item is
"promotable" - `ItemClass == Consumable`, or
`ItemType == Inventory.ItemType.Resource`, or `ItemClass == Default && Category == Resource`.
Equipment / NPCs / Ship cannons / Quest tokens at stack=1 stay at 1
unless the user sets an explicit per-item override.

---

## 4. Headless / CLI mode

The GUI binary doubles as a CLI for headless / CI use:

```powershell
# Run setup (dump + icon extraction). Skips steps that are already done.
dotnet run --project GUI\Web -- --setup
dotnet run --project GUI\Web -- --setup --force

# Build a profile, by id or display name
dotnet run --project GUI\Web -- --test-patcher --profile "My Stacks"

# Direct one-shot without a profile (legacy multiplier semantics)
dotnet run --project GUI\Web -- --test-patcher --multiplier 4 --build-pak
dotnet run --project GUI\Web -- --test-patcher --absolute 9999 --build-pak

# Patch only (no pack), keep temp dir for inspection
dotnet run --project GUI\Web -- --test-patcher --multiplier 4 --out .\.build-tmp\inspect
```

---

## 5. HTTP API

The GUI is a thin shell over a small REST API - handy if you want to
drive builds from another tool:

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/items` | list of all `R5BLInventoryItem` JSONs + icon URL + localized meta |
| GET | `/api/profiles` | list of profile summaries |
| GET | `/api/profiles/{id}` | full profile |
| POST | `/api/profiles` | create profile (server picks GUID) |
| PUT | `/api/profiles/{id}` | overwrite profile |
| DELETE | `/api/profiles/{id}` | delete profile |
| POST | `/api/profiles/{id}/duplicate` | clone |
| POST | `/api/build` body: `{profileId}` | run the full pipeline synchronously |
| GET | `/api/setup/status` | probe what's already extracted (Sources/Icons/usmap/Steam pak) |
| POST | `/api/setup/run[?force=true]` | Server-Sent Events stream of the dump + icon extraction |

The `/api/setup/run` stream emits two event types:

- `event: log` - one log line per frame. Step boundaries are tagged with
  `[step:start name=<dump|icons>] ...` and `[step:end name=... ok=true|false]`
  so the frontend (or any consumer) can render collapsible sections.
- `event: done` - terminal frame, payload is `{success: bool, error?: string}`.

A simple-flight guard prevents concurrent setup runs: a second
`POST /api/setup/run` while one is in progress returns `409`.

---

## 6. Profile schema (Profiles\<id>.json)

```json
{
  "id": "11000000-0000-4000-8000-000000000004",
  "name": "x4",
  "description": "All vanilla stacks quadrupled.",
  "createdAt": "2026-05-08T00:00:00+00:00",
  "modifiedAt": "2026-05-08T00:00:00+00:00",
  "globals": {
    "stackSize": { "multiplier": 4 }
  },
  "overrides": {}
}
```

Item-centric overrides + per-property globals. The structure is set up to
grow:

- new property (e.g. `weight`): add a typed global container
  (`globals.weight = { ... }`) and a field on `ItemOverride`
  (`overrides[id].weight`); old profiles stay backward-compatible
  because missing fields are simply `null`.
- new globals modes: extend `StackSizeGlobal` - the resolver picks
  `Absolute` over `Multiplier` already; add new branches as needed.

Profiles live at `Profiles\<id>.json` (gitignored).

---

## 7. Repo layout

```
Stack Size\
+-- Tools\
|   +-- QuartermasterCore\         C# class library:
|   |   +-- Profile / ProfileStore / StackPatcher / PakBuilder / RepakResolver
|   |   +-- BuildPipeline          (patch -> pack -> cleanup)
|   |   +-- SetupRunner            (dump + icon extraction with progress callbacks)
|   |   +-- VanillaDumper          (repak unpack of the AES-encrypted pak)
|   |   +-- IconExtractionRunner   (orchestrates manifest + IconExtractor.exe)
|   |   +-- IconManifestBuilder    (walks Sources/Vanilla, builds the manifest)
|   |   +-- SteamLocator           (registry + libraryfolders.vdf -> vanilla pak)
|   |   +-- UsmapLocator           (newest *.usmap in mod root)
|   |   +-- IconExtractorBuilder   (`dotnet publish` on first use)
|   |   +-- WindrosePaths / WindroseGameSecrets
|   +-- IconExtractor\             C# CLI: pulls UTexture2D + localized strings
|   |                              out of the IoStore container via CUE4Parse
|   +-- CUE4Parse\                 git submodule (UE pak / IoStore reader)
+-- GUI\
|   +-- GUI.csproj                 ASP.NET minimal-API shell (.NET 10)
|   +-- Program.cs                 Wires endpoints + static files
|   +-- Endpoints\                 ItemsEndpoint, ProfilesEndpoint, BuildEndpoint, SetupEndpoint
|   +-- ItemDto.cs, PatcherCli.cs  ...
|   +-- wwwroot\                   index.html (configurator + setup overlay),
|                                  app.css, app.js
+-- App\
|   +-- Quartermaster.App.csproj   WPF + WebView2 desktop wrapper (net10.0-windows)
|   +-- App.xaml(.cs)              Hosts Kestrel in-process via Program.CreateWebApp
|   +-- MainWindow.xaml(.cs)       WebView2 navigated to the dynamic localhost URL
+-- Profiles\
|   +-- <id>.json                  profiles (gitignored)
+-- Sources\Vanilla\               extracted vanilla JSONs (gitignored, auto-extracted)
+-- Icons\                         per-item PNG + sidecar JSON (gitignored, auto-extracted)
+-- Builds\                        finished .pak files (gitignored)
+-- .build-tmp\                    scratch dir for in-flight builds (gitignored)
+-- repak.exe                      auto-downloaded on first use (gitignored)
+-- *.usmap                        generated by UE4SS Ctrl+Num6 (gitignored)
```

---

## 8. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `SHA256 mismatch for repak_cli-*.zip` | Download corrupted, or trumank rotated the pinned release | Delete `repak.exe` and retry; if it persists, bump `PinnedVersion` in `Tools\QuartermasterCore\RepakResolver.cs` |
| `Could not find a Windrose vanilla pak under any Steam library` | Windrose isn't installed via Steam, or it's in a non-standard location Steam doesn't track | Install Windrose, or pass an explicit pak path through the API/CLI |
| `git not found in PATH` during icon setup | Auto-init of the CUE4Parse submodule needs git | Install Git for Windows from https://git-scm.com/download/win, or run `git submodule update --init Tools/CUE4Parse` from another machine and copy the result over |
| `Cannot auto-initialize the CUE4Parse submodule` (no .git directory) | The mod root was downloaded as a zip, not cloned via git | Re-clone the repo (`git clone --recursive ...`) or download CUE4Parse manually from https://github.com/FabianFG/CUE4Parse and place it under `Tools/CUE4Parse/` |
| `No *.usmap file found` | Icon extractor needs a UE5 mappings file. Should never happen on a deployed EXE - one is seeded from embedded resource on first run. Only triggers in dev mode if the repo root has no `.usmap`. | Press Ctrl+Num6 in-game with UE4SS Keybinds active, drop the produced `.usmap` in the mod root |
| Setup overlay shows "Setup is already running (409)" | Two browsers / API clients fired `/api/setup/run` simultaneously | Wait for the first run to finish; subsequent calls succeed |
| `Profile produces no changes - nothing to pack` | Profile has neither globals nor overrides | Pick a Multiplier / Absolute mode, or add at least one override |
| Desktop launcher fails with "Failed to initialize WebView2" | Microsoft Edge WebView2 Runtime missing (rare on Win11, possible on stripped Win10) | Install the [evergreen WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (~1.6 MB bootstrapper) and relaunch |

---

## 9. What this repo does NOT do

- **No auto-deploy.** Server / client paths are not configurable. You
  copy `_P.pak` files into the `~mods` folder yourself.
- **No `.uasset` mods.** This pipeline targets JSON-based R5BusinessRules
  mods. Mesh / material / animation mods need different tools (FModel,
  UAssetGUI, repak unpack/repack).
- **No multi-mod orchestration.** Only one `Quartermaster_*.pak` should live
  in `~mods` at a time; remove the previous build before installing a new one.
