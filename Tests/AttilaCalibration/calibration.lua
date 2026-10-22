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
local inactive_units = {}
local native_names = @@NATIVE_NAMES@@
local sentinel_names = @@SENTINEL_NAMES@@

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

local function position_pair(pair, kind)
    local left = pair.left.unit:position()
    local right = pair.right.unit:position()
    local left_order = pair.left.unit:ordered_position()
    local right_order = pair.right.unit:ordered_position()
    log(kind, elapsed .. "," .. pair.label .. "," .. left:get_x() .. "," .. left:get_z() .. "," ..
        right:get_x() .. "," .. right:get_z() .. "," .. left_order:get_x() .. "," .. left_order:get_z() .. "," ..
        right_order:get_x() .. "," .. right_order:get_z())
end

local function finish(reason)
    if completed then return end
    completed = true
    for i = 1, #pairs do sample_pair(pairs[i], "FINAL") end
    log("COMPLETE", elapsed .. "," .. reason)
    -- Root closes the test after COMPLETE; do not introduce post-sample scripted casualties.
end

local function tick()
    if completed or not started then return end
    elapsed = elapsed + 1
    local active = 0
    for i = 1, #pairs do
        local pair = pairs[i]
        if not pair.done then
            sample_pair(pair, "SAMPLE")
            if elapsed % 5 == 0 then position_pair(pair, "POSITION") end
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
    local ui_ok, ui_err = pcall(function()
        -- Same restoration used by native cutscene:release(); our replacement script
        -- bypasses that cutscene, so it must restore the HUD, camera and conflict clock.
        bm:enable_cinematic_ui(false, true, false)
        bm:enable_cinematic_camera(false)
        bm:change_conflict_time_update_overridden(false)
    end)
    log("UI_RESTORE", tostring(ui_ok) .. "," .. tostring(ui_err))
    bm:camera():fade(false, 0.5)
    if native_names then
        for side = 1, 2 do
            sentinels[side].uc:teleport_to_location(v(side == 1 and -650 or 650, 0), side == 1 and 90 or 270, 20)
        end
    end
    for i = 1, #pairs do
        local pair = pairs[i]
        position_pair(pair, "POSITION_BEFORE_TELEPORT")
        if native_names then
            local lane = (i - (#specifications + 1) / 2) * 80
            pair.left.uc:teleport_to_location(v(-35, lane), 90, 35)
            pair.right.uc:teleport_to_location(v(35, lane), 270, 35)
        end
        position_pair(pair, "POSITION_AFTER_TELEPORT")
        sample_pair(pair, "INITIAL")
    end
    bm:callback(function() guard("attack", function()
        for i = 1, #pairs do
            local pair = pairs[i]
            pair.left.uc:take_control()
            pair.right.uc:take_control()
            pair.left.uc:attack_unit(pair.right.unit, true, true)
            pair.right.uc:attack_unit(pair.left.unit, true, true)
            position_pair(pair, "ATTACK_ORDER")
        end
        local camera_ok, camera_err = pcall(function()
            local focus = pairs[1]
            local left = focus.left.unit:position()
            local right = focus.right.unit:position()
            local x = (left:get_x() + right:get_x()) / 2
            local z = (left:get_z() + right:get_z()) / 2
            local y = math.max(left:get_y(), right:get_y())
            bm:camera():move_to(v(x - 100, y + 140, z + 150), v(x, y + 2, z), 0, false, 0, 0, 0)
        end)
        log("CAMERA", tostring(camera_ok) .. "," .. tostring(camera_err))
    end) end, 1000, "cc_calibration_attack")
    bm:repeat_callback(function() guard("tick", tick) end, 1000, "cc_calibration_tick")
end

guard("setup", function()
    local armies = { bm:alliances():item(1):armies():item(1), bm:alliances():item(2):armies():item(1) }
    if native_names then
        local active_names = {}
        for _, name in ipairs(sentinel_names) do active_names[name] = true end
        for _, spec in ipairs(specifications) do active_names[spec.left] = true; active_names[spec.right] = true end
        for side = 1, 2 do
            for _, name in ipairs(native_names[side]) do
                if not active_names[name] then
                    local unused = script_unit:new(armies[side], name)
                    unused.uc:take_control()
                    unused.uc:halt()
                    unused:set_enabled(false)
                    table.insert(inactive_units, unused)
                end
            end
        end
    end
    for side = 1, 2 do
        local sentinel = script_unit:new(armies[side], sentinel_names[side])
        sentinel.uc:take_control()
        sentinel:set_enabled(true)
        sentinel:set_always_visible(true)
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
            unit:set_enabled(true)
            unit:set_always_visible(true)
            unit.uc:morale_behavior_fearless()
            unit.uc:fire_at_will(false)
        end
        table.insert(pairs, pair)
        log("UNIT_TYPES", "0," .. spec.label .. "," .. pair.left.unit:type() .. "," .. pair.right.unit:type())
    end
    -- Match Attila generated_battle's prevent_deployment=true path. Do not confuse
    -- end_deployment() with an engine start command: it only releases input focus.
    bm:setup_battle(function() bm:callback(function() guard("start", start) end, 500, "cc_calibration_start") end)
    bm:register_phase_change_callback("Deployment", function() log("PHASE", "0,Deployment") end)
    bm:register_phase_change_callback("Deployed", function() log("PHASE", "0,Deployed") end)
    bm:register_phase_change_callback("VictoryCountdown", function() guard("victory", function() finish("victory_countdown") end) end)
    bm:register_phase_change_callback("Complete", function() log("ENGINE_COMPLETE", elapsed) end)
end)
