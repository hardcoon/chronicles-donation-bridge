local registered
local dropped = {}
local outfit = { id = function() return 1 end, section = function() return "outfit" end }
local actor = {
    item_in_slot = function(_, slot) return slot == 7 and outfit or nil end,
    drop_item = function(_, item) dropped[#dropped + 1] = item end
}
system_ini = function() return {
    section_exist = function(_, section) return section == "outfit" end,
    line_exist = function() return false end
} end
get_object_story_id = function() return nil end
pf_donation_registry = { register = function(_, descriptor) registered = descriptor return true end }

local environment = setmetatable({}, { __index = _G })
local chunk = assert(loadfile(SOURCE_DIR .. "/game-addon/scripts/pf_donation_action_drop_outfit.script"))
setfenv(chunk, environment)()
local context = { actor = actor }
assert(registered.can_execute(context, { target = "both" }) == true,
    "both rejected the only equipped removable item")
local status, reason = registered.execute(context)
assert(status == "executed" and reason == "gear_dropped_both_1" and #dropped == 1,
    "both did not drop the one equipped item")

actor.item_in_slot = function() return nil end
local ok, rejected, why = registered.can_execute({ actor = actor }, { target = "both" })
assert(ok == false and rejected == "rejected" and why == "removable_gear_required",
    "both without equipment was not rejected")
print("PASS Lua equipment drop accepts either equipped part for the both target")
