-- VanillaItemDumper / main.lua
--
-- One-shot dumper that walks every loaded R5BLInventoryItem instance, reads
-- each property via UE4SS reflection (class:ForEachProperty), and serialises
-- the result to JSON in the same shape used by the existing
-- "Stack_Size_Changes_x04_P" mod.
--
-- Why a dumper at all?
--   The R5BusinessRules item DataAssets do *not* live in pakchunk0; they are
--   loaded from C++ defaults at runtime, so there is no "vanilla JSON" to copy
--   from disk. To author a fresh stack/weight/loot mod against the latest game
--   version we first need to capture the actual runtime values.
--
-- Output strategy:
--   UE4SS Lua cannot create directories (os.execute / io.popen deadlock in this
--   embedding — see WindrosePlus events.lua / rcon.lua). To stay in pure Lua
--   we write *flat* into Dumps/ with the package path encoded in the filename
--   using "___" as a directory separator, e.g.
--     Dumps/R5___Plugins___R5BusinessRules___Content___InventoryItems___Ammo___DA_AID_Ammo_Cannonball_RegularCannonball_T01.json
--   The companion PowerShell wrapper (Dump-WindroseVanilla.ps1, planned for
--   step 2) reorganises these into the conventional R5/Plugins/... tree under
--   Modding\src\Vanilla\.
--
-- Trigger:
--   Polled via LoopAsync because LoadMap-style hooks fire inconsistently on a
--   dedicated server (no real map transition). We poll FindAllOf until items
--   show up, then dump once and exit the loop. This is the same pattern used
--   by BellLimits_10x.

local json = require("json")

-- ---------------------------------------------------------------------------
-- Config
-- ---------------------------------------------------------------------------

local TARGET_CLASS    = "R5BLInventoryItem"
local POLL_INTERVAL   = 2000   -- ms between FindAllOf attempts
local MAX_RETRIES     = 30     -- ~60 s before giving up
local PATH_SEPARATOR  = "___"  -- on-disk encoding of "/"
local LOG_TAG         = "[VanillaItemDumper]"

-- Test/diagnostic constraints. Set MAX_DUMPS = 0 for a full run.
-- NAME_FILTER is matched as a substring against the object's GetFullName()
-- result -- "" disables filtering.
local MAX_DUMPS       = 0
local NAME_FILTER     = ""

-- UE4SS' embedded Lua print() does NOT append a newline, so consecutive
-- print() calls (and the timestamps the host prepends to each line) end up
-- concatenated into one long "wurst". Every other UE4SS mod in this repo
-- (BellLimits, WindrosePlus, ConsoleEnabler, ...) works around this by
-- appending "\n" manually -- we centralise that here.
local function log(msg)
    print(LOG_TAG .. " " .. msg .. "\n")
end
local function logf(fmt, ...)
    print(LOG_TAG .. " " .. string.format(fmt, ...) .. "\n")
end

-- ---------------------------------------------------------------------------
-- Path / filesystem helpers
-- ---------------------------------------------------------------------------

-- Resolve our own mod directory from debug.getinfo so we can write Dumps/
-- next to Scripts/ without hard-coding the install location. Same trick as
-- WindrosePlus uses to find its game root.
local function resolveModDir()
    local info = debug.getinfo(1, "S")
    if info and info.source then
        local src = info.source:gsub("^@", "")
        -- Expected: <...>\ue4ss\Mods\VanillaItemDumper\Scripts\main.lua
        local base = src:match("^(.+)[/\\]Scripts[/\\]main%.lua$")
        if base then return base end
    end
    return "."
end

local MOD_DIR  = resolveModDir()
local DUMP_DIR = MOD_DIR .. "\\Dumps"

-- Probe-write an empty file to confirm the Dumps directory exists. We can't
-- create the directory from Lua, so the wrapper script is responsible for
-- pre-creating it; if the probe fails we log loudly and bail rather than
-- silently writing nothing.
local function dumpsWritable()
    local probe = DUMP_DIR .. "\\.write_probe"
    local f = io.open(probe, "w")
    if not f then return false end
    f:close()
    os.remove(probe)
    return true
end

-- Convert "/R5BusinessRules/InventoryItems/Ammo/DA_AID_X" into the on-disk
-- mod-relative tree path, then flatten to a single filename using
-- PATH_SEPARATOR. Mount-prefix mapping mirrors the layout the .pak ships:
--   /R5BusinessRules/...  ->  R5/Plugins/R5BusinessRules/Content/...
--   /Game/...             ->  R5/Content/...
local function packagePathToTreePath(pkgPath)
    if not pkgPath or pkgPath == "" then return nil end
    local p = pkgPath
    if p:sub(1, 1) == "/" then p = p:sub(2) end       -- drop leading /

    -- /R5BusinessRules/... is the most common case for inventory data.
    local rest = p:match("^R5BusinessRules/(.+)$")
    if rest then
        return "R5/Plugins/R5BusinessRules/Content/" .. rest
    end
    -- /Game/... maps to R5/Content/... (same convention as repak unpack).
    rest = p:match("^Game/(.+)$")
    if rest then
        return "R5/Content/" .. rest
    end
    -- Other plugins (defensive — we may discover items mounted elsewhere):
    --   /<PluginName>/...  ->  R5/Plugins/<PluginName>/Content/...
    local plugin, sub = p:match("^([^/]+)/(.+)$")
    if plugin and sub then
        return "R5/Plugins/" .. plugin .. "/Content/" .. sub
    end
    return p
