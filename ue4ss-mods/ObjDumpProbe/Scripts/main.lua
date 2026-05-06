-- ObjDumpProbe / main.lua
--
-- Goal (Option C): trigger UE's built-in "obj dump <fullpath>" console command
-- against an inventory item DataAsset, so the engine writes the full property
-- list (including resolved soft-object paths) to the dedicated-server log.
--
-- Why this might work where Lua reflection failed (Options A+B):
--   * "obj dump" is implemented entirely on the C++ side. It calls ExportText
--     on every UProperty, which for FSoftObjectPath returns the canonical
--     "/Game/.../SM_Foo.SM_Foo" string - exactly what we want.
--   * We don't pass any TSoftObjectPtr through Lua -> C++ marshaling, so the
--     Access Violation that killed the SoftPathProbe should not reappear.
--
-- Risks/unknowns this probe has to verify:
--   1. Does ExecuteConsoleCommand do anything on a dedicated server with
--      zero connected players? UKismetSystemLibrary forwards to the first
--      APlayerController; if there is none, it might silently no-op.
--   2. If KSL no-ops, can we reach UEngine::Exec directly from Lua? The
--      function's third parameter is FOutputDevice& which UE4SS Lua cannot
--      marshal, but the 2-arg form (without explicit Ar) might be exposed.
--   3. Where does the output go on the dedicated server? "obj dump" writes
--      to whatever FOutputDevice it received. If the implicit one is GLog,
--      output ends up in R5.log; if it's a throwaway FStringOutputDevice,
--      we lose it and need a different approach.
--
-- Plan:
--   1. Find FiberPlant_T01 instance and read its FullName ("Class /Path.Name").
--   2. Strip the leading class to get the path-only form "/Path.Name".
--   3. Issue "stat fps" first as a canary - if even that doesn't show in any
--      log, we know ConsoleCommand routing is broken on this build.
--   4. Issue "obj dump <fullpath>" + variants, multiple dispatch paths.
--   5. Stop. User checks R5.log + UE4SS.log for output.

local UEHelpers = require("UEHelpers")

local LOG_TAG       = "[ObjDumpProbe]"
local TARGET_CLASS  = "R5BLInventoryItem"
local NAME_FILTER   = "FiberPlant_T01"
local POLL_INTERVAL = 2000
local MAX_RETRIES   = 30

-- ---------------------------------------------------------------------------
-- Logging helpers
-- ---------------------------------------------------------------------------

-- UE4SS' embedded Lua print() does NOT append a newline; we add one so each
-- log line gets its own timestamp in UE4SS.log.
local function log(msg)
    print(LOG_TAG .. " " .. msg .. "\n")
end

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

local function probe(item, fullName)
    log("target full name = " .. tostring(fullName))

    -- "obj dump" accepts either a name or a full object path. The FullName
    -- format from UE4SS is "ClassName /Package.ObjectName"; we want the
    -- "/Package.ObjectName" part for the most specific match.
    local pathOnly = string.match(fullName, "%S+%s+(.+)") or fullName
    local nameOnly = string.match(pathOnly, "([^.]+)$") or pathOnly
    log("path extracted = " .. tostring(pathOnly))
    log("name extracted = " .. tostring(nameOnly))

    local KSL    = UEHelpers.GetKismetSystemLibrary()
    local world  = UEHelpers.GetWorld()
    local engine = UEHelpers.GetEngine()
    log("world  = " .. describe(world))
    log("KSL    = " .. describe(KSL))
    log("engine = " .. describe(engine))

    -- ----------------------------------------------------------------
    -- Strategy 1: canary "stat fps" via ExecuteConsoleCommand.
    -- If this produces anything visible (UE4SS.log or R5.log), we know
    -- the console-command path is plumbed at all on this dedicated server.
    -- If not, all the obj-dump variants below will also no-op.
    -- ----------------------------------------------------------------

    strategy("KSL:ExecuteConsoleCommand(world, 'stat fps')", function()
        KSL:ExecuteConsoleCommand(world, "stat fps", nil)
        log("    sent (no return value to read)")
    end)

    -- Marker so we can find each strategy's output region in the log.
    log("=== SENT obj-dump variants now; look for 'Obj=' / '0x' lines below ===")

    -- ----------------------------------------------------------------
    -- Strategy 2: obj dump via ExecuteConsoleCommand, full path form.
    -- This is the canonical UE syntax. If KSL routes to GLog (or a player
    -- controller's console), output should appear inline.
    -- ----------------------------------------------------------------

    strategy("KSL:ExecuteConsoleCommand(world, 'obj dump /Path...')", function()
        KSL:ExecuteConsoleCommand(world, "obj dump " .. pathOnly, nil)
        log("    sent")
    end)

    -- ----------------------------------------------------------------
    -- Strategy 3: obj dump via ExecuteConsoleCommand, name-only form.
    -- Some "obj" subcommands accept short names; covers the case where the
    -- full path doesn't resolve (e.g. mismatch between FullName syntax and
    -- what the obj-system expects).
    -- ----------------------------------------------------------------

    strategy("KSL:ExecuteConsoleCommand(world, 'obj dump <Name>')", function()
        KSL:ExecuteConsoleCommand(world, "obj dump " .. nameOnly, nil)
        log("    sent")
    end)

    -- ----------------------------------------------------------------
    -- Strategy 4: UEngine:Exec(world, cmd) - direct fallback.
    -- UEngine::Exec is the actual handler that "obj" routes through.
    -- The signature with FOutputDevice might be exposed as a 2-arg form
    -- via UE4SS' UFunction binding, where the FOutputDevice is implicit.
    -- ----------------------------------------------------------------

    strategy("engine:Exec(world, 'obj dump <Name>')", function()
        if not (engine and engine:IsValid()) then
            log("    no engine")
            return
        end
        local r
        pcall(function() r = engine:Exec(world, "obj dump " .. nameOnly) end)
        log("    engine:Exec returned " .. describe(r))
    end)

    -- ----------------------------------------------------------------
    -- Strategy 5: list "obj" subcommands by issuing a deliberately empty
    -- "obj" call. UE prints a help line listing all subcommands for that
    -- handler, which (a) confirms commands are processed and (b) tells us
    -- the exact "obj dump" syntax this engine version expects.
    -- ----------------------------------------------------------------

    strategy("KSL:ExecuteConsoleCommand(world, 'obj')", function()
        KSL:ExecuteConsoleCommand(world, "obj", nil)
        log("    sent")
    end)

    log("=== probe done ===")
    log("Check R5.log AND UE4SS.log for command output.")
    log("Search for: 'obj dump', 'Obj=', '<class>=' (the ExportText format),")
    log("            'Unknown command', or 'stat fps' confirmation.")
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
