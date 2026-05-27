# Plan: Ship Music - Add Tracks (statt nur Override)

Status: **Implementiert in commits `2e93f8b` (M2+M3) und `b29a390` (M4+M5).** M6 (ingame-Test + Doc-Polish) offen. Siehe "Implementation Notes" am Ende.

## Ziel

Quartermaster soll dem User erlauben **zusaetzliche Shanty-Tracks** zu den 10 Vanilla-Slots hinzuzufuegen, statt nur existierende Slots zu ueberschreiben. Vorbild: Referenz-Mod `Extra Sea Shanties` aus dem `References/`-Ordner, die 4 zusaetzliche Tracks (DeadHorse, FishintheSea, MaidofAmsterdam, RandyDandyOH) einfuegt.

## Warum das hier (im Gegensatz zu Build-Items) funktionieren wird

Aus den B2.x-Failures wissen wir: bei UE5.6 IoStore scheitert "Add new asset" meistens, weil **AssetManager-Discovery** (PrimaryAssetType-Scan + AssetRegistry) den neuen Eintrag silent verwirft. Das war unsere Mauer bei `R5BuildingItem`.

Ship-Music hat eine fundamental andere Architektur. Discovery laeuft hier nicht ueber AssetRegistry-Scan, sondern ueber **Direct Asset Reference**:

```
DA_Frigate_AudioParams.uasset    (zentrales "Track-Index"-Asset)
  |
  +-- SoftObjectPath[] Tracks_VoicePlayer
  |     +-- CUE_Shanti_01_Large_VoicePlayer
  |     +-- CUE_Shanti_02_Large_VoicePlayer
  |     +-- ...
  |     +-- CUE_Shanti_10_Large_VoicePlayer  (vanilla letzter Slot)
  |     +-- CUE_Shanti_11_Large_VoicePlayer  (mod kann beliebig erweitern)
  |
  +-- SoftObjectPath[] Tracks_VoiceNoPlayer (analog)
        ...

CUE_Shanti_NN_Large_VoicePlayer.uasset (USoundCue)
  |
  +-- SoundNodeWavePlayer -> SWAV_Shanti_<Title>.uasset

SWAV_Shanti_<Title>.uasset + .ubulk
  |
  +-- Bink-encoded audio data (lokales WAV-Encoding haben wir schon)
```

UE5 laedt die DA, iteriert die Liste, laedt jeden referenzierten Cue. **Kein AR-Eintrag noetig, kein PrimaryAssetID-Filter, keine Discovery-Mauer.** Asset-Reference-Graph macht alles.

Verifiziert per String-Dump in der `Extra Sea Shanties`-Mod (`DA_Frigate_AudioParams.uasset`): Tracks 01-14 stehen als Pfad-Strings drin, vanilla hat nur 01-10.

## Was die Referenz-Mod konkret enthaelt (28 Assets, 11.5 MB)

| Layer | Path-Pattern | Anzahl | Funktion |
|---|---|---|---|
| **SWAV (Audio + Bulk)** | `/Game/Audio/Game/Music/Shanti/SWAV/SWAV_Shanti_<Title>` | 4 Tracks x (uasset+ubulk) | Eigentliche Bink-Audio-Daten |
| **Cue Large** | `/Game/Audio/Game/Music/Shanti/Ships/Large/CUE_Shanti_NN_Large_VoicePlayer` | 4 | Cues fuer Large Ships (Frigate) |
| **Cue Medium** | `/Game/Audio/Game/Music/Shanti/Ships/Medium/CUE_Shanti_NN_Medium_VoicePlayer` | 4 | Cues fuer Medium Ships (Brig) |
| **Cue Small** | `/Game/Audio/Game/Music/Shanti/Ships/Small/CUE_Shanti_NN_Small_VoicePlayer` | 4 | Cues fuer Small Ships (Ketch) |
| **Cue VoiceNoPlayer** | `/Game/Audio/Game/Music/Shanti/VoiceNoPlayer/CUE_Shanti_NN_VoiceNoPlayer` | 4 | Variante ohne Voice-Player |
| **Track-Index (DA)** | `/Game/Gameplay/Water/Character/Params/Audio/DA_<ShipType>_AudioParams` | 4 (Brig/Frigate/FrigateNoCrue/Ketch) | Zentrale Track-Listen, vanilla 10 Eintraege -> mod 14 |