end

local function treePathToFlatName(treePath)
    return (treePath:gsub("/", PATH_SEPARATOR)) .. ".json"
end

-- GetFullName() returns "<ClassName> <PackagePath>.<AssetName>". We strip the
-- class prefix and the trailing ".AssetName" to recover the package path.
local function extractPackagePath(fullName)
    if not fullName then return nil end
    local _, _, packageAndAsset = fullName:find("%s(.+)$")
    if not packageAndAsset then packageAndAsset = fullName end
    -- Drop the trailing .AssetName (UE always repeats it).
    local pkg = packageAndAsset:match("^(.-)%.[^%.]+$") or packageAndAsset
    return pkg
end

-- ---------------------------------------------------------------------------
-- Property walker (property-type aware)
-- ---------------------------------------------------------------------------
--
-- The first iteration of this walker probed values heuristically (does it have
-- :GetFullName? :ToString? :GetArrayNum?). That breaks for sub-objects and
-- sub-structs, because those *also* respond to :GetFullName -- so e.g.
-- InventoryItemGppData (a struct/inline UObject) was emitted as the class
-- path "/Script/R5BusinessRules.R5BLInventoryItemGPP" instead of being walked
-- recursively for its fields (MaxCountInSlot, Weight, ...).
--
-- Fix: walk class properties via ForEachProperty, and for each property read
-- the property's *type* (prop:GetClass():GetFName():ToString()) -- e.g.
-- "StructProperty", "ObjectProperty", "NameProperty", "FloatProperty", ...
-- That tells us unambiguously which decoder to use. UE4SS guarantees this
-- API; it's the same approach wp.fields uses in WindrosePlus admin.lua.
--
-- Property type -> JSON shape:
--   StructProperty        -> nested object (recurse)
--   ObjectProperty,
--   WeakObjectProperty    -> if val has children we can walk -> nested object
--                            (instanced sub-object, e.g. InventoryItemGppData);
--                            otherwise the asset path string, "None" if null
--   ClassProperty,
--   SoftObjectProperty,
--   SoftClassProperty     -> path string ("None" if null)
--   NameProperty          -> string ("None" if null)
--   StrProperty           -> string
--   TextProperty          -> best-effort string (FText has no direct prop tree)
--   BoolProperty          -> bool
--   Float/Double/Int*/UInt* -> number
--   ByteProperty,
--   EnumProperty          -> enum name string (or number for raw bytes)
--   ArrayProperty         -> JSON array; element type discovered the same way
--   MapProperty / SetProperty -> stringified for now (rare here)

local walkPropertyValue       -- forward decl (recursive)
local walkStructProperties    -- forward decl (UObject / UClass-based)
local walkStructValue         -- forward decl (UScriptStruct-based)

