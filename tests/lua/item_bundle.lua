-- Actual Chronicles item action with fake safe inventory APIs; never launches the game.
local function module(name)
    local environment = setmetatable({}, { __index = _G })
    local chunk = assert(loadfile(SOURCE_DIR .. "/game-addon/scripts/" .. name .. ".script"))
    setfenv(chunk, environment)()
    return environment
end
local function check(value, message) assert(value, message) end

local groups = { medkit = "medicine", bread = "food", ammo_9x18_fmj = "ammo_explosives" }
local ini = {}
function ini:section_exist(section)
    return section == "inventory_sort_registry" or section == "pf_donation_item_allowlist" or groups[section] ~= nil
end
function ini:line_exist(section, key)
    if section == "inventory_sort_registry" or section == "pf_donation_item_allowlist" then
        return groups[key] ~= nil
    end
    return false
end
function ini:r_string(section, key) return groups[key] end
system_ini = function() return ini end

local actor = {
    id = function() return 7 end,
    position = function() return { x = 1, y = 2, z = 3 } end,
    level_vertex_id = function() return 10 end,
    game_vertex_id = function() return 20 end
}
local state = { created = {}, released = {}, fail_at = nil }
local simulator = {}
function simulator:create(section, position, level_vertex, game_vertex, parent_id)
    check(position.x == 1 and level_vertex == 10 and game_vertex == 20 and parent_id == 7, "bad item create context")
    if state.fail_at ~= nil and #state.created + 1 == state.fail_at then return nil end
    local object = { section = section, serial = #state.created + 1 }
    state.created[#state.created + 1] = object
    return object
end
function simulator:release(object)
    state.released[#state.released + 1] = object
end
alife = function() return simulator end

pf_donation_registry = module("pf_donation_registry")
module("pf_donation_action_spawn_items")
local function spawn(params)
    return pf_donation_registry.dispatch("spawn_items", { actor = actor }, params)
end

local status, reason = spawn({ items = "medkit*2;bread*3;ammo_9x18_fmj*4" })
check(status == "executed" and reason == "spawned_item_bundle_3_9", "multi-item bundle did not execute")
check(#state.created == 9 and state.created[1].section == "medkit" and state.created[3].section == "bread", "bundle order or counts changed")

state = { created = {}, released = {}, fail_at = 4 }
status, reason = spawn({ items = "medkit*2;bread*3" })
check(status == "rejected" and reason == "item_spawn_failed_rolled_back",
    "partial bundle failure not rejected: " .. tostring(status) .. "/" .. tostring(reason))
check(#state.released == 3, "partial bundle was not rolled back as one transaction")

state = { created = {}, released = {}, fail_at = nil }
status, reason = spawn({ item = "medkit", count = "2" })
check(status == "executed" and reason == "spawned_item_bundle_1_2", "legacy single-item command stopped working")
for _, invalid in ipairs({ "", ";medkit*1", "medkit*0", "medkit*51", "medkit*1;;bread*1", "bad item*1" }) do
    check(spawn({ items = invalid }) == "rejected", "invalid bundle accepted: " .. invalid)
end
check(spawn({ items = "medkit*1", code = "anything" }) == "rejected", "arbitrary parameter accepted")

print("PASS Lua item bundles are atomic, validated and legacy-compatible")
