-- QuitProbe / main.lua
--
-- Goal: answer ONE question -- do console commands actually reach the engine
-- on this dedicated server, or are they silently swallowed somewhere between
-- UE4SS-Lua and UE's Exec dispatcher?
--
-- Method: send "quit" via two different paths and log between them. If the
-- server dies after one of the calls, that path works. If both calls return
-- and the loop reaches the final "all-ignored" log line, command routing is
-- dead and any further effort on Plan C is wasted.
--
-- Why "quit" instead of more diagnostic commands like "stat fps":
--   * "stat fps" only writes to the HUD, never to a log we can read.
--   * "log" toggles verbosity, no observable effect on a clean run.
--   * "quit" has a side-effect we can observe externally without reading any
--     logs: the server process exits. That's binary, unambiguous, and free.
--
-- Layout:
--   Phase 0: wait until UEngine + UWorld are ready (boot can take a few sec).
--   Phase 1: KSL:ExecuteConsoleCommand(world, "quit") -- 3s grace.
--   Phase 2: engine:Exec(world, "quit") -- 3s grace.
--   Phase 3: log "ALL IGNORED" and stop.

local UEHelpers = require("UEHelpers")

local LOG_TAG       = "[QuitProbe]"
local STEP_DELAY_MS = 3000   -- give the engine time to actually die

local function log(msg)
    print(LOG_TAG .. " " .. msg .. "\n")
end

local function nowMs()
    return os.clock() * 1000
end

log("loaded")

local _phase    = 0
local _next_at  = nil

LoopAsync(500, function()
    if _next_at and nowMs() < _next_at then
        return false
    end

    -- Phase 0: wait for engine
    if _phase == 0 then
        local engine = UEHelpers.GetEngine()
        local world  = UEHelpers.GetWorld()
        if not (engine and world) then
            return false  -- still booting
        end
        log("engine + world ready")
        _phase = 1
        return false
    end

    -- Phase 1: KSL:ExecuteConsoleCommand
    if _phase == 1 then
        log("=== Phase 1: KSL:ExecuteConsoleCommand(world, 'quit') ===")
        local KSL   = UEHelpers.GetKismetSystemLibrary()
        local world = UEHelpers.GetWorld()
        log("KSL=" .. tostring(KSL ~= nil) .. " world=" .. tostring(world ~= nil))
        local ok, err = pcall(function()
            KSL:ExecuteConsoleCommand(world, "quit", nil)
        end)
        log("    sent (ok=" .. tostring(ok) .. " err=" .. tostring(err) .. ")")
        _phase   = 2
        _next_at = nowMs() + STEP_DELAY_MS
        return false
    end

    -- Phase 2: engine:Exec
    if _phase == 2 then
        log(">>> KSL quit: server STILL ALIVE after " .. STEP_DELAY_MS .. "ms")
        log("=== Phase 2: engine:Exec(world, 'quit') ===")
        local engine = UEHelpers.GetEngine()
        local world  = UEHelpers.GetWorld()
        log("engine=" .. tostring(engine ~= nil) .. " world=" .. tostring(world ~= nil))
        local ok, err = pcall(function()
            local r = engine:Exec(world, "quit")
            log("    engine:Exec returned: " .. tostring(r))
        end)
        log("    sent (ok=" .. tostring(ok) .. " err=" .. tostring(err) .. ")")
        _phase   = 3
        _next_at = nowMs() + STEP_DELAY_MS
        return false
    end

    -- Phase 3: conclusion
    if _phase == 3 then
        log(">>> engine:Exec quit: server STILL ALIVE after " .. STEP_DELAY_MS .. "ms")
        log(">>> CONCLUSION: both quit commands were silently ignored")
        log(">>> -> console-command routing is DEAD on this dedicated server")
        log(">>> -> Plan C is not viable; output-hook would not help")
        _phase = 99
        return true
    end

    return false
end)