-- ---------------------------------------------------------------------------
-- Empty TSoftObjectPtr<X> struct collapse
-- ---------------------------------------------------------------------------
--
-- An unset TSoftObjectPtr<...> StructProperty walks to a JSON object of the
-- form:
--   { "AssetPath": { "PackageName": "None", "AssetName": "None" } }
-- The Stack-mod reference (and the R5 JSON loader's preferred form) collapse
-- this to the literal string "None":
--   "ConsumableData": "None"
-- Behaviour was verified via PropReflectProbe v5 Block 4 -- both PackageName
-- and AssetName always come back as the FName "None" for unset soft pointers.
-- We only collapse when the entire structure matches that shape; populated
-- soft pointers (which we can't see anyway, see _softProbeBudget note) would
-- not collapse.
local function isAllNoneSoftStruct(out)
    if type(out) ~= "table" or out.__kind ~= "obj" then return false end
    if #out._order ~= 1 then return false end
    if out._order[1] ~= "AssetPath" then return false end
    local ap = out._values.AssetPath
    if type(ap) ~= "table" or ap.__kind ~= "obj" then return false end
    local pn = ap._values.PackageName
    local an = ap._values.AssetName
    if pn ~= "None" or an ~= "None" then return false end
    -- SubPathString may or may not be present -- if it is, must be empty/None.
    local sub = ap._values.SubPathString
    if sub ~= nil and sub ~= "" and sub ~= "None" then return false end
    return true
end

-- Strip the "<ClassName> " prefix UE prepends to GetFullName().
-- "Texture2D /Game/UI/HUD/Foo.Foo" -> "/Game/UI/HUD/Foo.Foo"
local function stripFullName(full)
    if type(full) ~= "string" or full == "" then return nil end
    return full:match("%s(.+)$") or full
end

local function objectFullName(v)
    if v == nil then return nil end
    local full
    local ok = pcall(function() full = v:GetFullName() end)
    if not ok then return nil end
    return stripFullName(full)
end

-- Some UE4SS userdata wrappers expose :IsValid(); treat invalid handles as null.
local function isLiveUd(v)
    if v == nil then return false end
    if type(v) ~= "userdata" then return true end
    local ok, valid = pcall(function() return v:IsValid() end)
    if ok and valid == false then return false end
    return true
end

-- ---------------------------------------------------------------------------
-- Soft object/class path resolution
-- ---------------------------------------------------------------------------
--
-- TSoftObjectPtr / TSoftClassPtr / FSoftObjectPath properties don't carry a
-- live UObject pointer at CDO time, so v:GetFullName() returns "None". We
-- need the asset *path* the soft pointer holds before resolution.
--
-- Strategy history:
--   v1 used :ToSoftObjectPath():ToString() and :ToString() directly. That
--   returned "None" on this UE4SS build for unresolved soft refs (the native
--   FSoftObjectPath::ToString() seems to early-out when no live UObject is
--   present).
--   v1 also called val:Get() as a last resort, which actually triggers an
--   *asset load* on a dedicated server. That re-entered native code that
--   pcall cannot catch and crashed the whole process inside UE4SS.dll
--   (~50-frame recursive stack trace, last successful dump was item #861).
--
-- v2 (current): bypass ToString entirely and read the FTopLevelAssetPath
-- sub-fields (PackageName, AssetName) as FNames. They're populated even when
-- the soft pointer is unresolved. We never call :Get() -- ever.
--
-- Goal: return strings like
--   "/Game/TMP/ResourcesTEMP/Resources/SM_X.SM_X"
-- matching the format produced by FSoftObjectPath::ToString() when the asset
-- IS loaded.

-- Soft-path probing is now silenced (set to 0). PropReflectProbe v3-v5 verified
-- that no UE4SS Lua method can read the asset path of a TSoftObjectPtr CDO
-- without crashing -- ItemMesh/ItemTexture/AmmoBaseParams will always come back
-- as "None" from this dumper, and the merge step in Apply-StackMultiplier.ps1
-- pulls them from the Stack-Size reference mod (only canonical workaround).
local _softProbeBudget   = 0
local _softSuccessLogged = 0
local MAX_SUCCESS_LOG    = 0

-- Convert a value that *might* be an FName (userdata) or a plain string into
-- a Lua string, or nil if we can't.
local function fnameToStr(v)
    if v == nil then return nil end
    if type(v) == "string" then return v end
    if type(v) ~= "userdata" then return nil end
    local s
    pcall(function() s = v:ToString() end)
    if type(s) == "string" then return s end
    return nil
end

-- Combine an FTopLevelAssetPath's package + asset (+ optional sub) into the
-- canonical "/Path/To/Pkg.AssetName" form.
local function combinePackageAndAsset(pkg, asset, sub)
    if type(pkg) ~= "string" or pkg == "" or pkg == "None" then return nil end
    local result = pkg
    if type(asset) == "string" and asset ~= "" and asset ~= "None" then
        result = result .. "." .. asset
    end
    if type(sub) == "string" and sub ~= "" then
        result = result .. ":" .. sub
    end
    return result
end

-- Read an FTopLevelAssetPath userdata's PackageName + AssetName fields and
-- format them. Used both for FSoftObjectPath.AssetPath and for direct
-- FTopLevelAssetPath properties.
local function topLevelAssetPathToStr(tlap)
    if tlap == nil then return nil end
    local pkg, asset
    pcall(function() pkg = fnameToStr(tlap.PackageName) end)
    pcall(function() asset = fnameToStr(tlap.AssetName) end)
    return combinePackageAndAsset(pkg, asset, nil)
end

-- Read an FSoftObjectPath userdata's components (AssetPath sub-struct +
-- SubPathString). This is the bypass for the broken native ToString().
local function softObjectPathToStr(sop)
    if sop == nil then return nil end
    local sub
    pcall(function() sub = sop.SubPathString end)

    -- Primary: walk into FTopLevelAssetPath sub-struct.
    local ap
    pcall(function() ap = sop.AssetPath end)
    if ap ~= nil then
        local pkg, asset
        pcall(function() pkg = fnameToStr(ap.PackageName) end)
        pcall(function() asset = fnameToStr(ap.AssetName) end)
        local combined = combinePackageAndAsset(pkg, asset, sub)
        if combined then return combined end
    end

    -- Secondary: GetAssetPathString() on FSoftObjectPath, if available.
    local s
    pcall(function() s = sop:GetAssetPathString() end)
    if type(s) == "string" and s ~= "" and s ~= "None" then
        if type(sub) == "string" and sub ~= "" then
            return s .. ":" .. sub
        end
        return s
    end

    return nil
end

-- Describe a value in a single human-readable line: type + (string contents |
-- userdata's :ToString() if any | <userdata: addr>). Used so probes show what
-- a getter ACTUALLY returned instead of just "<userdata>".
local function describe(v)
    if v == nil then return "nil" end
    local t = type(v)
    if t == "string" then return "str:'" .. v .. "'" end
    if t == "number" then return "num:" .. tostring(v) end
    if t == "boolean" then return "bool:" .. tostring(v) end
    if t == "userdata" then
        local addr = tostring(v) -- something like "userdata: 0x..."
        local s
        pcall(function() s = v:ToString() end)
        if type(s) == "string" then
            return "ud:'" .. s .. "' (" .. addr .. ")"
        end
        return "ud:<no-ToString> (" .. addr .. ")"
    end
    return t .. ":" .. tostring(v)
end

local function softProbeLog(val, attempted)
    if _softProbeBudget <= 0 then return end
    _softProbeBudget = _softProbeBudget - 1

    -- Each strategy gets its own line in the log so we can clearly see which
    -- ones returned nil vs userdata vs string. Previous probe lumped everything
    -- on one line and silently dropped nil-returning getters.
    local function probe(label, getter)
        local raw
        local ok, err = pcall(function() raw = getter() end)
        if not ok then
            log("  probe " .. label .. " => ERR: " .. tostring(err))
            return
        end
        log("  probe " .. label .. " => " .. describe(raw))
    end

    log("soft-probe START attempted=" .. tostring(attempted) ..
        " val=" .. describe(val))

    probe("val:ToString()",           function() return val:ToString() end)
    probe("val:GetAssetPathString()", function() return val:GetAssetPathString() end)
    probe("val:GetAssetPathName()",   function() return val:GetAssetPathName() end)
    probe("val:GetLongPackageName()", function() return val:GetLongPackageName() end)
    probe("val:ToStringReference()",  function() return val:ToStringReference() end)
    probe("val:ToSoftObjectPath()",   function() return val:ToSoftObjectPath() end)

    probe("val.AssetPathName",        function() return val.AssetPathName end)
    probe("val.AssetPathName.PackageName",
        function() return val.AssetPathName.PackageName end)
    probe("val.AssetPathName.AssetName",
        function() return val.AssetPathName.AssetName end)
    probe("val.AssetPathName:ToString()",
        function() return val.AssetPathName:ToString() end)

    probe("val.SubPathString",        function() return val.SubPathString end)
    probe("val.SubPathString:ToString()",
        function() return val.SubPathString:ToString() end)

    probe("val.AssetPath",            function() return val.AssetPath end)
    probe("val.AssetPath.PackageName",
        function() return val.AssetPath.PackageName end)
    probe("val.AssetPath.AssetName",
        function() return val.AssetPath.AssetName end)
    probe("val.AssetPath:ToString()",
        function() return val.AssetPath:ToString() end)

    -- pairs() over the userdata: works on some UE4SS builds, fails silently on
    -- others. If it works we get a free dump of every exposed field.
    local pairsOk, pairsErr = pcall(function()
        local count = 0
        for k, v in pairs(val) do
            count = count + 1
            log("  pairs[" .. tostring(k) .. "] = " .. describe(v))
            if count > 20 then
                log("  pairs: stopping at 20")
                return
            end
        end
        if count == 0 then
            log("  pairs: (empty)")
        end
    end)
    if not pairsOk then
        log("  pairs: ERR " .. tostring(pairsErr))
    end

    log("soft-probe END")
end

local function noteSoftSuccess(strategy, result)
    if _softSuccessLogged >= MAX_SUCCESS_LOG then return end
    _softSuccessLogged = _softSuccessLogged + 1
    log("soft-resolve OK via " .. strategy .. " -> " .. tostring(result))
end

local function resolveSoftPath(val)
    if val == nil then return nil end
    if type(val) == "string" then
        if val == "" or val == "None" then return nil end
        return val
    end
    if type(val) ~= "userdata" then return nil end

    -- A) val IS an FSoftObjectPath (StructProperty case): read components.
    local s = softObjectPathToStr(val)
    if s then
        if _softSuccessLogged < MAX_SUCCESS_LOG then
            noteSoftSuccess("FSoftObjectPath.AssetPath", s)
        end
        return s
    end

    -- B) val is TSoftObjectPtr / TSoftClassPtr -> get inner FSoftObjectPath.
    local sop
    pcall(function() sop = val:ToSoftObjectPath() end)
    if sop ~= nil then
        s = softObjectPathToStr(sop)
        if s then
            if _softSuccessLogged < MAX_SUCCESS_LOG then
                noteSoftSuccess("ToSoftObjectPath()->AssetPath", s)
            end
            return s
        end
    end

    -- C) val is FTopLevelAssetPath directly (rare but possible).
    s = topLevelAssetPathToStr(val)
    if s then
        if _softSuccessLogged < MAX_SUCCESS_LOG then
            noteSoftSuccess("FTopLevelAssetPath.PackageName", s)
        end
        return s
    end

    -- D) GetAssetPathString() on the wrapper directly (clean engine API).
    pcall(function() s = val:GetAssetPathString() end)
    if type(s) == "string" and s ~= "" and s ~= "None" then
        if _softSuccessLogged < MAX_SUCCESS_LOG then
            noteSoftSuccess("GetAssetPathString()", s)
        end
        return s
    end

    -- E) GetAssetPathName() returns an FName, then :ToString().
    local apn
    pcall(function() apn = val:GetAssetPathName() end)
    s = fnameToStr(apn)
    if s and s ~= "" and s ~= "None" then
        if _softSuccessLogged < MAX_SUCCESS_LOG then
            noteSoftSuccess("GetAssetPathName():ToString", s)
        end
        return s
    end

    -- F) Older UE4SS member name AssetPathName -- might be an FName.
    pcall(function() apn = val.AssetPathName end)
    s = fnameToStr(apn)
    if s and s ~= "" and s ~= "None" then
        if _softSuccessLogged < MAX_SUCCESS_LOG then
            noteSoftSuccess(".AssetPathName (as FName)", s)
        end
        return s
    end

    -- F.5) UE5 quirk: some UE4SS builds expose .AssetPathName as the
    -- FTopLevelAssetPath itself (NOT an FName), so its sub-fields are what
    -- we actually need.
    if apn ~= nil and type(apn) == "userdata" then
        local pkg, asset
        pcall(function() pkg = fnameToStr(apn.PackageName) end)
        pcall(function() asset = fnameToStr(apn.AssetName) end)
        local sub
        pcall(function() sub = val.SubPathString end)
        if type(sub) == "userdata" then
            local subStr
            pcall(function() subStr = sub:ToString() end)
            sub = subStr
        end
        local combined = combinePackageAndAsset(pkg, asset, sub)
        if combined then
            if _softSuccessLogged < MAX_SUCCESS_LOG then
                noteSoftSuccess(".AssetPathName.{PackageName,AssetName}", combined)
            end
            return combined
        end
    end

    -- G) Last-ditch: ToString() on the wrapper. We try this LAST because on
    --    this UE4SS build it returned "None" for unresolved soft refs, but
    --    on builds where it works it's the cleanest answer.
    pcall(function() s = val:ToString() end)
    if type(s) == "string" and s ~= "" and s ~= "None" then
        if _softSuccessLogged < MAX_SUCCESS_LOG then
            noteSoftSuccess("ToString()", s)
        end
        return s
    end

    -- NOTE: we deliberately do NOT call val:Get() here. :Get() asks the asset
    -- loader to resolve the soft ref, which on a dedicated server can re-enter
    -- native code in a way pcall cannot guard against -- v1 of this resolver
    -- crashed mid-dump because of exactly that.

    softProbeLog(val, "A-G")
    return nil
