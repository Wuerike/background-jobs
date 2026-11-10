-- Shared read-only guards for claim/complete. All eight keys are explicit:
-- job hash, history list, attempts list, jobs set, ready zset, scheduled zset,
-- sequence string, processing set. No writes until every precondition passes.
local function checkTypes()
    local types = {'hash','list','list','set','zset','zset','string','set'}
    if #KEYS ~= 8 then error('Invalid processing keys') end
    for i = 1, 8 do
        local kind = redis.call('TYPE', KEYS[i]).ok
        if kind ~= 'none' and kind ~= types[i] then error('Invalid processing key type at position ' .. i) end
    end
end
local function checkSnapshot(json)
    local expected = cjson.decode(json)
    local hash = redis.call('HGETALL', KEYS[1])
    if #hash ~= #expected[1] then error('Job snapshot changed') end
    for i = 1, #expected[1], 2 do
        if redis.call('HGET', KEYS[1], expected[1][i]) ~= expected[1][i+1] then error('Job snapshot changed') end
    end
    for k = 2, 3 do
        local list = redis.call('LRANGE', KEYS[k], 0, -1)
        if #list ~= #expected[k] then error('Job history changed') end
        for i = 1, #list do
            if list[i] ~= expected[k][i] then error('Job history changed') end
        end
    end
end
local function checkMembership(id, processing)
    if redis.call('SISMEMBER', KEYS[4], id) ~= 1 or
       redis.call('ZSCORE', KEYS[6], id) or
       redis.call('SISMEMBER', KEYS[8], id) ~= processing then error('Inconsistent processing indexes') end
end
