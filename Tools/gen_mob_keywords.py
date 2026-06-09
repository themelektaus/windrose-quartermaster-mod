#!/usr/bin/env python3
# Generates the mob-keyword catalog for the XP-for-kills GUI.
#
# The DLL (qm_killxp.cpp) matches a config keyword case-insensitively as a SUBSTRING
# of the killed pawn's runtime UClass name (e.g. "Mob_Boar" matches "BP_Mob_BoarF_C").
# Longest matching keyword wins. This script curates family/variant keywords, then
# VERIFIES each keyword's substring matches against the full vanilla BP_Mob_* class
# list so the catalog can't silently over- or under-match.
#
# Input : the authoritative class list pulled from .build-tmp/all-chunks-listing.txt
# Output: Docs/mob-keywords.json  (catalog the frontend NPC-spawn tab consumes)

import json, re, os

# ---- full vanilla BP_Mob_* asset stems (from all-chunks-listing.txt) ----------
RAW = """
BP_Mob_AIController_BlackBeard_Sergeant BP_Mob_AIController_Blackbeard_Regular_Grenadier
BP_Mob_AIController_Blackbeard_Regular_Musketeer BP_Mob_AIController_Blackbeard_Regular_Sailor
BP_Mob_AIController_Boar BP_Mob_AIController_BoarF BP_Mob_AIController_Boar_Charger
BP_Mob_AIController_Boar_Friend BP_Mob_AIController_Boar_Mega BP_Mob_AIController_Crab_Drowned
BP_Mob_AIController_Crew_Officer BP_Mob_AIController_Crew_Regular BP_Mob_AIController_Crocodile
BP_Mob_AIController_CrocodileCorrupted BP_Mob_AIController_Dodo BP_Mob_AIController_DodoF
BP_Mob_AIController_Drowned BP_Mob_AIController_Drowned_Gamescom BP_Mob_AIController_Drowned_Spitter
BP_Mob_AIController_GoatF BP_Mob_AIController_GoatM BP_Mob_AIController_GoatMega
BP_Mob_AIController_Wolf BP_Mob_AlphaWolf BP_Mob_AutoTest_Boar
BP_Mob_BlackBeard_Regular_BaseActor BP_Mob_BlackBeard_Regular_Musketeer
BP_Mob_BlackBeard_Regular_Musketeer_ForQuest_JimGodart BP_Mob_BlackBeard_Regular_Sailor
BP_Mob_BlackBeard_Regular_Sailor_Onboarding BP_Mob_BlackBeard_Regular_Sergeant
BP_Mob_BlackBeard_Regular_Sergeant_BlackMarks BP_Mob_BlackBeard_Regular_Sergeant_ForQuest_LostCargo_01
BP_Mob_BlackBeard_Regular_Sergeant_ForQuest_LostCargo_02 BP_Mob_Blackbeard_Regular_Grenadier
BP_Mob_Blackbeard_Regular_Grenadier_BombThrow_AoEZone
BP_Mob_Blackbeard_Regular_Grenadier_BombThrow_SimpleProjectile
BP_Mob_Blackbeard_Regular_Grenadier_ForQuest_EddieLowe BP_Mob_Boar BP_Mob_BoarF
BP_Mob_Boar_Charger BP_Mob_Boar_Friend BP_Mob_Boar_FriendLvl2 BP_Mob_Boar_Mega
BP_Mob_Crab_Drowned BP_Mob_Crew_Officer_Base BP_Mob_Crew_Officer_Blackbeard
BP_Mob_Crew_Officer_Marlowe BP_Mob_Crew_Officer_Player BP_Mob_Crew_Officer_SteedBonet
BP_Mob_Crew_Regular_Base BP_Mob_Crew_Regular_Blackbeard BP_Mob_Crew_Regular_Player
BP_Mob_Crocodile BP_Mob_CrocodileCorrupted BP_Mob_CrocodileCorrupted_ForQuest_JokesOfGod
BP_Mob_Dodo BP_Mob_DodoF BP_Mob_Drowned_Armored BP_Mob_Drowned_Armored_AITesMap
BP_Mob_Drowned_Armored_Gamescom BP_Mob_Drowned_Naked BP_Mob_Drowned_Naked_AITestMap
BP_Mob_Drowned_Naked_Gamescom BP_Mob_Drowned_Spitter BP_Mob_Drowned_Spitter_ChannelingBeam_Puddle
BP_Mob_Drowned_Throw_Projectile BP_Mob_GiantShaker_GroundDamageZone BP_Mob_Giant_Abscess
BP_Mob_GoatF BP_Mob_GoatM BP_Mob_GoatMega BP_Mob_Regular_Shaman_Caster_GroundDamageZone
BP_Mob_Regular_Shaman_Healer_HealWave_Ribbon BP_Mob_Regular_Shaman_Healer_HealZone
BP_Mob_SenkamatiCorrupted_Giant_Base BP_Mob_SenkamatiCorrupted_Giant_ForQuest_ChickChan
BP_Mob_SenkamatiCorrupted_Giant_Shaker BP_Mob_SenkamatiCorrupted_Giant_Shaker_AIController
BP_Mob_SenkamatiCorrupted_Giant_Shaker_ZoneVisualizer BP_Mob_SenkamatiCorrupted_MeleeWpn_Dart
BP_Mob_SenkamatiCorrupted_MeleeWpn_Macuahuitl BP_Mob_SenkamatiCorrupted_RangeWpn_Dart_Projectile
BP_Mob_SenkamatiCorrupted_Reglar_Shaman_Caster_Totem BP_Mob_SenkamatiCorrupted_Regular_Hunter
BP_Mob_SenkamatiCorrupted_Regular_Hunter_AIController BP_Mob_SenkamatiCorrupted_Regular_Shaman_Caster
BP_Mob_SenkamatiCorrupted_Regular_Shaman_Caster_AIController
BP_Mob_SenkamatiCorrupted_Regular_Shaman_Caster_DeployTotem_Task
BP_Mob_SenkamatiCorrupted_Regular_Shaman_Caster_ReleaseProjectile_Projectile
BP_Mob_SenkamatiCorrupted_Regular_Shaman_Caster_Totem_ReleaseProjectile_LaunchSpline
BP_Mob_SenkamatiCorrupted_Regular_Shaman_Healer BP_Mob_SenkamatiCorrupted_Regular_Shaman_Healer_AIController
BP_Mob_SenkamatiCorrupted_Regular_Thrall_AIController BP_Mob_SenkamatiCorrupted_Regular_Warrior
BP_Mob_SenkamatiCorrupted_Regular_Warrior_AIController BP_Mob_SenkamatiCorrupted_RockThrow_AoEZone
BP_Mob_SenkamatiCorrupted_Thrall BP_Mob_Wolf BP_Mob_Zombie
"""