end

-- Walk a UObject or UStruct value: iterate its class' properties (incl.
-- superclass chain) and emit them. Returns (json.obj, hadAnyField) so callers
-- can distinguish "real instanced sub-object" from "ObjectProperty pointing at
-- an external asset we should serialise as a path".
walkStructProperties = function(v)
    if not isLiveUd(v) then return nil, false end

    local class
    local ok = pcall(function() class = v:GetClass() end)
    if not ok or not class or not class:IsValid() then return nil, false end

    local seen = {}
    local out  = json.obj()
    local cur  = class
    local guard = 0
    local hadAnyField = false

    while cur and cur:IsValid() and guard < 32 do
        guard = guard + 1
        local hadForEach = false
        pcall(function()
            if type(cur.ForEachProperty) == "function" then
                hadForEach = true
                cur:ForEachProperty(function(prop)
                    local pname, ptype
                    pcall(function() pname = prop:GetFName():ToString() end)
                    pcall(function() ptype = prop:GetClass():GetFName():ToString() end)
                    if pname and not seen[pname] then
                        seen[pname] = true
                        local val
                        local got = pcall(function() val = v[pname] end)
                        if got then
                            json.set(out, pname, walkPropertyValue(val, ptype, prop))
                            hadAnyField = true
                        else
                            json.set(out, pname, nil)
                        end
                    end
                end)
            end
        end)

        if not hadForEach then
            -- Fallback: linked Children list. We don't have prop-type info
            -- here, so we tag values "<unknown>" and walkPropertyValue falls
            -- back to heuristic decoding.
            pcall(function()
                local child = cur.Children
                local cguard = 0
                while child and child:IsValid() and cguard < 4096 do
                    cguard = cguard + 1
                    local fname, ftype
                    pcall(function() fname = child:GetFName():ToString() end)
                    pcall(function() ftype = child:GetClass():GetFName():ToString() end)
                    if fname and not seen[fname] then
                        seen[fname] = true
                        local val
                        local got = pcall(function() val = v[fname] end)
                        if got then
                            json.set(out, fname, walkPropertyValue(val, ftype, child))
                            hadAnyField = true
                        end
                    end
                    local nxt
                    pcall(function() nxt = child.Next end)
                    child = nxt
                end
            end)
        end

        local parent
        pcall(function() parent = cur:GetSuperStruct() end)
        if not parent or not parent:IsValid() then break end
        cur = parent
    end
    return out, hadAnyField