**Pro neuem Track sind also 8 Assets noetig:** 1 SWAV-uasset + 1 SWAV-ubulk + 4 CUE-uassets (Large/Medium/Small/NoPlayer) + 4 DA-Patches (eines pro Schiffstyp).

## Vergleich zu unserer existierenden Ship-Music-Override-Pipeline

| Komponente | Existing Override | Add-Tracks (neu) |
|---|---|---|
| WAV -> Bink-Encode | OK (`BinkAudioEncoder.cs`) | OK gleiche Pipeline |
| SWAV-Asset bauen | OK aus Vanilla-Template | OK aus Vanilla-Template, nur anderer Name |
| Cue-Asset bauen | nicht noetig (Slots 01-10 existieren) | **NEU** - Cue-Cloner fuer 4 Varianten pro Track |
| DA-Patching | `BulkReplaceFileName` (Inplace-Rename, gleiche Laenge) | **NEU** - Track-Liste **erweitern** (NameMap + Array extend) |
| Localization | nicht noetig (Shanties haben keine angezeigten Namen?) | tbd - vermutlich nicht noetig |

## Architektur-Wahl: Legacy-Roundtrip statt Zen-Format-Patcher

UE5.6 paks enthalten Zen-cooked Assets (kein Legacy-Header, Magic `0x00000000`). Statt einen direkten Zen-Format-Patcher zu bauen (komplexes Binaer-Format), nutzen wir den bewaehrten retoc-Roundtrip:

```
1. retoc to-legacy <vanilla-utoc> <stage>
   -> Vanilla DA_Frigate_AudioParams.uasset/.uexp (Legacy-Format)

2. Python-Patcher auf Legacy-Asset
   -> NameMap-Extend mit neuen FNames (Track-Pfade)
   -> SoftObjectPath-Array im "Tracks_VoicePlayer" Property erweitern
   -> Export/Import-Tabellen-Konsistenz wahren

3. retoc to-zen <stage>
   -> ucas/utoc mit neuem Zen-cooked DA + PackageStore-Entry

4. repak fuer Stub-Pak
   -> Mountable Mod-Pak
```

Das ist der gleiche Pfad den wir bei B2.4 mit dem Bucket erfolgreich angewendet haben (`to-legacy -> byte-patch -> to-zen`), nur dass wir hier strukturierte Property-Edits statt Byte-Replacements machen muessen.

## Engineering-Phasen

### Phase M1 - Reconnaissance + Asset-Klassifikation (~2h)

Ziel: vanilla DA-Strukturen verstehen, Cue-Strukturen verstehen, Roundtrip-Stabilitaet pruefen.

| Step | Was |
|---|---|
| M1.1 | Vanilla DA_*_AudioParams aus pakchunk0_sX extrahieren (4 Assets). Pfad-Probe via `retoc list` + chunk-id-Match |
| M1.2 | `retoc to-legacy` Roundtrip-Test ohne Aenderung -> bauen + ingame booten -> Shanties spielen vanilla? Wenn ja: Legacy-Roundtrip ist verlustfrei |
| M1.3 | Legacy DA-Asset mit UAssetAPI/.NET oder einem Python-uasset-Parser oeffnen, "Tracks"-Property finden, Array-Element-Format verstehen (FSoftObjectPath = PackageName+AssetName+SubPath, typically 3 FNames) |
| M1.4 | Vanilla CUE_Shanti_10_Large_VoicePlayer extrahieren, Struktur verstehen (welche FNames muessen umgemappt werden: self-name + SWAV-ref) |
| M1.5 | Vanilla SWAV_Shanti_<XX> extrahieren (Vorlage fuer neue SWAVs) |

