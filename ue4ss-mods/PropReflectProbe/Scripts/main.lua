-- PropReflectProbe / main.lua  (v5 - SAFER: AssetBundleData + empty-struct only)
--
-- Findings carried forward:
--   v2: Direct field access on primitive struct fields works (MaxCountInSlot=100)
--   v3: BLOCK 1 (Enums) won. prop:GetEnum():GetNameByValue(v):ToString() works.
--       Strip "EnumName::" prefix -> bare name like "Default", "Common".
--   v3: BLOCK 2 (FText) -- text:ToString() works (resolved string).
--       text:GetTableId() crashed UE4SS hard.
--   v4: BLOCK 2 SAFE (FText field access only) -- text.TableId crashed too.
--       FText is fully off-limits except for :ToString().
--
-- Open questions remaining (v5):
--   BLOCK 3: AssetBundleData on UPrimaryDataAsset reachable?
--     - Class-chain walk to confirm property exists
--     - Direct field access item.AssetBundleData
--     - bd.Bundles:GetArrayNum() (Array methods are safe per WindrosePlus usage)
--   BLOCK 4: ConsumableData / LootTableData -- can we read PackageName/AssetName
--     FNames so we can collapse "all-None" into the string "None"?
--     - Both are StructProperty in InventoryItemGppData (offset 0x110, 0x130).
--     - Likely contain an FSoftObjectPath sub-struct with FName fields.
--
-- Crash-safety rules locked in:
--   - NO method calls on TSoftObjectPtr userdata (verified wall in v1)
--   - NO method calls on FText userdata except :ToString() (verified in v3)
--   - NO field access on FText userdata (verified in v4)
--   - ENUM: GetNameStringByValue crashes; only GetNameByValue + :ToString() safe
--   - Field access on UStruct/UObject is safe (FSoftObjectPath is a UStruct)
--   - Marker logs BEFORE every risky access so a crash points to the trigger.

local UEHelpers = require("UEHelpers")

local TARGET_CLASS  = "R5BLInventoryItem"
local NAME_FILTER   = "FiberPlant"
local POLL_INTERVAL = 1500
local MAX_RETRIES   = 30
local LOG_TAG       = "[PropReflectProbe]"

local function log(msg)  print(LOG_TAG .. " " .. msg .. "\n") end
local function logf(fmt, ...) print(LOG_TAG .. " " .. string.format(fmt, ...) .. "\n") end

-- describe() WITHOUT calling :ToString() on userdata -- safe for any opaque type.
local function describeRaw(v)
    if v == nil then return "nil" end
    local t = type(v)
    if t == "string"  then return "str:'" .. v .. "'" end
    if t == "number"  then return "num:" .. tostring(v) end
    if t == "boolean" then return "bool:" .. tostring(v) end
    if t == "userdata" then return "ud:" .. tostring(v) end
    return t .. ":" .. tostring(v)
end

local function tryRead(label, fn)
    log("  >>> attempting: " .. label)   -- marker BEFORE the risky call
    local raw
    local ok, err = pcall(function() raw = fn() end)
    if not ok then
        log("  " .. label .. " => ERR: " .. tostring(err))
        return nil
    end
    log("  " .. label .. " => " .. describeRaw(raw))
    return raw
end

-- ---------------------------------------------------------------------------
-- BLOCK 3 (run FIRST -- lower risk profile)
-- ---------------------------------------------------------------------------

local function probeAssetBundleData(item)
    log("=== BLOCK 3: AssetBundleData class-chain walk ===")

    local cls
    pcall(function() cls = item:GetClass() end)
    if not cls then log("  no class -- skipping"); return end

    local cur = cls
    local guard = 0
    while cur and cur:IsValid() and guard < 16 do
        guard = guard + 1
        local cn
        pcall(function() cn = cur:GetFullName() end)
        log("--- chain[" .. guard .. "] = " .. tostring(cn) .. " ---")
        local found = false
        pcall(function()
            cur:ForEachProperty(function(prop)
                local pname, ptype
                pcall(function() pname = prop:GetFName():ToString() end)
                pcall(function() ptype = prop:GetClass():GetFName():ToString() end)
                if pname == "AssetBundleData" then
                    log("  FOUND prop, type = " .. tostring(ptype))
                    found = true
                end
            end)
        end)
        if not found then log("  (not on this level)") end
        local parent
        pcall(function() parent = cur:GetSuperStruct() end)
        if not parent or not parent:IsValid() then break end
        cur = parent
    end

    -- Direct field read.
    log("--- direct field access ---")
    local bd = tryRead("item.AssetBundleData",
        function() return item.AssetBundleData end)

    if bd ~= nil and type(bd) == "userdata" then
        local bundles = tryRead("bd.Bundles", function() return bd.Bundles end)
        if bundles ~= nil and type(bundles) == "userdata" then
            -- TArray methods are battle-tested in WindrosePlus, considered safe.
            log("  >>> attempting: bundles:GetArrayNum()")
            local n
            local okN, errN = pcall(function() n = bundles:GetArrayNum() end)
            if okN and type(n) == "number" then
                log("  bundles:GetArrayNum() => " .. tostring(n))
            else
                log("  bundles:GetArrayNum() ERR: " .. tostring(errN))
            end
        end
    end
