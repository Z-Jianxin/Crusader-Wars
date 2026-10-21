-- Task-only Attila calibration. No gameplay pack is modified and no WH APIs are assumed.
-- Historical library evidence: battle_manager setup_battle, script_unit, unit controllers,
-- number_of_men_alive and number_of_enemies_killed are present in Attila build 25370302.
local logfile = "@@LOGFILE@@"
local suite = "@@SUITE@@"
local run = tostring(os.time())
local function log(kind, fields)
    local file, err = io.open(logfile, "a")
    if not file then error("Cannot write calibration log: " .. tostring(err)) end
    file:write(run .. "," .. suite .. "," .. kind .. "," .. tostring(fields or "") .. "\n")
    file:close()
end
log("SCRIPT_LOADED", "0")
system.ClearRequiredFiles()
require "lua_scripts.Battle_Script_Header"
bm = battle_manager:new(empire_battle:new())
local specifications = {
@@PAIRS@@
}
local pairs = {}
local elapsed = 0
local started = false
local completed = false
local sentinels = {}

local function guard(where, fn)
    local ok, err = pcall(fn)
    if not ok then log("ERROR", where .. "," .. string.gsub(tostring(err), "[\r\n,]", " ")) end
    return ok
end

local function sample_pair(pair, kind)
    local left = pair.left.unit
    local right = pair.right.unit
    log(kind, elapsed .. "," .. pair.label .. "," .. left:number_of_men_alive() .. "," ..
        right:number_of_men_alive() .. "," .. left:number_of_enemies_killed() .. "," ..
        right:number_of_enemies_killed() .. "," .. tostring(left:is_routing()) .. "," .. tostring(right:is_routing()))
end

local function finish(reason)
    if completed then return end
    completed = true
    for i = 1, #pairs do sample_pair(pairs[i], "FINAL") end
    log("COMPLETE", elapsed .. "," .. reason)
    -- Root can close Attila when COMPLETE is observed; this also releases the normal results screen.
    guard("finish_victory", function()
        for i = 1, #pairs do pairs[i].right.uc:kill() end
        sentinels[2].uc:kill()
        bm:end_battle()
    end)
end

local function tick()
    if completed or not started then return end
    elapsed = elapsed + 1
    local active = 0
    for i = 1, #pairs do
        local pair = pairs[i]
        if not pair.done then
            sample_pair(pair, "SAMPLE")
            if pair.left.unit:number_of_men_alive() == 0 or pair.right.unit:number_of_men_alive() == 0 then
                pair.done = true
                pair.left.uc:halt()
                pair.right.uc:halt()
                sample_pair(pair, "PAIR_DONE")
            else active = active + 1 end
        end
    end
    if active == 0 then finish("all_pairs_complete")
    elseif elapsed >= 300 then finish("300_second_timeout") end
end

local function start()
    if started then return end
    started = true
    log("START", "0")
    for i = 1, #pairs do
        local pair = pairs[i]
        pair.left.uc:attack_unit(pair.right.unit, true, true)
        pair.right.uc:attack_unit(pair.left.unit, true, true)
        sample_pair(pair, "INITIAL")
    end
    bm:repeat_callback(function() guard("tick", tick) end, 1000, "cc_calibration_tick")
end

guard("setup", function()
    local armies = { bm:alliances():item(1):armies():item(1), bm:alliances():item(2):armies():item(1) }
    for side = 1, 2 do
        local sentinel = script_unit:new(armies[side], "sentinel" .. side)
        sentinel.uc:take_control()
        sentinel.uc:morale_behavior_fearless()
        sentinel.uc:set_invincible(true)
        sentinel.uc:halt()
        sentinels[side] = sentinel
    end
    for i = 1, #specifications do
        local spec = specifications[i]
        local pair = { label = spec.label, done = false,
            left = script_unit:new(armies[1], spec.left), right = script_unit:new(armies[2], spec.right) }
        for _, unit in ipairs({ pair.left, pair.right }) do
            unit.uc:take_control()
            unit.uc:morale_behavior_fearless()
            unit.uc:fire_at_will(false)
        end
        table.insert(pairs, pair)
        log("UNIT_TYPES", "0," .. spec.label .. "," .. pair.left.unit:type() .. "," .. pair.right.unit:type())
    end
    -- Match Attila generated_battle's prevent_deployment=true path. Do not confuse
    -- end_deployment() with an engine start command: it only releases input focus.
    bm:setup_battle(function() guard("start", start) end)
    bm:register_phase_change_callback("Deployment", function() log("PHASE", "0,Deployment") end)
    bm:register_phase_change_callback("Deployed", function() log("PHASE", "0,Deployed") end)
    bm:register_phase_change_callback("VictoryCountdown", function() guard("victory", function() finish("victory_countdown") end) end)
    bm:register_phase_change_callback("Complete", function() log("ENGINE_COMPLETE", elapsed) end)
end)