### Phase M2 - DA-Patcher-Engineering (~6-8h)

Ziel: Python-Tool `tools/da_patcher/` das ein vanilla DA-Asset um N neue Track-Referenzen erweitert.

| Step | Was |
|---|---|
| M2.1 | Legacy .uasset Parser bauen (Header + NameMap + ImportMap + ExportMap + ExportData). Reference: UAssetAPI C#-Source oder fmodel/CUE4Parse |
| M2.2 | "Tracks"-Property-Locator: in ExportData die `TArray<FSoftObjectPath>`-Property finden. Format: PropertyTag + ArrayHeader + N x FSoftObjectPath |
| M2.3 | NameMap-Extender: neue FNames anhaengen (gleiche Logik wie unser `ar_patcher.py`, nur Legacy statt Zen) |
| M2.4 | Array-Extender: neue FSoftObjectPath-Eintraege anhaengen. Achtung: ArrayProperty hat Element-Count + Total-Size-Header, beides muss korrigiert werden |
| M2.5 | Offset-Recalculation: alle nachfolgenden ExportData-Offsets im ExportMap-Header anpassen + SerialOffset-Drift correctness |
| M2.6 | Roundtrip-Verifikation: gepatchtes Legacy-Asset -> retoc to-zen -> retoc to-legacy -> string-dump zeigt unsere neuen FNames in der Track-Liste |

### Phase M3 - Cue-Cloner (~2-3h)

Ziel: aus vanilla `CUE_Shanti_10_*_VoicePlayer.uasset` einen Clone bauen mit:
- Self-FName -> `CUE_Shanti_NN_*_VoicePlayer`
- SWAV-Reference-FName -> `SWAV_Shanti_<UserTitle>`
- Pfad-Strings entsprechend

| Step | Was |
|---|---|
| M3.1 | Vanilla Cue-Asset-Struktur verstehen (Imports auf SWAV, AttenuationSettings, SoundClass) |
| M3.2 | Inplace-FName-Substitution wo moeglich (gleiche-Laenge-Renames). Falls Laengen-Drift noetig: NameMap-Extend reuse |
| M3.3 | Variante fuer Large/Medium/Small/NoPlayer - 4 leicht unterschiedliche Templates |

### Phase M4 - Pipeline-Integration + Backend (~3-4h)

Ziel: Quartermaster-Backend kann pro User-Track-Eintrag:
- WAV ingest -> Bink-encode (haben wir)
- SWAV-Asset generieren (haben wir, leichte Anpassung fuer neuen Asset-Path)
- 4 Cue-Assets generieren via M3 Cue-Cloner
- 4 DA-Assets patchen via M2 DA-Patcher

| Step | Was |
|---|---|
| M4.1 | `Profile.cs`: `ShipMusicAddTracks` Section mit Liste `{title, wavPath, durationSec, ...}` |
| M4.2 | `ShipMusicAddPipeline.cs`: orchestriert M2+M3-Tools + bestehenden Bink-Encoder |
| M4.3 | Build-Endpoint `POST /api/build/ship-music-add`: triggert Pipeline |
| M4.4 | Output: 1 Mod-Pak `QmShantyAdd_P.{pak,ucas,utoc}` mit allen neuen Assets + 4 gepatchten DAs |

### Phase M5 - Frontend (~2h)

| Step | Was |
|---|---|
| M5.1 | Neuer Tab "Ship Music: Add Tracks" |
| M5.2 | Multi-WAV-Upload mit Titel-Input pro Track |
| M5.3 | Conflict-Warnung: "Diese Mod ueberschreibt die DA_*_AudioParams - inkompatibel mit `Extra Sea Shanties` und anderen Mods die das gleiche tun" |

### Phase M6 - Test + Doc (~2h)