end

-- ---------------------------------------------------------------------------
-- BLOCK 4 (run LAST -- higher risk: nested FName field access on FSoftObjectPath)
-- ---------------------------------------------------------------------------

local function probeStructEmpty(item)
    log("=== BLOCK 4: 'all-None' struct collapse (ConsumableData/LootTableData) ===")

    for _, name in ipairs({ "ConsumableData", "LootTableData" }) do
        log("--- " .. name .. " ---")

        -- Step 1: confirm field-access on the struct itself is safe.
        local val = tryRead("item.InventoryItemGppData." .. name,
            function() return item.InventoryItemGppData[name] end)

        if val == nil or type(val) ~= "userdata" then
            log("  not a userdata -- skipping inner access")
        else
            -- Step 2: try AssetPath (most likely an FSoftObjectPath UStruct).
            local ap = tryRead("val.AssetPath", function() return val.AssetPath end)
            if ap ~= nil and type(ap) == "userdata" then
                -- Step 3: PackageName / AssetName are FName (safe via :ToString).
                log("  >>> attempting: ap.PackageName")
                local pkg
                pcall(function() pkg = ap.PackageName end)
                log("  ap.PackageName = " .. describeRaw(pkg))
                if pkg ~= nil and type(pkg) == "userdata" then
                    log("  >>> attempting: pkg:ToString()")
                    local s
                    local ok = pcall(function() s = pkg:ToString() end)
                    if ok then log("  pkg:ToString() => '" .. tostring(s) .. "'")
                    else        log("  pkg:ToString() ERR") end
                end

                log("  >>> attempting: ap.AssetName")
                local asset
                pcall(function() asset = ap.AssetName end)
                log("  ap.AssetName = " .. describeRaw(asset))
                if asset ~= nil and type(asset) == "userdata" then
                    log("  >>> attempting: asset:ToString()")
                    local s
                    local ok = pcall(function() s = asset:ToString() end)
                    if ok then log("  asset:ToString() => '" .. tostring(s) .. "'")
                    else        log("  asset:ToString() ERR") end
                end
            end

            -- Step 4: SubPathString is an FString (probably -> Lua string directly).
            tryRead("val.SubPathString",
                function() return val.SubPathString end)
        end
    end
end

-- ---------------------------------------------------------------------------
-- Driver
-- ---------------------------------------------------------------------------

local function findTarget()
    local objs
    local ok = pcall(function() objs = FindAllOf(TARGET_CLASS) end)
    if not ok or not objs then return nil end
    for _, obj in ipairs(objs) do
        if obj and obj:IsValid() then
            local fn
            pcall(function() fn = obj:GetFullName() end)
            if fn and string.find(fn, NAME_FILTER, 1, true) then
                return obj, fn
            end
        end
    end
    return nil
end

log("loaded (v5 SAFER). waiting for " .. TARGET_CLASS .. " (filter='" .. NAME_FILTER .. "')")
log("v5 only runs BLOCK 3 (AssetBundleData) and BLOCK 4 (empty struct collapse).")
log("FText is off-limits per v4 crash -- skipped entirely.")

local _retries = 0
local _done = false

LoopAsync(POLL_INTERVAL, function()
    if _done then return true end
    _retries = _retries + 1

    local target, fname = findTarget()
    if target then
        logf("found target after %d poll(s): %s", _retries, tostring(fname))

        log(">>> Block 3 starting")
        local ok3, err3 = pcall(function() probeAssetBundleData(target) end)
        if not ok3 then log("Block 3 crashed in Lua layer: " .. tostring(err3)) end
        log(">>> Block 3 done")

        log(">>> Block 4 starting")
        local ok4, err4 = pcall(function() probeStructEmpty(target) end)
        if not ok4 then log("Block 4 crashed in Lua layer: " .. tostring(err4)) end
        log(">>> Block 4 done")

        log("=== probe done ===")
        _done = true
        return true
    end

    if _retries >= MAX_RETRIES then
        logf("no %s after %d retries -- giving up", TARGET_CLASS, MAX_RETRIES)
        return true
    end
    return false
end)
