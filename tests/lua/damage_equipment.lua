local registered
local condition = 1
local slot_reads = 0

local item = {
    section = function() return "test_outfit" end,
    condition = function() return condition end,
    set_condition = function(_, value) condition = value end
}
local helmet = nil
local actor = {
    item_in_slot = function(_, slot)
        slot_reads = slot_reads + 1
        if slot == 7 then return item end
        assert(slot == 12)
        return helmet
    end
}
local ini = {
    section_exist = function(_, section) return section == "test_outfit" end,
    line_exist = function() return false end
}

system_ini = function() return ini end
pf_donation_registry = {
    register = function(action_id, descriptor)
        assert(action_id == "damage_equipment")
        registered = descriptor
        return true
    end
}

local environment = setmetatable({}, { __index = _G })
local chunk = assert(loadfile(SOURCE_DIR .. "/game-addon/scripts/pf_donation_action_damage_equipment.script"))
setfenv(chunk, environment)()
assert(registered and registered.can_execute and registered.execute,
    "damage equipment action is not registered")

local context = { actor = actor }
assert(registered.can_execute(context, { target = "outfit", percent = "25" }) == true)
actor.item_in_slot = function() error("execute must reuse the inspected item") end
local status, reason = registered.execute(context, { target = "outfit", percent = "25" })
assert(status == "executed" and reason == "equipment_damaged_outfit_1")
assert(math.abs(condition - 0.75) < 0.0001, "outfit condition was not reduced by 25 percent")
assert(slot_reads == 1, "equipment slot was inspected more than once")

actor.item_in_slot = function(_, slot) return slot == 7 and item or nil end
context = { actor = actor }
assert(registered.can_execute(context, { target = "both", percent = "25" }) == true,
    "both rejected one equipped damageable item")
status, reason = registered.execute(context, { target = "both", percent = "25" })
assert(status == "executed" and reason == "equipment_damaged_both_1",
    "both did not damage the one equipped item")

print("PASS Lua equipment damage reuses validated items and accepts a partial both target")