| Step | Was |
|---|---|
| M6.1 | E2E-Test mit 1 / 2 / 5 / 20 Tracks |
| M6.2 | Backwards-compat-Test: gleichzeitiges Build von Override + Add - wenn beide das gleiche DA anfassen, last-write-wins |
| M6.3 | Doc-Update + Screenshots |

**Gesamtaufwand-Schaetzung: ~17-21h reine Engineering-Zeit, also ~2-3 Arbeitstage.**

## Risiken und Unbekannte

| Risiko | Wahrscheinlichkeit | Mitigation |
|---|---|---|
| Legacy-Roundtrip ist verlustbehaftet (retoc to-legacy/to-zen drifted ein paar Bytes) | mittel | M1.2 verifiziert das vorab. Falls Drift: zen-direct-patching noetig (~+1 Tag) |
| DA hat verschluesselte/encrypted Tracks-Property | niedrig | UE5 Sound-DataAssets sind normalerweise plain. Falls doch: Engine-Source pruefen |
| Cue-Asset hat versteckte Cross-Refs (SoundClass, Attenuation) die wir uebersehen | mittel | M3.1 sorgfaeltige String-Dump-Analyse |
| Track-Count-Limit im Spiel-Code hardcoded (z.B. max 16 Tracks) | niedrig | Wenn User 50 Tracks adden will -> ingame-Test wuerde zeigen |
| Save-Game speichert "last played track index" -> wenn wir 14 Tracks haben und Save 12 erwartet, evtl. OOB | niedrig | Pruefen Save-Game-Format (vanilla schreibt nur 0-9?) |
| Konflikt mit `Extra Sea Shanties` und aehnlichen Mods | hoch | Frontend warnt explizit. Architekturbedingt - last-pak-wins |

## Plagiat-Fallback (Plan B falls M2 zu lange dauert)

Falls der DA-Patcher in M2 nicht-trivial wird, gibt es einen **schnelleren Workaround**:

Wir nehmen die 4 fertig-gepatchten DA-Assets aus `Extra Sea Shanties` als **statische Templates** und bauen Quartermaster nur darauf:
- **Fester Cap bei 4 Add-Slots** (Slots 11-14)
- Cue-Assets klonen wir aus deren CUE_Shanti_11-14 als Templates und biegen SWAV-Refs um
- DA-Patching entfaellt komplett
- Aufwand: ~4-6h statt ~17-21h

Trade-off: Skalierbarkeit (kein "User kann 20 Tracks adden"). Aber MVP-Mehrwert vorhanden.

## Wiedereinstieg-Checkliste

Wenn die Engineering-Session startet:

1. Doc lesen, Phase M1 reproduzieren - sind die Annahmen ueber Asset-Struktur noch valid?
2. `tools/ar_writer/ar_parser.py` als Architektur-Vorbild rezipieren - DA-Patcher ist konzeptuell aehnlich aber kleineres Asset
3. Mit M1.2 (Roundtrip-Test) beginnen, weil das die groesste Unbekannte ist
4. Falls M1.2 negativ ist: Plagiat-Fallback ueberlegen statt M2 zu starten

## Wiedereinstieg-Checkliste (post-implementation)

Beim Debugging eines neuen Bugs:

