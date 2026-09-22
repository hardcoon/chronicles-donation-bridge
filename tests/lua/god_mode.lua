local clock, god_enabled = 1000, false
local registered
local registry_actions = {}
local commands = {}

time_global = function() return clock end
db = { actor = {} }
local console = {
    get_bool = function(_, name) assert(name == "g_god") return god_enabled end,
    execute = function(_, command)
        assert(command == "g_god 1" or command == "g_god 0", "unexpected command: " .. tostring(command))
        commands[#commands + 1] = command
        god_enabled = command == "g_god 1"
    end
}
get_console = function() return console end
pf_donation_registry = {
    register = function(action_id, descriptor)
        assert(action_id == "god_mode")
        if registry_actions[action_id] ~= nil then return false, "action_already_registered" end
        registry_actions[action_id] = descriptor
        registered = descriptor
        return true
    end,
    get = function(action_id) return registry_actions[action_id] end
}

local environment = setmetatable({}, { __index = _G })
local chunk = assert(loadfile(SOURCE_DIR .. "/game-addon/scripts/pf_donation_action_invulnerability.script"))
setfenv(chunk, environment)()
assert(registered and registered.can_execute and registered.execute and registered.update and registered.on_game_load)

registry_actions.god_mode, registered = nil, nil
assert(environment.register() == true and registered ~= nil, "runtime registration was not restored")

local context = { actor = db.actor, now = clock }
assert(registered.can_execute(context, { duration_seconds = "5" }))
local status, reason = registered.execute(context, { duration_seconds = "5" })
assert(status == "executed" and reason == "god_mode_seconds_5" and god_enabled)
assert(commands[#commands] == "g_god 1", "native enable command was not used")

clock = 3000
context = { actor = db.actor, now = clock }
assert(registered.can_execute(context, { duration_seconds = "5" }))
assert(registered.execute(context, { duration_seconds = "5" }) == "executed")
clock = 7500
registered.update(clock)
assert(god_enabled, "repeated god mode did not extend the active effect")
clock = 8000
registered.update(clock)
assert(not god_enabled and commands[#commands] == "g_god 0", "native disable command was not used")

god_enabled = true
clock = 9000
context = { actor = db.actor, now = clock }
assert(registered.can_execute(context, { duration_seconds = "5" }))
assert(registered.execute(context, { duration_seconds = "5" }) == "executed")
clock = 14000
registered.update(clock)
assert(god_enabled and commands[#commands] == "g_god 1", "pre-existing g_god state was overwritten")

god_enabled = false
clock = 15000
context = { actor = db.actor, now = clock }
assert(registered.can_execute(context, { duration_seconds = "30" }))
assert(registered.execute(context, { duration_seconds = "30" }) == "executed" and god_enabled)
registered.on_game_load()
assert(not god_enabled, "loading a save retained temporary g_god")

for _, value in ipairs({ "", "4", "601", "5.5", "-1" }) do
    local ok, rejected_status, rejected_reason = registered.can_execute(
        { actor = db.actor, now = clock }, { duration_seconds = value })
    assert(ok == false and rejected_status == "rejected" and rejected_reason == "god_mode_duration_invalid")
end
local ok, rejected_status, rejected_reason = registered.can_execute(
    { actor = db.actor, now = clock }, { duration_seconds = "30", extra = "1" })
assert(ok == false and rejected_status == "rejected" and rejected_reason == "parameter_not_supported")

environment.get_console = function() return nil end
local unavailable, unavailable_status, unavailable_reason = registered.can_execute(
    { actor = db.actor, now = clock }, { duration_seconds = "30" })
assert(unavailable == false and unavailable_status == "deferred" and unavailable_reason == "god_mode_console_unavailable")

print("PASS Lua timed native g_god commands, extension, restore and load cleanup")
