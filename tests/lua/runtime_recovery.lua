-- Runs the real runtime/protocol modules against a fake IX-Ray host; no game.
local clock, connected, disconnects, executions, loads = 1000, false, 0, 0, 0
local frame_delta, dispatch_failure = 0.016, false
local incoming, outgoing = {}, {}

time_global = function() return clock end
device = function() return { f_time_delta = frame_delta } end
level = { present = function() return true end }
db = { storage = {}, actor = {
    alive = function() return true end, IsInCar = function() return false end,
    is_talking = function() return false end, has_info = function() return false end
} }
printf = function() end

local function module(name)
    local environment = setmetatable({}, { __index = _G })
    local chunk = assert(loadfile(SOURCE_DIR .. "/game-addon/scripts/" .. name .. ".script"))
    setfenv(chunk, environment)()
    return environment
end

pf_donation_protocol = module("pf_donation_protocol")
pf_donation_transport = {
    is_connected = function() return connected end,
    connect = function() connected = true return true end,
    disconnect = function() connected = false disconnects = disconnects + 1 end,
    process_id = function() return 12345 end,
    send = function(fields)
        local wire = assert(pf_donation_protocol.encode_fields(fields))
        outgoing[#outgoing + 1] = assert(pf_donation_protocol.decode_line(wire:sub(1, -2)))
        return connected
    end,
    poll = function(limit)
        local result = {}
        while #incoming > 0 and #result < limit do result[#result + 1] = table.remove(incoming, 1) end
        return result
    end
}
pf_donation_registry = {
    update = function() end,
    on_game_load = function() loads = loads + 1 end,
    dispatch = function()
        executions = executions + 1
        if dispatch_failure then error("simulated partial execution") end
        return "executed", "ok"
    end
}

local runtime = module("pf_donation_runtime")
local function tick(fields)
    if fields then incoming[#incoming + 1] = fields end
    clock = clock + 100
    runtime.update()
end
local function last(kind)
    for index = #outgoing, 1, -1 do if outgoing[index][1] == kind then return outgoing[index] end end
    error("missing " .. kind)
end
local function command(id)
    return { "COMMAND", id, "test:" .. id, "add_radiation", "100", "RUB", "percent=25" }
end
local function expect_result(id, status, reason)
    local result = last("RESULT")
    assert(result[2] == id and result[3] == status and result[4] == reason, "unexpected result")
end

tick()
local hello = last("HELLO")
assert(#hello == 5 and hello[2] == "2" and #hello[5] > 0)
local session_id = hello[5]
tick({ "WELCOME", "2", "1.1.0" })
tick(command("completed"))
assert(executions == 1)
tick({ "RESULT_QUERY", "completed", session_id })
expect_result("completed", "executed", "ok")
assert(executions == 1, "result query executed the action again")
print("PASS Lua read-only cached result query")

runtime.on_game_load()
assert(connected and disconnects == 0 and loads == 1, "load discarded live transport")
tick({ "RESULT_QUERY", "completed", session_id })
expect_result("completed", "executed", "ok")
assert(executions == 1, "load cleared the cached result")
clock = clock + 2000
print("PASS Lua save loading retains connection and result cache")

frame_delta = 0
tick(command("deferred"))
expect_result("deferred", "deferred", "game_paused")
tick({ "RESULT_QUERY", "deferred", session_id })
expect_result("deferred", "deferred", "game_paused")
assert(executions == 1)
frame_delta = 0.016
tick(command("deferred"))
assert(executions == 1, "same attempt ID was re-executed after deferral")
tick(command("new-attempt"))
assert(executions == 2)
print("PASS Lua deferred ACK recovery preserves original attempt")

tick({ "RESULT_QUERY", "missing", session_id })
expect_result("missing", "uncertain", "result_not_cached")
tick({ "RESULT_QUERY", "completed", "wrong-session" })
expect_result("completed", "uncertain", "game_session_changed")
assert(executions == 2, "unknown query executed an effect")
print("PASS Lua unknown and wrong-session queries fail closed")

connected = false
tick()
assert(last("HELLO")[5] == session_id, "reconnect changed runtime identity")
tick({ "WELCOME", "2", "1.1.0" })
tick({ "RESULT_QUERY", "completed", session_id })
expect_result("completed", "executed", "ok")
assert(executions == 2)
print("PASS Lua reconnect preserves session identity")

frame_delta = 0
tick({ "PING" })
assert(outgoing[#outgoing][1] == "PONG" and outgoing[#outgoing - 1][1] == "STATUS" and
    outgoing[#outgoing - 1][2] == "waiting", "PONG resumed stale readiness")
frame_delta = 0.016
print("PASS Lua PONG refreshes readiness first")

dispatch_failure = true
tick(command("partial"))
expect_result("partial", "uncertain", "action_dispatch_failed")
tick(command("partial"))
assert(executions == 3, "uncertain partial action was executed twice")
print("PASS Lua dispatch failure is cached as uncertain")

__pf_donation_runtime_v1 = nil
connected = false
runtime = module("pf_donation_runtime")
tick()
assert(last("HELLO")[5] ~= session_id, "new Lua runtime reused session identity")
tick({ "WELCOME", "2", "1.1.0" })
tick({ "RESULT_QUERY", "completed", session_id })
expect_result("completed", "uncertain", "game_session_changed")
assert(executions == 3)
print("PASS Lua runtime restart cannot recover an old session")
