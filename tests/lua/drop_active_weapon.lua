local registered
local clock, current_item, dropped = 1000, nil, nil
local weapon = { section = function() return "wpn_test" end }
local actor = {
    active_slot = function() return current_item and 2 or 0 end,
    active_item = function() return current_item end,
    drop_item = function(_, item) dropped = item end
}
db = { actor = actor }
system_ini = function() return {
    section_exist = function(_, section) return section == "wpn_test" end,
    line_exist = function() return false end
} end
pf_donation_registry = { register = function(_, descriptor) registered = descriptor return true end }

local environment = setmetatable({}, { __index = _G })
local chunk = assert(loadfile(SOURCE_DIR .. "/game-addon/scripts/pf_donation_action_drop_active_weapon.script"))
setfenv(chunk, environment)()
local context = { actor = actor, now = clock }
assert(registered.can_execute(context, { wait_seconds = "5" }))
local status = registered.execute(context)
assert(status == "pending", "missing weapon did not enter bounded wait")
clock = 3000
assert(registered.poll(context, clock) == "pending", "wait finished too early")
current_item = weapon
local completed, reason = registered.poll(context, clock + 100)
assert(completed == "executed" and reason == "weapon_dropped_wpn_test" and dropped == weapon,
    "weapon drawn during the wait was not dropped")

current_item, dropped = nil, nil
context = { actor = actor, now = 10000 }
assert(registered.can_execute(context, { wait_seconds = "1" }))
assert(registered.execute(context) == "pending")
local rejected, rejected_reason = registered.poll(context, 11000)
assert(rejected == "rejected" and rejected_reason == "active_weapon_required",
    "bounded weapon wait did not finish")
print("PASS Lua active weapon action waits for a drawn weapon without blocking")
