-- Explicit development trial: actual engine combat with automated attack orders.
-- Does not change stats, health, morale, routing, placement, casualties or winner.
-- Installed only in a separately marked development runtime. Normal packs omit it.
local cc_trial_units = { attacker = {}, defender = {} }
local cc_trial_specifications = {
@@CC_TRIAL_UNITS@@
}
local cc_trial_stopped = false
local cc_trial_elapsed = 0
local function cc_trial_log(kind, detail)
    dev.log("CC_ACTUAL_TRIAL|" .. kind .. "|" .. tostring(detail or ""))
end
local function cc_trial_guard(where, action)
    local ok, err = pcall(action)
    if not ok then cc_trial_log("ERROR", where .. ": " .. tostring(err)) end
    return ok
end
local function cc_trial_active(entry)
    return entry.su.unit:number_of_men_alive() > 0 and not entry.su.unit:is_routing()
end
local function cc_trial_orders(side, opponent)
    local active_fighters = 0
    for _, entry in ipairs(cc_trial_units[side]) do
        if not entry.general and cc_trial_active(entry) then active_fighters = active_fighters + 1 end
    end
    for _, entry in ipairs(cc_trial_units[side]) do
        if (not entry.general or active_fighters == 0) and cc_trial_active(entry) then
            local here = entry.su.unit:position()
            local target = nil
            local distance = 0
            for _, enemy in ipairs(cc_trial_units[opponent]) do
                if cc_trial_active(enemy) then
                    local there = enemy.su.unit:position()
                    local dx, dz = here:get_x() - there:get_x(), here:get_z() - there:get_z()
                    local candidate = dx * dx + dz * dz
                    if target == nil or candidate < distance then
                        target = enemy
                        distance = candidate
                    end
                end
            end
            if target then
                if not target.su or not target.su.unit then error("Invalid target reference " .. tostring(target.name)) end
                local there = target.su.unit:position()
                local visible = target.su.unit:is_visible_to_alliance(entry.su.alliance)
                local action = ""
                -- Native attack orders can be ineffective against unseen targets.
                -- Walk/run normally into contact; do not reveal or teleport units.
                if not visible or distance > 180 * 180 then
                    action = "run"
                    entry.su.uc:goto_location(there, true)
                else
                    action = "attack"
                    entry.su.uc:attack_unit(target.su.unit, true, true)
                end
                cc_trial_log("ORDER", cc_trial_elapsed .. "|" .. entry.name .. "|" .. action .. "|" .. target.name .. "|" ..
                    here:get_x() .. "|" .. here:get_z() .. "|" .. there:get_x() .. "|" .. there:get_z() .. "|" ..
                    (distance ^ 0.5) .. "|" .. tostring(visible) .. "|call-completed")
            end
        end
    end
end
local function cc_trial_tick()
    if cc_trial_stopped then return end
    cc_trial_elapsed = cc_trial_elapsed + 10
    cc_trial_guard("orders", function()
        cc_trial_orders("attacker", "defender")
        cc_trial_orders("defender", "attacker")
    end)
    cc_trial_guard("telemetry", function()
        for side, entries in pairs(cc_trial_units) do
            for _, entry in ipairs(entries) do
                cc_trial_log("SAMPLE", cc_trial_elapsed .. "|" .. side .. "|" .. entry.name .. "|" ..
                    entry.su.unit:number_of_men_alive() .. "|" .. entry.su.unit:number_of_enemies_killed() .. "|" .. tostring(entry.su.unit:is_routing()))
            end
        end
    end)
    if not cc_trial_stopped then bm:callback(cc_trial_tick, 10000) end
end
local function cc_trial_start()
    cc_trial_guard("collect", function()
        -- Validate every explicit survivor-report reference before taking control
        -- of any unit, avoiding a partly controlled army on a missing reference.
        for _, entry in ipairs(cc_trial_specifications) do
            if not entry.su or not entry.su.unit or not entry.su.uc or not entry.su.alliance then error("Missing explicit generated unit/alliance: " .. entry.name) end
            if not cc_trial_units[entry.side] then error("Unknown generated side: " .. entry.side) end
            table.insert(cc_trial_units[entry.side], entry)
        end
        cc_trial_log("START", #cc_trial_units.attacker .. "|" .. #cc_trial_units.defender)
        if #cc_trial_units.attacker == 0 or #cc_trial_units.defender == 0 then error("Missing actual generated side") end
        for _, entry in ipairs(cc_trial_specifications) do
            entry.su.uc:take_control()
            if entry.general then entry.su.uc:halt() end
        end
        cc_trial_orders("attacker", "defender")
        cc_trial_orders("defender", "attacker")
        bm:callback(cc_trial_tick, 10000)
    end)
end
bm:register_phase_change_callback("Deployed", function() bm:callback(cc_trial_start, 1000) end)
bm:register_phase_change_callback("Complete", function()
    cc_trial_stopped = true
    cc_trial_log("ENGINE_COMPLETE", cc_trial_elapsed)
end)
cc_trial_log("LOADED", "Normal approach/visible attack orders and actual telemetry only; generals held until no regular fighters remain")
