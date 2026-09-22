local registered
local random_calls = 0
local first_requested_distance
local vertex_calls = 0
local moved_to

local positions = {
    [101] = { x = 510, y = 0, z = 0 },
    [102] = { x = 742, y = 0, z = 0 },
    [103] = { x = 990, y = 0, z = 0 }
}
local test_math = {}
for key, value in pairs(math) do test_math[key] = value end
test_math.random = function()
    random_calls = random_calls + 1
    return random_calls == 1 and 0.5 or 0
end

local function new_vector()
    return {
        set = function(self, x, y, z)
            self.x, self.y, self.z = x, y, z
            return self
        end
    }
end

local test_level = {
    valid_vertex_id = function(vertex_id) return vertex_id ~= nil and vertex_id ~= 4294967295 end,
    is_accessible_vertex_id = function(vertex_id) return positions[vertex_id] ~= nil end,
    vertex_position = function(vertex_id) return positions[vertex_id] end,
    vertex_id = function(desired)
        if first_requested_distance == nil then
            first_requested_distance = math.sqrt(desired.x * desired.x + desired.z * desired.z)
        end
        vertex_calls = vertex_calls + 1
        return 100 + ((vertex_calls - 1) % 3) + 1
    end,
    vertex_in_direction = function() return 4294967295 end
}
local actor = {
    level_vertex_id = function() return 1 end,
    position = function() return { x = 0, y = 0, z = 0 } end,
    set_actor_position = function(_, position) moved_to = position end
}

pf_donation_registry = {
    register = function(action_id, descriptor)
        assert(action_id == "teleport_random")
        registered = descriptor
        return true
    end
}

local environment = setmetatable({
    math = test_math,
    level = test_level,
    vector = new_vector
}, { __index = _G })
local chunk = assert(loadfile(SOURCE_DIR .. "/game-addon/scripts/pf_donation_action_teleport_random.script"))
setfenv(chunk, environment)()
assert(registered and registered.can_execute and registered.execute,
    "random teleport action is not registered")

local context = { actor = actor }
assert(registered.can_execute(context, {
    minimum_meters = "500",
    maximum_meters = "1000"
}) == true)
assert(math.abs(first_requested_distance - 750) < 0.001,
    "navigation search did not start at the preselected random distance")
assert(math.abs(context.teleport_target_distance - 750) < 0.001,
    "random target distance was not preserved")
assert(math.abs(context.teleport_distance - 742) < 0.001,
    "search did not select the safe vertex closest to the target distance")
local status, reason = registered.execute(context)
assert(status == "executed" and reason == "teleported_randomly_742m" and moved_to ~= nil)

print("PASS Lua random distance is selected before the nearest safe teleport vertex")
