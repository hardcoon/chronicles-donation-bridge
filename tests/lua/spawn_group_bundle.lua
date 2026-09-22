-- Actual Chronicles NPC/mutant actions with fake safe spawn APIs; never launches the game.
local function module(name)
    local environment = setmetatable({}, { __index = _G })
    local chunk = assert(loadfile(SOURCE_DIR .. "/game-addon/scripts/" .. name .. ".script"))
    setfenv(chunk, environment)()
    return environment
end
local function check(value, message) assert(value, message) end

function vector()
    return { set = function(self, x, y, z) self.x, self.y, self.z = x, y, z return self end }
end

local state = {}
local actor = {
    position = function() return vector():set(0, 0, 0) end,
    level_vertex_id = function() return 1 end,
    game_vertex_id = function() return 2 end
}
level = {
    valid_vertex_id = function(id) return id ~= nil and id ~= 4294967295 end,
    vertex_in_direction = function(_, direction, radius)
        state.next_vertex = state.next_vertex + 1
        state.positions[state.next_vertex] = vector():set(direction.x * radius, 0, direction.z * radius)
        return state.next_vertex
    end,
    vertex_position = function(id) return state.positions[id] end
}
local ini = {}
function ini:section_exist(section) return section ~= "missing" end
system_ini = function() return ini end

local simulator = {}
function simulator:create(section, _, vertex, game_vertex)
    check(vertex > 1 and game_vertex == 2, "bad spawn graph ids")
    if state.fail_at ~= nil and #state.created + 1 == state.fail_at then return nil end
    local object = { section = section, serial = #state.created + 1 }
    state.created[#state.created + 1] = object
    return object
end
function simulator:release(object) state.released[#state.released + 1] = object end
alife = function() return simulator end

local function reset(fail_at)
    state = { next_vertex = 1, positions = {}, created = {}, released = {}, fail_at = fail_at }
end

pf_donation_registry = module("pf_donation_registry")
module("pf_donation_action_spawn_mutants")
module("pf_donation_action_spawn_npcs")

reset()
local status, reason = pf_donation_registry.dispatch("spawn_mutants", { actor = actor },
    { groups = "dog*weak*2;chimera*strong*1" })
check(status == "executed" and reason == "spawned_mutant_groups_2_3", "mutant bundle did not execute")
check(#state.created == 3 and state.created[1].section == "dog_weak" and
    state.created[3].section == "chimera_normal", "mutant group order or variants changed")

reset()
status, reason = pf_donation_registry.dispatch("spawn_npcs", { actor = actor },
    { groups = "bandit*medium*2;duty*strong*1" })
check(status == "executed" and reason == "spawned_npc_groups_2_3", "NPC bundle did not execute")
check(#state.created == 3 and state.created[1].section == "sim_default_bandit_2" and
    state.created[3].section == "sim_default_duty_4", "NPC group order or variants changed")

reset(3)
status, reason = pf_donation_registry.dispatch("spawn_mutants", { actor = actor },
    { groups = "dog*medium*2;boar*strong*2" })
check(status == "rejected" and reason == "mutant_spawn_failed_rolled_back",
    "partial group failure was not rejected atomically")
check(#state.released == 2, "partial group was not rolled back as one transaction")

reset()
status, reason = pf_donation_registry.dispatch("spawn_npcs", { actor = actor },
    { faction = "stalker", strength = "weak", count = "2" })
check(status == "executed" and reason == "spawned_npc_groups_1_2",
    "legacy single-group command stopped working")

for _, invalid in ipairs({ "", ";dog*weak*1", "dog*weak*0", "dog*weak*100",
    "dog*weak*1;;boar*weak*1", "dog-only" }) do
    reset()
    check(pf_donation_registry.dispatch("spawn_mutants", { actor = actor }, { groups = invalid }) == "rejected",
        "invalid mutant group bundle accepted: " .. invalid)
end
reset()
check(pf_donation_registry.dispatch("spawn_npcs", { actor = actor },
    { groups = "bandit*weak*1", code = "anything" }) == "rejected", "arbitrary parameter accepted")

print("PASS Lua NPC/mutant group bundles are atomic, validated and legacy-compatible")
