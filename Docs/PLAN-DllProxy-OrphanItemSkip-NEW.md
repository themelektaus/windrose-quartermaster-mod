# Plan: DllProxy soll verwaiste Items in qm_items_*.json silent skippen

Status: **Planungsnotiz** - noch nicht umgesetzt. Aufgegriffen wird das, sobald
ein User darüber stolpert oder es einen konkreten Anlass gibt. Auf dem
Test-Server vom 2026-05-28 manuell gelöst.

## Ausgangslage / beobachteter Bug

User-Report 2026-05-28: Im Building-Menü (Build-Mode) erschien ein
Fragezeichen-Slot, der zu nichts mehr aufgelöst hat.

R5.log zeigte den Root-Cause:

```
LogStreaming: Warning: LoadPackage: SkipPackage:
  /Game/Quartermaster/DA_BI_QmBldg_def00b3c
  - The package to load does not exist on disk or in the loader
LogUObjectGlobals: Warning: Objekt
  "Object /Game/Quartermaster/DA_BI_QmBldg_def00b3c.DA_BI_QmBldg_def00b3c"
  konnte nicht gefunden werden
```

Die ID `QmBldg_def00b3c` stand noch in
`R5\Binaries\Win64\qm_items_My-Profile.json`, obwohl:

1. das `.pak` mit dem zugehörigen DataAsset nicht mehr deployed war
2. die Building-ID im aktuellen Quartermaster-Profile nicht mehr existierte

Pipeline-Theorie wie der Drift entsteht:
- Der DllProxy-Hook (`R5\Binaries\Win64\dxgi.dll`) liest beim Spielstart
  `qm_items_*.json` und injiziert jeden Eintrag in die entsprechende
  ItemList / BuildModeList des Spiels.
- Quartermaster schreibt `qm_items_<safeProfile>.json` beim Build neu (siehe
  `GameDeployer.WriteItemsJson`), aber wenn der zugehörige Build nicht mehr
  läuft (z.B. Profile gewechselt, manuelles Pak-Cleanup, alter Test-Build
  Reste), bleibt die JSON-Datei stehen und enthält noch tote Referenzen.
- Bei Profile-Wechsel im Frontend wird per Default nur die aktuelle Profile-JSON
  überschrieben; alte `qm_items_<otherSafeName>.json` neben dran bleiben
  potenziell stale.

## Vorgeschlagener Fix

Im DllProxy-Inject-Code: pro Eintrag aus `qm_items_*.json` einen
"DA-Existence-Check" vor dem Inject:

```cpp
// pseudo: pseudocode in dem die Idee deutlich wird
for (const auto& entry : items) {
    FSoftObjectPath daPath(entry.dataAssetPath);
    UObject* asset = daPath.TryLoad();
    if (!asset) {
        UE_LOG(LogQuartermaster, Warning,
               TEXT("Skipping orphan item '%s': DA package not found"),
               *entry.id);
        continue;
    }
    InjectIntoSlot(entry, asset);
}
```

Konkrete Verifizierung welche Methode der DllProxy tatsächlich nutzt, bevor
implementiert wird (TryLoad blockt I/O; vielleicht reicht ein
`FPackageName::DoesPackageExist` ohne Load, weil der nachfolgende
Inject-Pfad das DA ohnehin laden würde).

## Wo das im Repo anzufassen ist

- DllProxy-Source: vermutlich `External/DxgiProxy/` o.ä.; bei Implementierung
  zuerst per Glob nach dem Inject-Punkt suchen
  (`grep -r "qm_items" External/`).
- Sync-Logik die die finale DLL ausliefert: siehe `Program.cs`
  → `SyncDxgiDllFromEmbedded` (das ist der Embed-Pfad mit SHA256-Stamp,
  wie auch für `binkaudioenc.exe` 2026-05-28 implementiert).

## Tradeoffs / offene Fragen

| Pro | Contra |
|---|---|
| Verhindert Fragezeichen-Slots auch wenn User `~mods` manuell aufräumt. | Verschiebt das Debugging weiter weg: User sieht keinen Hinweis dass ein Eintrag fehlt. Logs bleiben aber. |
| Hilft bei Profile-Switch mit stale Sidecar-JSONs. | Eigentlich ist das ein Symptom davon, dass die `qm_items_*.json`-Lifecycle nicht sauber an Pak-Lifecycle gekoppelt ist. Saubere Lösung wäre die JSON beim Pak-Cleanup ebenfalls zu löschen. |
| Konsistent mit dem Pre-Flight-Check Pattern für Building-Audio (commit `9fba0fa`). | Trivial im Inject-Code, aber Cross-Build (C++/MSVC) deutlich umständlicher als ein C#-Edit. |

## Alternative die zuerst geprüft werden sollte

**JSON-Cleanup beim Quartermaster-Build statt Hook-Härtung.**
`GameDeployer.WriteItemsJson` schreibt schon per-Profile. Was fehlt:

1. Bei Deploy alle `qm_items_<safeName>.json` im `Win64/`-Folder einsammeln.
2. Wenn der zugehörige Safe-Name nicht (mehr) im Profile-Set existiert, JSON
   löschen.
3. Innerhalb der aktiven Profile-JSON dead IDs (Building/Item in Profile aber
   ohne fertig deployten Asset im Pak) raus filtern.

Punkt 3 ist der schwierigere Teil weil "deployed" lazy ist und race-anfällig
sein kann (Build läuft, Pak noch nicht final geschrieben). Die JSON wird aber
direkt nach Pak-Move geschrieben - sollte safe sein.

## Reproduktions-Schritt für späteres Testing

1. Building anlegen, Build & deploy → ID landet in JSON & Pak landet in `~mods`.
2. Profile bearbeiten, ID entfernen, **NICHT** neu builden.
3. Pak manuell aus `~mods` löschen.
4. Spiel starten → Fragezeichen-Slot, R5.log enthält `LoadPackage: SkipPackage:
   /Game/Quartermaster/DA_BI_QmBldg_<id>`.

## Workaround (für jetzt, manuell)

`E:\Games\steamapps\common\Windrose\R5\Binaries\Win64\qm_items_<profile>.json`
öffnen, betroffenen Eintrag aus dem `items`-Array entfernen, speichern.
Beim nächsten Quartermaster-Build wird die Datei ohnehin neu geschrieben.