# runtime UClass FName = asset stem + "_C"
ALL = sorted({s + "_C" for s in RAW.split()})

# ---- classify: only real, player-killable pawns are kill victims --------------
# Everything else (controllers, weapons, projectiles, effect zones, stat assets,
# abstract bases, friendly/player allies, test/onboarding maps) is excluded from
# "killable" but kept around so we can detect when a keyword over-matches them.
NOISE = re.compile(
    r"AIController|_Wpn_|MeleeWpn|RangeWpn|StatCorrection|_Projectile|AoEZone|"
    r"GroundDamageZone|_Puddle|_Ribbon|HealZone|_Totem|_Task|LaunchSpline|"
    r"ZoneVisualizer|BombThrow|ChannelingBeam|Throw_Projectile|GiantShaker_",
    re.I)
ABSTRACT = re.compile(r"_Base_C$|_BaseActor_C$", re.I)
ALLY     = re.compile(r"_Friend|_Player_C$", re.I)
TESTMAP  = re.compile(r"AutoTest|Gamescom|Onboarding|AITestMap|AITesMap|BlackMarks", re.I)
QUEST    = re.compile(r"ForQuest", re.I)

def kind(cls):
    if NOISE.search(cls):    return "noise"
    if ABSTRACT.search(cls): return "abstract"
    if ALLY.search(cls):     return "ally"
    if TESTMAP.search(cls):  return "testmap"
    return "quest" if QUEST.search(cls) else "pawn"

KILLABLE = [c for c in ALL if kind(c) == "pawn"]
QUESTV   = [c for c in ALL if kind(c) == "quest"]