end

-- Walk a UScriptStruct *value* using its UScriptStruct *type* (obtained from
-- the owning property via prop:GetStruct()). UStructs are NOT UObjects -- they
-- have no :GetClass(); the type definition has to come in from outside, which
-- is why this function takes scriptStruct as a parameter (whereas
-- walkStructProperties derives the class from the value itself).
--
-- This is the missing piece that caused InventoryItemGppData / InventoryItemUIData
-- to dump as empty {}: the previous fix routed StructProperty into
-- walkStructProperties, but that calls val:GetClass() which fails on plain
-- struct values. Now StructProperty pulls the type via prop:GetStruct() and
-- we drive the iteration from there.
walkStructValue = function(val, scriptStruct)
    if val == nil then return json.obj() end
    if not scriptStruct or not scriptStruct:IsValid() then
        -- Defensive fallback: maybe the value behaves like a UObject after
        -- all (some UE4SS wrappers do). Try walkStructProperties; if it finds
        -- nothing we'll just return an empty object.
        local s = walkStructProperties(val)
        return s or json.obj()
    end

    local out  = json.obj()
    local seen = {}
    local cur  = scriptStruct
    local guard = 0

    while cur and cur:IsValid() and guard < 32 do
        guard = guard + 1
        local hadForEach = false
        pcall(function()
            if type(cur.ForEachProperty) == "function" then
                hadForEach = true
                cur:ForEachProperty(function(prop)
                    local pname, ptype
                    pcall(function() pname = prop:GetFName():ToString() end)
                    pcall(function() ptype = prop:GetClass():GetFName():ToString() end)
                    if pname and not seen[pname] then
                        seen[pname] = true
                        local fval
                        local got = pcall(function() fval = val[pname] end)
                        if got then
                            json.set(out, pname, walkPropertyValue(fval, ptype, prop))
                        end
                    end
                end)
            end
        end)

        if not hadForEach then
            pcall(function()
                local child = cur.Children
                local cguard = 0
                while child and child:IsValid() and cguard < 4096 do
                    cguard = cguard + 1
                    local fname, ftype
                    pcall(function() fname = child:GetFName():ToString() end)
                    pcall(function() ftype = child:GetClass():GetFName():ToString() end)
                    if fname and not seen[fname] then
                        seen[fname] = true
                        local fval
                        local got = pcall(function() fval = val[fname] end)
                        if got then
                            json.set(out, fname, walkPropertyValue(fval, ftype, child))
                        end
                    end
                    local nxt
                    pcall(function() nxt = child.Next end)
                    child = nxt
                end
            end)
        end

        local parent
        pcall(function() parent = cur:GetSuperStruct() end)
        if not parent or not parent:IsValid() then break end
        cur = parent
    end
    return out
