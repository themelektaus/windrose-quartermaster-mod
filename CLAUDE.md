# Quartermaster - Windrose Mod-Workspace

Mehrere parallele Mod-Workstreams für das Spiel
[Windrose](https://www.nexusmods.com/windrose) (UE5.6, R5-Build).

## Workstreams

| Bereich | Pfad | Tech |
|---|---|---|
| **Configurator + Pak-Build-Pipeline** | `GUI/`, `Sources/`, `Tools/QuartermasterCore/` | C# .NET 10, WPF-Shell + WebView2 + ASP.NET-Minimal-API, baut `Quartermaster_*.pak` |
| **DLL-Mod (Build-Mode-Slot-Inject)** | `Tools/DllProxy/dxgi/` | C++ DXGI-Proxy, UE5-Reflection-Hook auf `GetBuildingGroupsByCategoryTag` |
| **Asset-Mods** (Pak-only) | diverse `Docs/PLAN-*.md` | Pak-Triplets (`.pak`/`.ucas`/`.utoc`) gebaut via `retoc` |
| **Tooling** | `Tools/ar_writer/`, `Tools/Dumper7Setup/` | Python AR-Patcher, Dumper-7 SDK-Generator |
| **UE-Editor-Templates** | `Tools/UeBuildingItem/` | Python-Helper für UE5.6, erzeugt Mod-Asset-Template (BP + DataAsset) |

## Wichtige Pfade

| Was | Pfad |
|---|---|
| Workspace-Root | `E:\Windrose\Mods\Quartermaster\` |
| Game-Install | `E:\Games\steamapps\common\Windrose\` |
| Deploy-Ziel Paks | `E:\Games\steamapps\common\Windrose\R5\Content\Paks\~mods\` |
| Deploy-Ziel DLL | `E:\Games\steamapps\common\Windrose\R5\Binaries\Win64\dxgi.dll` |
| Deploy-Ziel Sidecars (Profile-JSON + alle `qm_*.txt`/`qm_*.json`) | `E:\Games\steamapps\common\Windrose\R5\Binaries\Win64\Quartermaster\` |
| DLL-Runtime-Log | `%LOCALAPPDATA%\R5\Saved\Logs\Quartermaster_Inject.log` |
| Referenz-Mods (read-only) | `E:\Windrose\Mods\Quartermaster\References\` |
| usmap (UE-Reflection) | `R5-5.6.1-0+UE5-20260518.usmap` (root, Zstd-komprimiert, von Dumper-7) |

Der Game-Install wird automatisch über Steam erkannt. Für Nicht-Steam-Setups
(Epic/GOG/portabel) lässt er sich im Mods-Tab manuell setzen; der Override
persistiert in `<DataRoot>/game-install.json`. Validiert wird gegen eine
`Windrose*.exe` unter `R5\Binaries\Win64\` plus eine Vanilla-Pak.

## DLL-Mod Build/Deploy

Bash-Befehle mit Forward-Slashes und Quotes. Die `.bat`-Scripts
machen `pushd "%~dp0"` als erstes, daher egal von wo aufgerufen.

| Zweck | Befehl |
|---|---|
| Dev-Build (Diag-Logs) | `"E:/Windrose/Mods/Quartermaster/Tools/DllProxy/dxgi/build.bat"` |
| Release-Build (schlank) | `"E:/Windrose/Mods/Quartermaster/Tools/DllProxy/dxgi/build.bat" release` |
| Deploy DLL | `"E:/Windrose/Mods/Quartermaster/Tools/DllProxy/dxgi/deploy.bat"` |
| Uninstall DLL | `"E:/Windrose/Mods/Quartermaster/Tools/DllProxy/dxgi/uninstall.bat"` |
| **Build + Deploy (chained)** | `"E:/Windrose/Mods/Quartermaster/Tools/DllProxy/dxgi/build.bat" && "E:/Windrose/Mods/Quartermaster/Tools/DllProxy/dxgi/deploy.bat"` |

**Workflow-Regel:** Nach jedem erfolgreichen DLL-Build IMMER direkt
deployen - nicht erst auf Bestätigung warten. Chained Pattern mit
`&&` macht das automatisch (Deploy nur bei erfolgreichem Build).

Build-Output: Dev ~190 KB (`log-level=5 diag=1`), Release ~181 KB
(`log-level=3 diag=0`). Build-Type steht beim DLL-Start im Log in
der `Build:`-Zeile.

## Configurator (C# GUI)

Drei Schichten, `GUI/GUI.sln`:

| Schicht | Pfad | Rolle |
|---|---|---|
| **Core** | `Tools/QuartermasterCore/` (`net10.0`) | Library ohne UI: Profil-Modell (`Profile`), Build-Pipeline (`BuildPipeline`), alle Patcher, Vanilla-Extraktion, Tool-Resolver. Wird von Web referenziert. |
| **Web** | `GUI/Web/` (`net10.0`) | ASP.NET-Core-Minimal-API (`Program.cs`) + Frontend in `wwwroot/` (HTML/CSS/JS, Tab-basiert). Routen unter `Endpoints/`. Hat außerdem CLI-Modi (`--setup`, `--test-patcher`). |
| **App** | `GUI/App/` (`net10.0-windows`, WPF) | Desktop-Shell. Startet den Kestrel-Host der Web-Schicht in-process auf einem dynamischen Port und navigiert ein einzelnes WebView2-Fenster dorthin. |

```powershell
# Web-Backend + Frontend allein (Browser, schnellster Dev-Loop)
dotnet run --project GUI\Web        # http://localhost:17777

# Komplette Desktop-App (WPF-Shell + WebView2)
dotnet run --project GUI\App -c Release

# Release-Builds
GUI\build-windows-app.bat           # Quartermaster.exe (WPF, self-contained)
GUI\build-linux-web.bat             # Quartermaster.Web (standalone Server)
```

DataRoot (Profile, `game-install.json`, Vanilla-Dumps): im Dev-Checkout der
Repo-Root (erkannt an `Tools/QuartermasterCore/QuartermasterCore.csproj`),
sonst `QuartermasterData/` neben der EXE.

Mehr Details in `README.md`.

## Code-Konventionen

- **Source of Truth ist der Code.** Kommentare nur für echte, nicht-ableitbare
  Invarianten / Footguns / Sentinel-Verträge. Keine driftenden Referenzen in
  Kommentaren (Pfade, Byte-Offsets, Counts, Historie) - die veralten und lügen.
- **Kein `--` in Frontend-Text oder Kommentaren** - stattdessen einfaches `-`.
- **Dedup mit Augenmaß:** nur byte-identische, entkoppelte Helfer zentralisieren,
  nicht überparametrisieren. Bestehende Shared-Helfer (vor Re-Duplizierung
  prüfen): Core `R5Json`, `ToolProcess`, `GitHubReleaseTool`, `UAssetIo.Ue`
  (Engine-Version-SSOT); Web `RecipeRefHelpers`.
- **Build-Verifikation:** nach Core-Änderungen Core *und* die ganze Solution
  bauen (Web + App konsumieren Core).
- **`References/` ist read-only** - nur Vorlage, wird nie in die Mods integriert.
- **Vanilla-Paks sind AES-verschlüsselt;** der Key liegt zentral in
  `WindroseGameSecrets` (Core), nicht über den Code verstreut.
- **Git:** Conventional-Commits-Stil (`refactor(core): ...`, `fix(install): ...`),
  Commits direkt auf `main`. **NIEMALS `git push`** - niemals, auch nicht auf
  Nachfrage oder bei "OK". Nur lokal committen; der User pusht selbst.

## Pak-Mod Build (Asset-Mods)

Generelle Tools direkt im Workspace-Root:

- `retoc.exe` - IoStore-Container bauen (`.ucas`+`.utoc`)
- `repak.exe` - Legacy-Pak-Wrapper (`.pak`)
- `ffmpeg.exe` - Audio-Konvertierung für Ship-Music

Build-Skripte und Sources liegen pro Mod im jeweiligen `Sources/`-Subfolder.

## Steam-Update Recovery (DLL-Offsets)

Wenn nach einem Game-Update die hardcoded Offsets in
`Tools/DllProxy/dxgi/qm_ue.hpp` nicht mehr stimmen (Symptom:
`init NEVER reached 100000 objects` im Log):

1. Windrose normal via Steam starten + in Welt laden
2. `Tools\Dumper7Setup\run_dump.bat` doppelklicken (F8 = Dump, F6 = Unload)
3. Neue Offsets aus `Tools/Dumper7Setup/output/<UE-Version>/CppSDK/SDK/Basic.hpp`
   übernehmen (`OFFSET_GObjects`, `OFFSET_AppendString`, `OFFSET_GWorld`,
   `OFFSET_ProcessEvent`)
4. Build + Deploy

Note: `qm_scan.cpp` macht zur Laufzeit auch einen Validation-Scan -
falls hardcoded Offsets stale und der Scan greift, sieht man im Log
`[Scan] rescan: GObjects relocated`. Hardcoded Offsets bleiben dann
trotzdem Fallback und sollten nachgezogen werden.

## Submodule

| Modul | Pfad | Upstream |
|---|---|---|
| MinHook | `Tools/DllProxy/dxgi/minhook` | TsudaKageyu/minhook |
| Dumper-7 | `Tools/Dumper7` | Encryqed/Dumper-7 |
| CUE4Parse | `Tools/CUE4Parse` | (Pak-Inspection im Configurator) |
