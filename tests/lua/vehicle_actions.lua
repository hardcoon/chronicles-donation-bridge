-- Actual Chronicles action/registry, fake native APIs; does not launch a game.
local function module(name)
    local environment = setmetatable({}, { __index = _G })
    local chunk = assert(loadfile(SOURCE_DIR .. "/game-addon/scripts/" .. name .. ".script"))
    setfenv(chunk, environment)()
    return environment
end
local state = {}
local function check(value, message) assert(value, message) end
function vector()
    return {
        set = function(self, x, y, z) self.x, self.y, self.z = x, y, z return self end,
        normalize = function(self)
            local size = math.sqrt(self.x^2+self.y^2+self.z^2)
            self.x, self.y, self.z = self.x/size, self.y/size, self.z/size
            return self
        end
    }
end
rq_target = { rqtStatic = 1 }
local function new_ray()
    state.ray_instances = (state.ray_instances or 0) + 1
    return {
        set_flags = function(_, flag) check(flag == rq_target.rqtStatic, "unsafe dynamic ray") end,
        set_position = function(self, p) self.position = p end,
        set_direction = function(self, d) self.direction = d end,
        set_range = function(self, r) self.range = r end,
        query = function(self)
            if state.blocked then return true end
            if state.block_exit and math.abs(self.position.x) < 0.001 and
                math.abs(self.position.z) < 0.001 and math.abs(self.direction.y) < 0.99 then
                return true
            end
            if state.low_roof and self.direction.y > 0.99 and self.range > 3 then return true end
            return self.direction.y < -0.99 and not state.no_ground
        end,
        get_distance = function() return 0.75 end
    }