# ---- curated keyword catalog --------------------------------------------------
# (keyword, display label, category, suggested starting XP). suggestedXp is only a
# hint for the GUI - the user/frontend overrides it; default=0 means vanilla.
CATALOG = [
    # --- wildlife ---
    ("Mob_Boar",            "Boar (all variants)",        "Wildlife", 5),
    ("Mob_Boar_Mega",       "Boar - Mega",                "Wildlife", 25),
    ("Mob_Boar_Charger",    "Boar - Charger",             "Wildlife", 10),
    ("Mob_Dodo",            "Dodo (all variants)",        "Wildlife", 3),
    ("Mob_Wolf",            "Wolf",                        "Wildlife", 8),
    ("Mob_AlphaWolf",       "Wolf - Alpha",               "Wildlife", 30),
    ("Mob_Goat",            "Goat (all variants)",        "Wildlife", 6),
    ("Mob_GoatMega",        "Goat - Mega",                "Wildlife", 25),
    ("Mob_Crocodile",       "Crocodile (all variants)",   "Wildlife", 15),
    ("Mob_CrocodileCorrupted", "Crocodile - Corrupted",   "Wildlife", 35),
    ("Mob_Crab",            "Drowned Crab",                "Wildlife", 12),
    ("Mob_Zombie",          "Zombie",                      "Undead",   10),
    # --- drowned (undead humanoids) ---
    ("Mob_Drowned",         "Drowned (all humanoids)",     "Undead",   12),
    ("Drowned_Naked",       "Drowned - Naked",             "Undead",   10),
    ("Drowned_Armored",     "Drowned - Armored",           "Undead",   18),
    ("Drowned_Spitter",     "Drowned - Spitter",           "Undead",   20),
    # --- blackbeard faction (human pirates) ---
    ("Mob_BlackBeard",      "Blackbeard (all regulars)",   "Blackbeard", 10),
    ("Regular_Sailor",      "Blackbeard - Sailor",         "Blackbeard", 8),
    ("Regular_Musketeer",   "Blackbeard - Musketeer",      "Blackbeard", 12),
    ("Regular_Sergeant",    "Blackbeard - Sergeant",       "Blackbeard", 15),
    ("Regular_Grenadier",   "Blackbeard - Grenadier",      "Blackbeard", 15),
    # --- crew officers / named ---
    ("Officer_Blackbeard",  "Officer - Blackbeard",        "Officer",  50),
    ("Officer_Marlowe",     "Officer - Marlowe",           "Officer",  50),
    ("Officer_SteedBonet",  "Officer - Steed Bonet",       "Officer",  50),
    ("Crew_Regular_Blackbeard", "Crew - Blackbeard Regular", "Officer", 20),
    # --- senkamati corrupted faction ---
    ("SenkamatiCorrupted",  "Senkamati Corrupted (all)",   "Senkamati", 15),
    ("SenkamatiCorrupted_Thrall",  "Senkamati - Thrall",          "Senkamati", 10),
    ("Regular_Warrior",     "Senkamati - Warrior",         "Senkamati", 18),
    ("Regular_Hunter",      "Senkamati - Hunter",          "Senkamati", 18),
    ("Shaman_Caster",       "Senkamati - Shaman Caster",   "Senkamati", 22),
    ("Shaman_Healer",       "Senkamati - Shaman Healer",   "Senkamati", 22),
    ("Giant_Shaker",        "Senkamati - Giant Shaker",    "Senkamati", 60),
    ("Giant_Abscess",       "Giant - Abscess",             "Giant",     60),
]

def matches(keyword, pool):
    k = keyword.lower()
    return [c for c in pool if k in c.lower()]

entries = []
for kw, label, cat, xp in CATALOG:
    pawns  = matches(kw, KILLABLE)
    quests = matches(kw, QUESTV)
    bleed  = [c for c in matches(kw, ALL) if kind(c) in ("noise","abstract","ally","testmap")]
    entries.append({
        "keyword": kw,
        "label": label,
        "category": cat,
        "suggestedXp": xp,
        "matchesPawns": pawns,
        "matchesQuestVariants": quests,
        "overMatches": bleed,   # non-pawn classes the substring also hits (harmless: never kill victims, but transparent)
    })

# ---- coverage check: every killable pawn hit by >=1 keyword? ------------------
covered = set()
for e in entries:
    covered.update(e["matchesPawns"])
uncovered = [c for c in KILLABLE if c not in covered]

doc = {
    "version": 1,
    "note": ("XP-for-kills keyword catalog. The DLL matches a keyword case-insensitively "
             "as a substring of the killed pawn's runtime UClass FName; longest match wins. "
             "Put keyword=XP lines in qm_killxp_onkill_<profile>.txt; default=0 is vanilla. "
             "overMatches are non-pawn actors the substring also hits - harmless, they are "
             "never kill victims, listed only for transparency."),
    "keywords": entries,
    "killablePawns": KILLABLE,
    "questVariants": QUESTV,
    "uncoveredPawns": uncovered,
}

out = os.path.join(os.path.dirname(__file__), "..", "Docs", "mob-keywords.json")
out = os.path.abspath(out)
with open(out, "w", encoding="utf-8") as f:
    json.dump(doc, f, indent=2, ensure_ascii=False)

print("wrote", out)
print("killable pawns:", len(KILLABLE), "| keywords:", len(entries), "| quest variants:", len(QUESTV))
print("UNCOVERED pawns:", uncovered if uncovered else "(none - full coverage)")
for e in entries:
    flag = "  <-- OVER-MATCH" if e["overMatches"] else ""
    print(f'  {e["keyword"]:<26} -> {len(e["matchesPawns"])} pawn(s){flag}')
