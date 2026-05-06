-- SoftPathProbe / main.lua
--
-- Goal: figure out HOW to resolve a TSoftObjectPtr to its asset path string
-- ("/Game/.../SM_Foo.SM_Foo") on this UE4SS build, without calling :Get()
-- (which crashes inside the asset loader on a dedicated server).
--
-- Previous attempts (see VanillaItemDumper history):
--   * v:ToString()                 -> "None"  (native ToString early-outs)
--   * v.AssetPath.PackageName etc. -> userdata that aliases v itself
--   * v:GetAssetPathString()       -> "attempt to call ... value"
--   * pairs(v)                     -> iterator error
-- => UE4SS on this build exposes the TSoftObjectPtr userdata as opaque.
--
-- New approach (Option B): bypass the broken Lua binding entirely and ask
-- the engine. UKismetSystemLibrary has native conversion functions that
-- take a SoftObjectReference and return an FString. UE4SS can invoke them
-- via direct call (which translates to ProcessEvent under the hood), which
-- side-steps the userdata-member problem.
--
-- This script:
--   1. Finds R5BLInventoryItem instance whose name matches NAME_FILTER.
--   2. Reads its ItemMesh property (the soft object userdata).
--   3. Tries every plausible conversion path, logging each result.
--   4. Stops after one item -- this is a probe, not a dumper.
--
-- The winning strategy gets folded back into VanillaItemDumper later.

local UEHelpers = require("UEHelpers")

local LOG_TAG       = "[SoftPathProbe]"
local TARGET_CLASS  = "R5BLInventoryItem"
local NAME_FILTER   = "FiberPlant_T01"
local POLL_INTERVAL = 2000
local MAX_RETRIES   = 30

-- ---------------------------------------------------------------------------
-- Logging
-- ---------------------------------------------------------------------------

-- UE4SS' embedded Lua print() does NOT append a newline; we add one manually
-- to keep timestamps separate.
local function log(msg)
    print(LOG_TAG .. " " .. msg .. "\n")
end

-- Render a Lua/UE4SS value compactly for log output.
--   nil/string/number/boolean -> obvious
--   userdata with :ToString() -> "ud:'<...>'"
--   userdata without          -> "ud:<addr>"
local function describe(v)
    if v == nil then return "nil" end
    local t = type(v)
    if t == "string"  then return "str:'" .. v .. "'" end
    if t == "number"  then return "num:" .. tostring(v) end
    if t == "boolean" then return "bool:" .. tostring(v) end
    if t == "userdata" then
        local addr = tostring(v)
        local s
        local ok = pcall(function() s = v:ToString() end)
        if ok and type(s) == "string" then
            return "ud:'" .. s .. "' (" .. addr .. ")"
        end
        return "ud:<no-ToString> (" .. addr .. ")"
    end
    return t .. ":" .. tostring(v)
end

-- Wrap a strategy in pcall, log header + result-or-error.
local function strategy(name, fn)
    log("--- " .. name .. " ---")
    local ok, err = pcall(fn)
    if not ok then
        log("    ERR: " .. tostring(err))
    end
end

-- ---------------------------------------------------------------------------
-- Find target item
-- ---------------------------------------------------------------------------

local function findItem()
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

-- ---------------------------------------------------------------------------
-- Probe strategies
-- ---------------------------------------------------------------------------