end
-- Native IX-Ray exports a callable luabind class, not a plain Lua function.
ray_pick = setmetatable({}, { __call = function() return new_ray() end })
local actor = {
    id = function() return 1 end,
    position = function() return vector():set(0,0,0) end,
    direction = function() return vector():set(0,0,1) end,
    level_vertex_id = function() return 1 end,
    game_vertex_id = function() return 12 end,
    alive = function() return true end, IsInCar = function() return false end,
    is_talking = function() return false end, has_info = function() return false end
}
db = { actor = actor, storage = {} }
local function make_car(id, distance, story, section)
    local data = { fuel = 0, fuel_tank = 40, health = 1, enabled = true }
    function data:GetfHealth() return self.health end
    function data:fuel_system_enabled() return self.enabled end
    function data:CarExplode() state.explosions[#state.explosions+1] = id end
    return {
        id = function() return id end, name = function() return "car_"..id end,
        section = function() return section or "veh_dcp_niva" end,
        story_id = function() return story or 4294967295 end,
        parent = function() return nil end,
        position = function() return vector():set(0,0,distance) end,
        get_car = function() return data end
    }, data
end
nearest = {
    set = function(radius, position)
        check(radius > 0 and position.x ~= nil, "bad nearest signature")
        state.spatial_position = position
        state.spatial_radius = radius
    end,
    size = function()
        if state.crowded or state.require_far and state.spatial_position.z < 6 then return 257 end
        return #state.nearby
    end,
    get = function(index)
        check(index >= 0 and index < #state.nearby, "nearest.get outside [0,size)")
        return state.nearby[index+1]
    end
}
local ini = {}
function ini:section_exist(section) return section ~= "missing" end
function ini:line_exist(section, key) return key == "class" or state.quest and key == "quest_item" end
function ini:r_string(section, key) return state.wrong_class and "SCRPTCAR" or "C_NIVA" end
function ini:r_bool() return state.quest == true end
system_ini = function() return ini end
level = {
    present = function() return true end,
    valid_vertex_id = function(id) return id ~= 4294967295 end,
    vertex_in_direction = function(_, direction, radius)
        if state.invalid_vertex then return 4294967295 end
        state.vertex = state.vertex + 1
        state.points[state.vertex] = vector():set(direction.x * radius, 0, direction.z * radius)
        return state.vertex
    end,
    vertex_id = function(point)
        if state.invalid_vertex then return 4294967295 end
        state.vertex = state.vertex + 1
        state.points[state.vertex] = vector():set(point.x, point.y, point.z)
        return state.vertex
    end,
    is_accessible_vertex_id = function() return not state.inaccessible_vertex end,
    vertex_position = function(id)
        if state.distant_vertex then return vector():set(0,0,130) end
        return state.points[id]
    end,
    object_by_id = function(id) return state.online and state.clients[id] or nil end
}
local simulator = {}
function simulator:create(section, position, vertex, game_vertex)
    local distance2 = position.x^2 + position.z^2
    check(position.z > 0 and distance2 <= 10000, "spawn outside radius or not in front")
    if state.require_outside then check(distance2 > 100, "indoor fallback did not leave the local search radius")
    else check(distance2 <= 100, "ordinary spawn skipped the nearest local site") end
    if state.require_far then check(position.z >= 6, "search never reached the extended 10m radius") end
    check(vertex > 1 and game_vertex == 12, "bad graph ids")
    state.creates = state.creates + 1
    state.spawn_position = position
    if state.create_nil then return nil end
    if state.create_error then error("uncertain create") end
    local id = 10 + state.creates
    local server = { id = id, name = function() return "car_"..id end, section_name = function() return section end }
    state.servers[id] = server
    state.clients[id], state.car = make_car(id, position.z, nil, section)
    return server
end
function simulator:object(id) return state.servers[id] end
function simulator:release(server)
    if state.release_error then error("release failure") end
    state.releases = state.releases + 1
    state.servers[server.id], state.clients[server.id] = nil, nil
end
alife = function() return simulator end
local function reset()
    state = { nearby = { actor }, servers = {}, clients = {}, explosions = {}, creates = 0,
        releases = 0, online = false, vertex = 1, points = {}, clock = 1000 }
end
local function context() return { actor = actor, now = state.clock } end
local function spawn(params)
    local ctx = context()
    local status, reason = pf_donation_registry.dispatch("spawn_vehicle", ctx, params or {})
    return status, reason, ctx
end
pf_donation_registry = module("pf_donation_registry")
module("pf_donation_action_vehicle")
reset()
for _, invalid in ipairs({"0", "11", "-1", "1.5", "", "NaN", "1e0"}) do
    check(spawn({fuel_level = invalid}) == "rejected", "invalid fuel accepted")
end
check(spawn({vehicle = "veh_dcp_paz"}) == "rejected", "uninstalled model accepted")
check(spawn({code = "anything"}) == "rejected", "arbitrary parameter accepted")
state.wrong_class = true
check(spawn() == "rejected", "Anomaly car class accepted")
state.wrong_class = false
state.quest = true
check(spawn() == "rejected" and state.creates == 0, "quest vehicle spawned")
print("PASS Lua vehicle input, class and story-section guards")

local class_ray = ray_pick
local userdata_ray = newproxy(true)
getmetatable(userdata_ray).__call = function() return new_ray() end
for _, constructor in ipairs({ class_ray, userdata_ray, new_ray }) do
    reset()
    ray_pick = constructor
    local status, reason, pending = spawn()
    check(status == "pending" and state.creates == 1, "callable ray constructor rejected: "..tostring(reason))
    check(state.ray_instances == 1 and pending.vehicle_ray == nil, "probe recreated or retained after search")
end
ray_pick = class_ray
print("PASS Lua callable table/userdata and function ray constructors")

local function ray_rejected()
    reset()
    local status, reason = spawn()
    check(status == "rejected" and reason == "vehicle_ray_api_unavailable" and state.creates == 0,
        "unavailable ray API must reject before mutation, never defer: "..tostring(reason))
end
ray_pick = nil
ray_rejected()
for _, broken in ipairs({ {}, function() error("constructor failure") end, function() return nil end,
    function() return {} end, function() local ray = new_ray() ray.query = nil return ray end,
    function() local ray = new_ray() ray.set_flags = function() error("bad flags") end return ray end }) do
    ray_pick = broken
    ray_rejected()
end
ray_pick = class_ray
local static_target = rq_target
rq_target = nil
ray_rejected()
rq_target = {}
ray_rejected()
rq_target = static_target
print("PASS Lua missing/broken ray API is terminal and creates no vehicles")

for _, obstruction in ipairs({"blocked", "no_ground", "invalid_vertex", "distant_vertex", "crowded"}) do
    reset()
    state[obstruction] = true
    check(spawn() == "deferred" and state.creates == 0, "unsafe placement: "..obstruction)
end
reset()
state.nearby = { actor, make_car(55,3) }
check(spawn() == "deferred" and state.creates == 0, "overlapping vehicle spawned")
print("PASS Lua vehicle static/dynamic space, floor and radius guards")
reset()
state.require_far = true
check(spawn() == "pending" and state.creates == 1, "free site beyond 5m not used")
print("PASS Lua extended 10m search radius")
reset()
state.block_exit = true
state.require_outside = true
check(spawn() == "pending" and state.creates == 1, "building wall prevented an outdoor fallback spawn")
check(state.spawn_position.z > 10 and state.spawn_position.z <= 100,
    "outdoor fallback did not select the nearest extended radius")
print("PASS Lua indoor-to-outdoor fallback within 100m")

for _, fuel in ipairs({1,5,10}) do
  for _, tank in ipairs({12,42,250}) do
    reset()
    local status, _, ctx = spawn({fuel_level=tostring(fuel)})
    state.car.fuel_tank = tank
    check(status == "pending" and state.creates == 1, "spawn ACK preceded fuel")
    check(pf_donation_registry.poll("spawn_vehicle", ctx, 1100) == "pending", "offline object accepted")
    state.online = true
    check(pf_donation_registry.poll("spawn_vehicle", ctx, 1200) == "executed", "online spawn did not finish")
    check(state.car.fuel == fuel and state.creates == 1, "fuel is not actual litres or duplicate spawn")
  end
end
reset()
local _, _, ctx = spawn()
check(pf_donation_registry.poll("spawn_vehicle", ctx, 7000) == "rejected" and state.releases == 1, "timeout not rolled back")
reset()
_, _, ctx = spawn()
state.release_error = true
check(pf_donation_registry.poll("spawn_vehicle", ctx, 7000) == "uncertain", "rollback failure retried")
reset()
_, _, ctx = spawn()
state.servers[ctx.vehicle_id] = { section_name=function() return "other" end, name=function() return "reused" end }
check(pf_donation_registry.poll("spawn_vehicle", ctx, 1200) == "uncertain" and state.releases == 0, "reused ID mutated")
reset()
_, _, ctx = spawn()
state.online = true
state.car.enabled = false
check(pf_donation_registry.poll("spawn_vehicle", ctx, 1200) == "rejected" and state.releases == 1, "fuel-less car retained")
print("PASS Lua actual litres in different tank sizes, delayed online, rollback and ID reuse")
for _, section in ipairs({
    "veh_dcp_niva","veh_dcp_niva_green","veh_dcp_zaz968_2","veh_dcp_lada_2101",
    "veh_dcp_lada_dead","veh_dcp_uaz_van","veh_dcp_uaz_van_broken","veh_dcp_uaz_broken",
    "veh_dcp_gaz24","veh_dcp_moskvich_412","veh_dcp_raf","veh_dcp_moskvich_2715",
    "veh_dcp_uaz","veh_dcp_zil","veh_dcp_zil131","veh_dcp_mercedes_w123",
    "veh_dcp_lada_2107","veh_dcp_lada_2108","veh_dcp_niva_2329","veh_dcp_brdm",
    "veh_dcp_btr","veh_dcp_kamaz_army_tent","veh_dcp_kamaz_army","veh_btr",
    "veh_kamaz","veh_niva_g","veh_niva_w","veh_tr13",
    "veh_uaz","veh_zaz",
}) do
    reset()
    local status, _, variant = spawn({vehicle=section,fuel_level="5"})
    local legacy = not section:match("^veh_dcp_")
    state.car.enabled = not legacy
    state.online = true
    local completed, reason = pf_donation_registry.poll("spawn_vehicle", variant, 1200)
    check(status == "pending" and completed == "executed" and reason:match("_fuel_5L$") and state.car.fuel == 5,
        "vehicle variant failed: "..section)
    check(state.car.enabled == not legacy, "legacy fuel profile was changed")
end
reset()
state.create_nil = true
check(spawn() == "rejected", "nil create not rejected")
reset()
state.create_error = true
check(spawn() == "uncertain", "unknown create outcome retried")
reset()
_, _, ctx = spawn()
state.online = true
state.car.fuel_tank = 0
check(pf_donation_registry.poll("spawn_vehicle", ctx, 1200) == "rejected", "zero tank accepted")
reset()
local spatial_api = nearest
nearest = nil
check(spawn() == "rejected" and state.creates == 0, "missing spatial API didn't fail closed")
nearest = spatial_api
reset()
_, _, ctx = spawn({fuel_level="10"})
state.online = true
state.car.fuel_tank = 4
check(pf_donation_registry.poll("spawn_vehicle", ctx, 1200) == "rejected" and state.releases == 1,
    "small tank overflow silently clamped")
reset()
_, _, ctx = spawn()
state.online = true
state.car.fuel = nil
setmetatable(state.car, {__newindex=function(self, key, value) rawset(self, key, key=="fuel" and 0/0 or value) end})
check(pf_donation_registry.poll("spawn_vehicle", ctx, 1200) == "rejected", "NaN fuel readback accepted")
print("PASS Lua all 30 F1 choices including legacy and native API failure handling")

reset()
state.low_roof = true
check(spawn({vehicle="veh_dcp_kamaz_army_tent"}) == "deferred" and state.creates == 0,
    "tall truck spawned through low roof")
check(spawn({vehicle="veh_dcp_niva"}) == "pending", "small car wrongly needs truck headroom")
reset()
check(spawn({vehicle="veh_dcp_zil131"}) == "pending" and state.spawn_position.y >= 0.4,
    "negative model bounds were not lifted above ground")
reset()
check(spawn({vehicle="veh_tr13"}) == "pending" and state.spawn_position.z >= 9 and state.spatial_radius > 8,
    "large legacy bounds use small-car clearance")
print("PASS Lua model-specific headroom, ground lift and large-vehicle clearance")

reset()
local near, near_data = make_car(11, 3)
local far = make_car(12, 4)
local story = make_car(13, 1, 123)
local outside = make_car(14, 6)
state.nearby = { actor, far, story, outside, near }
check(pf_donation_registry.dispatch("explode_nearest_vehicle", context(), {radius_meters="5"}) == "executed",
    "no explosion")
check(#state.explosions == 1 and state.explosions[1] == 11, "not exactly the nearest unprotected car")
near_data.health = 0
state.explosions = {}
check(pf_donation_registry.dispatch("explode_nearest_vehicle", context(), {radius_meters="3"}) == "rejected" and
    #state.explosions == 0, "dead, story or distant car exploded")
check(pf_donation_registry.dispatch("explode_nearest_vehicle", context(), {radius_meters="2001"}) == "rejected", "radius overflow")
print("PASS Lua nearest-only explosion and protected/dead/outside exclusions")

-- Run the real wire runtime as well: no early ACK, no queue overtake or replay.
reset()
local incoming, outgoing, connected = {}, {}, false
time_global = function() return state.clock end
device = function() return { f_time_delta = 0.016 } end
pf_donation_protocol = module("pf_donation_protocol")
pf_donation_transport = {
    is_connected=function() return connected end,
    connect=function() connected=true return true end,
    disconnect=function() connected=false end,
    process_id=function() return 123 end,
    send=function(fields) if connected then outgoing[#outgoing+1]=fields end return connected end,
    poll=function() local result=incoming incoming={} return result end
}
local runtime = module("pf_donation_runtime")
local function tick(fields)
    if fields then incoming[#incoming+1]=fields end
    state.clock=state.clock+100
    runtime.update()
end
local function command(id)
    return {"COMMAND", id, "test:"..id, "spawn_vehicle", "100", "RUB", "fuel_level=5"}
end
local function results(id)
    local list={}
    for _, row in ipairs(outgoing) do if row[1]=="RESULT" and row[2]==id then list[#list+1]=row end end
    return list
end
tick()
local session=outgoing[1][5]
tick({"WELCOME","2","1.3.0"})
ray_pick=nil
tick(command("no-ray"))
check(results("no-ray")[1][3]=="rejected" and state.creates==0, "missing API didn't send terminal ACK")
ray_pick=class_ray
tick(command("no-ray"))
check(#results("no-ray")==2 and results("no-ray")[2][3]=="rejected" and state.creates==0,
    "terminal failure was re-executed after API recovery")
print("PASS Lua missing ray API terminal ACK and deduplication")
tick(command("first"))
tick(command("first"))
check(state.creates==1 and #results("first")==0, "pending duplicate spawned again or early ACK")
tick({"RESULT_QUERY","first",session})
check(#results("first")==0, "query cancelled pending spawn")
tick(command("second"))
check(state.creates==1 and results("second")[1][3]=="deferred", "queue overtook pending spawn")
connected=false
state.online=true
tick() -- completes while disconnected and stores ACK, then reconnects.
state.clock=state.clock+1000 -- allow the normal reconnect backoff.
tick({"WELCOME","2","1.3.0"})
tick({"RESULT_QUERY","first",session})
check(results("first")[1][3]=="executed" and state.creates==1, "lost ACK not recovered safely")
tick(command("first"))
check(state.creates==1, "completed spawn repeated")
state.online=false
tick(command("before-load"))
check(state.creates==2, "second command not dispatched")
runtime.on_game_load()
check(results("before-load")[1][3]=="uncertain", "save load didn't terminate pending ID")
state.online=true
state.car.fuel=7
state.clock=state.clock+2000
tick()
check(state.car.fuel==7, "old pending setter ran in new save")
tick(command("before-load"))
check(state.creates==2, "save-load uncertain action replayed")
print("PASS Lua pending spawn queue, reconnect/ACK recovery and save-load isolation")