1. Repro mit minimal-Profile: 1 added track, kurzes WAV, in `~mods` deployen, ingame Schiff besteigen, Shanty starten
2. Logs:
   - Build-Log in der GUI (zeigt `DONE - ship music extended (N added track(s))` + slot stems)
   - Server-Log fuer `[PreWarm]` / `[Crash]` Lines
   - R5 log unter `%LOCALAPPDATA%\R5\Saved\Logs\`
3. Falls Engine-Crash:
   - retoc-Roundtrip nochmal verifizieren: `.uexp` von gepatchtem DA gegen vanilla mit Probe-Tool dumpen, Cues.Count sollte 10+N sein
   - Save-Game-Format anschauen ob "last played track index" persistiert wird
4. Falls Tracks nie drankommen:
   - DA ingame inspizieren via UE5SS Live Editor falls verfuegbar
   - Crew-Shanty-Picker-Code suchen (R5 sources via dumpsxact oder fmodel)

## Referenz-Artefakte

| Pfad | Was |
|---|---|
| `References/Extra Sea Shanties/ExtraSeaShanties_P.utoc` | Source der Referenz-Mod (28 Assets, 11.5 MB) |
| `.build-tmp/shanties-recon/` | Vollstaendig unpacked (per `retoc unpack`) als Read-Reference - bei Bedarf neu erzeugen, der Ordner ist gitignored und wird in jeder Recon-Session aufgeraeumt |
| `tools/ar_writer/ar_parser.py` | Architektur-Vorbild fuer NameMap-Extension-Logik |
| `BinkAudioEncoder.cs` | WAV -> Bink-Codec (bereits in unserer Override-Pipeline aktiv) |

## Implementation Notes (post-recon)

Die ausgefuehrte Implementierung weicht in mehreren Punkten von den ursprueng-
lichen Annahmen ab. Die folgenden Befunde gelten als die Wahrheit auf disk.

### Tatsaechliche DA-Property-Struktur

Nicht `Tracks_VoicePlayer` / `Tracks_VoiceNoPlayer` (das war geraten), sondern:

```
DA_<ShipType>_AudioParams (R5ShipAudioParams)
  Shanty : R5ShipShantySoundData {
    Cues : Array<R5ShantyCuData>[10] {
      [i] {
        AutonomousShantySound : ObjectProperty -> Import(SoundCue: CUE_Shanti_<i+1>_<Variant>_VoicePlayer)
        SimulatedShantySound  : ObjectProperty -> Import(SoundCue: CUE_Shanti_<i+1>_VoiceNoPlayer)
      }
    }
    ShantyFadeIn : Float<0.1>
    TechSoundMix : Object<MIX_Shanti>
    AttachSocketName : Name<SoundCrue>
  }
  ...
}
```

Wichtige Konsequenzen:
- Cue-Refs sind **`ObjectProperty` mit hard ImportMap-Refs**, nicht `FSoftObjectPath`. Daher braucht jeder neue Cue 2 ImportMap-Eintraege (Package + SoundCue).
- Jeder DA referenziert eine **andere VoicePlayer-Variante**:
  - `DA_Brig_AudioParams`         -> CUE_Shanti_N_**Medium**_VoicePlayer
  - `DA_Frigate_AudioParams`      -> CUE_Shanti_N_**Large**_VoicePlayer
  - `DA_FrigateNoCrue_AudioParams`-> CUE_Shanti_N_**Large**_VoicePlayer
  - `DA_Ketch_AudioParams`        -> CUE_Shanti_N_**Small**_VoicePlayer
  - `_VoiceNoPlayer` ist shared in allen 4 DAs.

### Cue-Cloning ist trivial

Vanilla `CUE_Shanti_10_<flavor>` ist ein 62-Export-Soundcue mit Layering (14
SWAV-Refs: ShipsChatter calls + ein "SWAV_Shanti_MaggieMay"-Lead). Refmod
`Extra Sea Shanties` clont diesen Cue **byte-equivalent** und renamt nur
vier FName-Strings via NameMap-Replace:
- Self-stem (CUE_Shanti_10_* -> CUE_Shanti_N_*)
- Self-path (analog mit `/Game/`-Praefix)
- SWAV-stem (SWAV_Shanti_MaggieMay -> SWAV_Shanti_<UserStem>)
- SWAV-path (analog)

Das bedeutet: **kein Cue-Aufbau from scratch noetig** - `UAssetAPI.SetNameReference`
auf die 4 NameMap-Indices reicht. Export[0].ObjectName wird automatisch updated
weil sie ueber den NameMap-Index referenziert.

### retoc to-zen Roundtrip

`.uexp` ist nach `retoc to-legacy -> UAssetAPI patch -> retoc to-zen ->
retoc to-legacy` byte-identisch (verifiziert im M1.2-Recon). `.uasset` ist
strukturell gleich aber Bytes drifted (Hash/UUID-Felder, NameMap-Order) -
das ist normal weil retoc das Legacy-Format als View auf Zen-internes
generiert. Ingame ist die Asset semantisch identisch.

### Aufwand-Realitaet vs. Schaetzung

| Phase | Schaetzung | Tatsaechlich |
|---|---|---|
| M1 Recon                | ~2h      | ~1.5h |
| M2 DA-Patcher           | ~6-8h    | ~30min Probe + 1h Production (UAssetAPI macht 95%) |
| M3 Cue-Cloner           | ~2-3h    | ~30min Probe + 30min Production (NameMap-Replace) |
| M4 Pipeline+Backend     | ~3-4h    | ~2h |
| M5 Frontend             | ~2h      | ~1h |
| **Gesamt (ohne M6)**    | ~15-19h  | **~6.5h** |

Die Schaetzung hat den UAssetAPI-Hebel und die Cue-Clone-Trivialitaet (die
Refmod-Reverse-Engineering offengelegt hat) deutlich unterschaetzt. Ein
Plan-B (statische Refmod-DA-Templates) wurde damit unnoetig.

### Implementation Layout

| Layer | Datei | Kommentar |
|---|---|---|
| Patcher M2 | `Tools/QuartermasterCore/ShipMusicAddDaPatcher.cs` | Extended Shanty.Cues + 2*N Imports |
| Patcher M3 | `Tools/QuartermasterCore/ShipMusicAddCueCloner.cs` | 4 NameMap-Replacements pro Cue-Variante |
| Pipeline   | `Tools/QuartermasterCore/ShipMusicAddPipeline.cs` | Helper + Job/Result-Types |
| Build      | `Tools/QuartermasterCore/BuildPipeline.cs` | `ResolveShipMusicAddJobs` + IoStoreCompositeSource-Setup |
| Profile    | `Tools/QuartermasterCore/Profile.cs` | `ShipMusicAddGlobal { Tracks: List<ShipMusicAddedTrack> }` |
| Storage    | `Tools/QuartermasterCore/WindrosePaths.cs` | `ProfileShipMusicAddTrackDir(id, trackKey)` |
| Build API  | `GUI/Web/Endpoints/BuildEndpoint.cs` | `shipMusicAdd: {ucasPath, utocPath, tracks: [...]}` |
| Profile API| `GUI/Web/Endpoints/ProfilesEndpoint.cs` | `GET/POST/DELETE /api/profiles/{id}/ship-music-add` |
| Tab        | `GUI/Web/wwwroot/tabs/shipmusicadd.{html,css,js}` | "Ship Music+" tab with Add form + Track list |
| Glue       | `GUI/Web/wwwroot/app.js`, `index.html` | TAB_NAMES, lifecycle, build-log renderer |

### Offene Risiken fuer M6 (ingame-Test)

| Risiko | Was passieren koennte | Mitigation |
|---|---|---|
| Save-Game speichert "last played track index" mit fixem cap | Spieler joint, save versucht index 12 zu laden, OOB-crash | Vanilla mit 10 Slots testen ob save-game ueberhaupt einen index speichert. Falls ja: monkey-patch im Engine-Hook |
| Crew-Shanty-Picker bias | Engine waehlt Cues[0..9] nur (hardcoded count) | Reverse-engineer den Picker (vermutlich `randInt(0, Cues.Num())`); falls hardcoded 10 = Fix-Plan B |
| Konflikt mit anderen Mods die DA_*_AudioParams anfassen | Last-pak-wins; ein anderer Mod ueberschreibt unsere DA | Frontend warnt explizit. Architektur-bedingt. |
| ImportMap-Drift in retoc to-zen | retoc re-orderet Imports und zerstoert die ObjectProperty-Refs | M1.2 hat zwar .uexp byte-equal nachgewiesen, aber das war ohne Import-Adds. Falls Bug: importmap-Order-stabilisierung in UAssetAPI-Write erzwingen |