end

-- ArrayProperty handling: UE4SS exposes :GetArrayNum() and 0-based index
-- access. The Inner property describes the element type -- crucial for arrays
-- of structs (e.g. LootTableEntries), where without the inner StructProperty
-- we'd hit the struct-walk heuristic that fails for plain struct values.
local function walkArray(v, prop)
    local n
    local ok = pcall(function() n = v:GetArrayNum() end)
    if not ok or type(n) ~= "number" then return json.arr() end

    -- Discover the inner property (and its type) so element decoding has the
    -- same information as a regular property walk. UE4SS exposes this either
    -- via :GetInner() (newer) or as a .Inner field (older).
    local innerProp, innerType
    if prop then
        pcall(function() innerProp = prop:GetInner() end)
        if not innerProp then
            pcall(function() innerProp = prop.Inner end)
        end
        if innerProp then
            pcall(function() innerType = innerProp:GetClass():GetFName():ToString() end)
        end
    end

    local arr = json.arr()
    for i = 0, n - 1 do
        local elem
        pcall(function() elem = v[i] end)
        json.push(arr, walkPropertyValue(elem, innerType, innerProp))
    end
    return arr
end

-- Decode a single property value given its UE property type name. ptype may
-- be nil when called recursively without a prop reference (array elements,
-- Children-fallback values that didn't expose a class) -- in that case we
-- fall back to the old heuristic flow.
walkPropertyValue = function(val, ptype, prop)
    -- Lua primitives are already final.
    if val == nil then
        -- Most "null"-shaped properties in the Stack-mod JSON serialise as
        -- the literal string "None", not JSON null.
        if ptype == "ObjectProperty" or ptype == "WeakObjectProperty"
            or ptype == "ClassProperty" or ptype == "SoftObjectProperty"
            or ptype == "SoftClassProperty" or ptype == "NameProperty"
            or ptype == "InterfaceProperty" then
            return "None"
        elseif ptype == "ArrayProperty" then
            return json.arr()
        elseif ptype == "StructProperty" then
            return json.obj()
        elseif ptype == "StrProperty" or ptype == "TextProperty" then
            return ""
        end
        return nil
    end
    local t = type(val)

    -- IMPORTANT: enum properties surface as raw numbers from v[pname] -- if we
    -- early-return on number we never reach the EnumProperty resolver below.
    -- Resolve enum-typed numerics here before the primitive shortcut.
    if t == "number" and (ptype == "EnumProperty" or ptype == "ByteProperty") and prop then
        local enum
        pcall(function() enum = prop:GetEnum() end)
        if enum and enum:IsValid() then
            local fullName
            pcall(function() fullName = enum:GetNameByValue(val):ToString() end)
            if type(fullName) == "string" and fullName ~= "" then
                local short = fullName:match("::(.+)$")
                return short or fullName
            end
        end
        -- ByteProperty without an attached enum is just a raw byte; fall through.
    end

    if t == "boolean" or t == "number" or t == "string" then
        return val
    end

    -- Userdata: dispatch on property type.
    if ptype == "StructProperty" then
        -- Pull the UScriptStruct definition from the property so we know
        -- which fields to walk. walkStructProperties (UObject path) won't
        -- work here because struct values have no :GetClass().
        local scriptStruct
        if prop then
            pcall(function() scriptStruct = prop:GetStruct() end)
            if not scriptStruct then
                -- Some UE4SS builds expose this via .Struct instead.
                pcall(function() scriptStruct = prop.Struct end)
            end
        end
        local result
        if scriptStruct and scriptStruct:IsValid() then
            result = walkStructValue(val, scriptStruct)
        else
            -- Fallback: try the UObject-style walker (might match if the value
            -- happens to be wrapped as a UObject in this UE4SS build).
            result = walkStructProperties(val) or json.obj()
        end
        -- Collapse unset TSoftObjectPtr<X> structs to the string "None"
        -- (matches Stack-mod reference shape -- see isAllNoneSoftStruct).
        if isAllNoneSoftStruct(result) then return "None" end
        return result

    elseif ptype == "ObjectProperty" or ptype == "WeakObjectProperty" then
        if not isLiveUd(val) then
            -- Defensive: some UE4SS builds tag soft refs as ObjectProperty.
            -- Try the soft-path resolver before giving up.
            local sp = resolveSoftPath(val)
            return sp or "None"
        end
        -- Inline subobject (e.g. InventoryItemGppData) vs. external asset
        -- reference (e.g. ItemMesh -> /Game/.../SM_Foo.SM_Foo). We distinguish
        -- by trying to walk: if we find any property fields, treat as inline;
        -- otherwise emit the path. /Script/* paths are always class refs and
        -- never get inlined as inline subobjects, so we short-circuit those.
        local full = objectFullName(val)
        if full and full:sub(1, 8) == "/Script/" then
            -- The "value" is itself a UClass -- emit the class path. (Plain
            -- ObjectProperty pointing at a UClass is unusual but happens.)
            return full
        end
        local s, hadFields = walkStructProperties(val)
        if hadFields then return s end
        if full and full ~= "None" then return full end
        -- Last-ditch: try soft-path even on a "live" wrapper -- some UE4SS
        -- builds expose soft-asset wrappers via the same property type.
        local sp = resolveSoftPath(val)
        return sp or full or "None"

    elseif ptype == "SoftObjectProperty" or ptype == "SoftClassProperty" then
        -- Soft refs hold an asset *path*; the UObject pointer is null at CDO
        -- time. resolveSoftPath() reads the path via FSoftObjectPath.
        return resolveSoftPath(val) or "None"

    elseif ptype == "ClassProperty" or ptype == "InterfaceProperty" then
        -- Hard class/interface refs: live UObject is reliable.
        return objectFullName(val) or "None"

    elseif ptype == "NameProperty" then
        local s
        local ok = pcall(function() s = val:ToString() end)
        if ok and type(s) == "string" then return s end
        return "None"

    elseif ptype == "StrProperty" then
        if t == "userdata" then
            local s
            pcall(function() s = val:ToString() end)
            return s or ""
        end
        return tostring(val)

    elseif ptype == "TextProperty" then
        -- FText has no exposed property tree in UE4SS; we render the
        -- localised string. The Stack-mod JSON keeps {TableId, Key, ...}
        -- shape from UAssetGUI's struct-table export, but we don't have
        -- access to those underlying fields at runtime without engine help.
        local s
        pcall(function() s = val:ToString() end)
        return s or ""

    elseif ptype == "BoolProperty" then
        return val == true or val == 1 or false

    elseif ptype == "ArrayProperty" then
        return walkArray(val, prop)

    elseif ptype == "ByteProperty" or ptype == "EnumProperty" then
        -- PropReflectProbe v3 Block 1 verified the canonical pattern:
        --     prop:GetEnum():GetNameByValue(rawNumeric):ToString()
        -- yields the fully-qualified enum name, e.g.
        --     "ER5BLInventoryItemClass::Default"
        -- Stack-mod reference shortens to the value-only suffix ("Default"),
        -- which the R5 JSON loader accepts identically.
        if prop and type(val) == "number" then
            local enum
            pcall(function() enum = prop:GetEnum() end)
            if enum and enum:IsValid() then
                local fullName
                pcall(function() fullName = enum:GetNameByValue(val):ToString() end)
                if type(fullName) == "string" and fullName ~= "" then
                    local short = fullName:match("::(.+)$")
                    return short or fullName
                end
            end
        end
        -- Fallback for FName-shaped enum userdata (rare on this build).
        if t == "userdata" then
            local s
            pcall(function() s = val:ToString() end)
            if type(s) == "string" then return s end
        end
        return tostring(val)

    elseif ptype == "FloatProperty" or ptype == "DoubleProperty"
        or ptype == "IntProperty" or ptype == "Int8Property"
        or ptype == "Int16Property" or ptype == "Int64Property"
        or ptype == "UInt16Property" or ptype == "UInt32Property"
        or ptype == "UInt64Property" then
        return val
    end

    -- Unknown / unspecified ptype: fall back to old heuristics. Order matters:
    -- struct walk first (so sub-objects don't get squashed to a class path).
    if t == "userdata" then
        local s, hadFields = walkStructProperties(val)
        if hadFields then return s end
        local p = objectFullName(val)
        if p then return p end
        local str
        pcall(function() str = val:ToString() end)
        if type(str) == "string" then return str end
        return tostring(val)
    end

    return tostring(val)
end

-- Backwards-compat alias used by the dump driver below.
local function walkStruct(v) return walkStructProperties(v) end

-- ---------------------------------------------------------------------------
-- Dump driver
-- ---------------------------------------------------------------------------

local function dumpItem(obj)
    local fullName
    pcall(function() fullName = obj:GetFullName() end)
    if not fullName then return false, "no full name" end

    local pkg = extractPackagePath(fullName)
    local treePath = packagePathToTreePath(pkg)
    if not treePath then return false, "no tree path: " .. tostring(pkg) end

    local fileName = treePathToFlatName(treePath)
    local outPath  = DUMP_DIR .. "\\" .. fileName

    local payload = json.obj()
    json.set(payload, "$type", TARGET_CLASS)

    -- Walk top-level struct: top-level $type is the class name; everything
    -- else is the property tree of the instance itself.
    local tree, ok = walkStruct(obj)
    if ok and tree then
        for _, key in ipairs(tree._order) do
            json.set(payload, key, tree._values[key])
        end
    end

    -- AssetBundleData: PrimaryDataAsset's bundle index. PropReflectProbe v5
    -- Block 3 verified that this property is NOT exposed via UE4SS's class
    -- reflection on this build (ForEachProperty walks every superclass and
    -- never encounters it). The Stack-mod reference always emits an empty
    -- bundle list ("Bundles": []) for inventory items, so we hard-code that
    -- shape. If we ever discover an inventory item with non-empty bundles
    -- this would need revisiting -- but since the reference itself stripped
    -- it to empty, the R5 loader clearly accepts that as the canonical CDO
    -- value.
    local assetBundleData = json.obj()
    json.set(assetBundleData, "Bundles", json.arr())
    json.set(payload, "AssetBundleData", assetBundleData)

    -- Mirror the NativeClass footer the Stack-mod JSONs end with. We compute
    -- it from the live class so it stays correct even if the engine moves
    -- the type.
    local nativeClass
    pcall(function()
        local c = obj:GetClass()
        if c and c:IsValid() then
            local cn = c:GetFullName()           -- e.g. "Class /Script/R5BusinessRules.R5BLInventoryItem"
            local space = cn:find(" ")
            local kind = space and cn:sub(1, space - 1) or "Class"
            local path = space and cn:sub(space + 1) or cn
            nativeClass = "/Script/CoreUObject." .. kind .. "'" .. path .. "'"
        end
    end)
    if nativeClass then
        json.set(payload, "NativeClass", nativeClass)
    end

    local f = io.open(outPath, "w")
    if not f then return false, "io.open failed: " .. outPath end
    f:write(json.encode(payload))
    f:close()
    return true, outPath
end

local function runDump()
    if not dumpsWritable() then
        log("ERROR: Dumps directory not writable: " .. DUMP_DIR)
        log("(Dump-WindroseVanilla.ps1 should pre-create this dir.)")
        return
    end

    local objs
    local ok = pcall(function() objs = FindAllOf(TARGET_CLASS) end)
    if not ok or not objs then
        log("FindAllOf(" .. TARGET_CLASS .. ") returned nothing")
        return
    end

    local total, dumped, failed, skipped = 0, 0, 0, 0
    if NAME_FILTER ~= "" then
        log("NAME_FILTER active: '" .. NAME_FILTER .. "'")
    end
    if MAX_DUMPS > 0 then
        logf("MAX_DUMPS=%d (test mode)", MAX_DUMPS)
    end
    for _, obj in ipairs(objs) do
        if obj and obj:IsValid() then
            total = total + 1

            -- Filter by full name BEFORE we start walking properties; saves
            -- log noise and time on large object sets.
            local pass = true
            if NAME_FILTER ~= "" then
                local fn
                pcall(function() fn = obj:GetFullName() end)
                if not (fn and string.find(fn, NAME_FILTER, 1, true)) then
                    pass = false
                    skipped = skipped + 1
                end
            end

            if pass then
                local ok, wrote, why = pcall(dumpItem, obj)
                if ok and wrote then
                    dumped = dumped + 1
                else
                    failed = failed + 1
                    local reason = (not ok) and tostring(wrote) or tostring(why)
                    log("skip: " .. reason)
                end

                if MAX_DUMPS > 0 and dumped >= MAX_DUMPS then
                    logf("reached MAX_DUMPS=%d, stopping early", MAX_DUMPS)
                    break
                end
            end
        end
    end
    if skipped > 0 then
        logf("filtered out %d items by NAME_FILTER", skipped)
    end

    -- Manifest: list every file we wrote, plus run metadata. Useful for the
    -- PowerShell wrapper to verify completion without scanning the directory.
    local manifest = json.obj()
    json.set(manifest, "tool",         "VanillaItemDumper")
    json.set(manifest, "target_class", TARGET_CLASS)
    json.set(manifest, "total_found",  total)
    json.set(manifest, "dumped",       dumped)
    json.set(manifest, "failed",       failed)
    json.set(manifest, "timestamp",    os.date("!%Y-%m-%dT%H:%M:%SZ"))
    json.set(manifest, "path_separator", PATH_SEPARATOR)
    local mf = io.open(DUMP_DIR .. "\\_manifest.json", "w")
    if mf then
        mf:write(json.encode(manifest))
        mf:close()
    end

    logf("done: %d dumped, %d failed (of %d found) -> %s",
        dumped, failed, total, DUMP_DIR)
end

-- ---------------------------------------------------------------------------
-- Boot: poll for the target class, then dump once and stop.
-- ---------------------------------------------------------------------------

log("loaded. mod_dir=" .. MOD_DIR)
log("dump_dir=" .. DUMP_DIR)

local _retries = 0
local _done = false

LoopAsync(POLL_INTERVAL, function()
    if _done then return true end
    _retries = _retries + 1

    local objs
    local ok = pcall(function() objs = FindAllOf(TARGET_CLASS) end)
    if ok and objs then
        local count = 0
        pcall(function()
            for _ in ipairs(objs) do count = count + 1 end
        end)
        if count > 0 then
            logf("found %d %s instances after %d poll(s) — dumping",
                count, TARGET_CLASS, _retries)
            local rok, rerr = pcall(runDump)
            if not rok then
                log("dump crashed: " .. tostring(rerr))
            end
            _done = true
            return true
        end
    end

    if _retries >= MAX_RETRIES then
        logf("no %s instances after %d retries — giving up",
            TARGET_CLASS, MAX_RETRIES)
        return true
    end
    return false
end)
