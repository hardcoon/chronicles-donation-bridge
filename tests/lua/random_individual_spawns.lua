local descriptors, created = {}, {}
local next_vertex, next_id = 10, 100

vector = function()
    return { set = function(self, x, y, z) self.x, self.y, self.z = x, y, z return self end }
end
level = {
    valid_vertex_id = function() return true end,
    vertex_in_direction = function() next_vertex = next_vertex + 1 return next_vertex end,
    vertex_position = function() return { x = 20, y = 0, z = 0 } end,
    object_by_id = function() return nil end
}
local life = {
    create = function(_, profile)
        assert(not tostring(profile):match("pf_donation_random"), "simulation squad section was spawned")
        next_id = next_id + 1
        local object = { id = next_id, profile = profile }
        created[#created + 1] = object
        return object
    end,
    release = function() end,
    object = function() return {} end
}
alife = function() return life end
system_ini = function() return { section_exist = function() return true end } end
relation_registry = {
    community_relation = function() return -1000 end,
    community_goodwill = function() return 0 end
}
time_global = function() return 1000 end
local actor = {
    position = function() return { x = 0, y = 0, z = 0 } end,
    level_vertex_id = function() return 1 end,
    game_vertex_id = function() return 2 end,
    character_community = function() return "actor" end,
    id = function() return 99 end
}
db = { actor = actor }
pf_donation_registry = {
    register = function(action_id, descriptor) descriptors[action_id] = descriptor return true end
}

local function load_action(name)
    local environment = setmetatable({}, { __index = _G })
    local chunk = assert(loadfile(SOURCE_DIR .. "/game-addon/scripts/" .. name .. ".script"))
    setfenv(chunk, environment)()
end

load_action("pf_donation_action_spawn_random_hostile_squad")
local hostile = descriptors.spawn_random_hostile_squad
local context = { actor = actor, now = 1000 }
assert(hostile.can_execute(context, { minimum_count = "2", maximum_count = "2" }))
local status, reason = hostile.execute(context, { minimum_count = "2", maximum_count = "2" })
assert(status == "executed" and reason:match("spawned_random_hostile_npcs_2_") and #created == 2,
    "hostile action did not create two independent NPCs")

created = {}
load_action("pf_donation_action_spawn_random_mutant_pack")
local mutants = descriptors.spawn_random_mutant_pack
context = { actor = actor, now = 1000 }
assert(mutants.can_execute(context, { minimum_count = "3", maximum_count = "3" }))
status, reason = mutants.execute(context, { minimum_count = "3", maximum_count = "3" })
assert(status == "executed" and reason:match("spawned_random_mutants_3_") and #created == 3,
    "mutant action did not create three independent mutants")

print("PASS Lua random hostile and mutant actions use independent ALife creates without squads")
