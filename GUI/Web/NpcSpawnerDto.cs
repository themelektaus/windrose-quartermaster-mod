using System.Collections.Generic;

namespace Windrose.Quartermaster.Web;

sealed class NpcSpawnerDto
{
    public string id;          // pak-relative path under A2_Spawners without ".json"; stable override key
    public string name;        // display name (file stem)
    public string category;    // first path segment under A2_Spawners ("(root)" if none)
    public string kind;        // "npc" (swept by the global multipliers) or "other" (resources / chests / nodes)
    public string type;        // $type ("R5GameplaySpawnerParams" / "R5GameplaySpawnerVariantPreset")
    public bool hasRespawn;    // true when the file carries a RespawnInterval
    public int respawnMinutes; // vanilla RespawnInterval.Min / 60 (0 when none)
    public int countMin;       // vanilla min across all Amount blocks
    public int countMax;       // vanilla max across all Amount blocks
    public int amountBlocks;   // number of Amount blocks (variants x collection entries)
    public List<string> mobs;  // distinct spawned-actor stems (e.g. "BP_Mob_Wolf")
}