local function probe(item, itemName)
    log("target: " .. tostring(itemName))

    -- Read the soft-mesh property. ItemMesh is the canonical example
    -- (resource items always have one), and the value type for these is
    -- TSoftObjectPtr<UStaticMesh>.
    local mesh
    pcall(function() mesh = item.ItemMesh end)
    log("ItemMesh raw = " .. describe(mesh))
    if mesh == nil then
        log("ItemMesh is nil -- nothing to resolve, aborting probe")
        return
    end

    -- Get UKismetSystemLibrary CDO via UEHelpers (cached default object).
    -- This is the engine class with all the static Conv_* helpers.
    local KSL = UEHelpers.GetKismetSystemLibrary()
    log("KismetSystemLibrary CDO = " .. describe(KSL))

    -- Also pull KismetStringLibrary and KismetTextLibrary in case helpful
    -- conversions live there.
    local KStr = UEHelpers.GetKismetStringLibrary()
    log("KismetStringLibrary CDO = " .. describe(KStr))

    -- ----------------------------------------------------------------
    -- Group 1: direct UE4SS method dispatch on KSL CDO.
    -- UE4SS will internally invoke ProcessEvent for these.
    -- ----------------------------------------------------------------

    strategy("KSL:Conv_SoftObjectReferenceToString(mesh)", function()
        local r = KSL:Conv_SoftObjectReferenceToString(mesh)
        log("    => " .. describe(r))
    end)

    strategy("KSL:Conv_SoftClassReferenceToString(mesh)", function()
        local r = KSL:Conv_SoftClassReferenceToString(mesh)
        log("    => " .. describe(r))
    end)

    -- IsValidSoftObjectReference returns bool, but is a sanity check that
    -- the soft userdata travels through ProcessEvent at all.
    strategy("KSL:IsValidSoftObjectReference(mesh)", function()
        local r = KSL:IsValidSoftObjectReference(mesh)
        log("    => " .. describe(r))
    end)

    -- ----------------------------------------------------------------
    -- Group 2: convert via FSoftObjectPath, not TSoftObjectPtr.
    -- Some UE4SS builds expose .ToSoftObjectPath() which returns the inner
    -- struct; conversion functions on KSL might accept that instead.
    -- ----------------------------------------------------------------

    local sop
    strategy("mesh:ToSoftObjectPath()", function()
        sop = mesh:ToSoftObjectPath()
        log("    => " .. describe(sop))
    end)
    if sop ~= nil then
        strategy("KSL:Conv_SoftObjectReferenceToString(<sop>)", function()
            local r = KSL:Conv_SoftObjectReferenceToString(sop)
            log("    => " .. describe(r))
        end)
    end

    -- ----------------------------------------------------------------
    -- Group 3: alternative engine helpers.
    -- KismetStringLibrary has Conv_NameToString and friends; if anything
    -- lets us coerce a property to a string, this is a good place to try.
    -- ----------------------------------------------------------------

    strategy("KStr:Conv_NameToString(mesh)", function()
        local r = KStr:Conv_NameToString(mesh)
        log("    => " .. describe(r))
    end)

    -- ----------------------------------------------------------------
    -- Group 4: ProcessEvent explicitly. Bypass any UE4SS magic by
    -- looking up the function on the class and invoking it directly.
    -- ----------------------------------------------------------------

    strategy("ProcessEvent: Conv_SoftObjectReferenceToString", function()
        local cls
        pcall(function() cls = KSL:GetClass() end)
        if cls == nil then
            log("    no class")
            return
        end
        log("    KSL class = " .. describe(cls))

        -- UE4SS exposes UObject:GetFunctionByName() on UClass
        -- (some builds: FindFunction). Try both.
        local fn
        pcall(function() fn = cls:FindFunctionByName(FName("Conv_SoftObjectReferenceToString")) end)
        if fn == nil then
            pcall(function() fn = cls:FindFunctionByName("Conv_SoftObjectReferenceToString") end)
        end
        if fn == nil then
            pcall(function() fn = cls:GetFunctionByName(FName("Conv_SoftObjectReferenceToString")) end)
        end
        log("    function = " .. describe(fn))

        if fn == nil then return end

        -- ProcessEvent expects a params table mirroring the function's
        -- in/out parameters. The signature is roughly:
        --     FString Conv_SoftObjectReferenceToString(TSoftObjectPtr<UObject> SoftObjectReference);
        -- so we expect SoftObjectReference and ReturnValue (FString).
        local params = { SoftObjectReference = mesh, ReturnValue = "" }
        local pe_ok, pe_err = pcall(function() KSL:ProcessEvent(fn, params) end)
        if not pe_ok then
            log("    ProcessEvent ERR: " .. tostring(pe_err))
            return
        end
        log("    after PE: ReturnValue = " .. describe(params.ReturnValue))
        -- Some UE4SS versions don't write back to the same table; dump
        -- everything so we see what came back.
        for k, v in pairs(params) do
            log("    params[" .. tostring(k) .. "] = " .. describe(v))
        end
    end)

    -- ----------------------------------------------------------------
    -- Group 5: enumerate UClass functions to see what's actually bound
    -- on this engine build. Useful if the canonical name doesn't exist.
    -- ----------------------------------------------------------------

    strategy("Enumerate KSL class functions (Conv_*)", function()
        local cls
        pcall(function() cls = KSL:GetClass() end)
        if cls == nil then return end

        -- ForEachFunction is the UE4SS API for walking a class's UFunctions.
        local count = 0
        local matched = 0
        local hadIter = false
        pcall(function()
            if type(cls.ForEachFunction) == "function" then
                hadIter = true
                cls:ForEachFunction(function(f)
                    count = count + 1
                    local fname
                    pcall(function() fname = f:GetFName():ToString() end)
                    if type(fname) == "string" and string.find(fname, "Conv_", 1, true) then
                        matched = matched + 1
                        log("    fn " .. fname)
                    end
                end)
            end
        end)
        if not hadIter then
            log("    cls has no ForEachFunction")
        else
            log("    enumerated " .. tostring(count) .. " functions, "
                .. tostring(matched) .. " match Conv_*")
        end
    end)

    log("=== probe done ===")
end

-- ---------------------------------------------------------------------------
-- Boot loop
-- ---------------------------------------------------------------------------

log("loaded.")
local _retries = 0
local _done = false

LoopAsync(POLL_INTERVAL, function()
    if _done then return true end
    _retries = _retries + 1

    local item, name = findItem()
    if item then
        log("found target after " .. tostring(_retries) .. " poll(s)")
        local ok, err = pcall(probe, item, name)
        if not ok then
            log("probe crashed: " .. tostring(err))
        end
        _done = true
        return true
    end

    if _retries >= MAX_RETRIES then
        log("target not found after " .. tostring(MAX_RETRIES) .. " retries")
        return true
    end
    return false
end)
