-- Shared read-only guards for per-job transitions and candidate reads. All eight keys are explicit:
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

local function snapshotMatches(json)
    local expected = cjson.decode(json)
    local hash = redis.call('HGETALL', KEYS[1])
    if #hash ~= #expected[1] then return false end
    for i = 1, #expected[1], 2 do
        if redis.call('HGET', KEYS[1], expected[1][i]) ~= expected[1][i+1] then return false end
    end
    for k = 2, 3 do
        local list = redis.call('LRANGE', KEYS[k], 0, -1)
        if #list ~= #expected[k] then return false end
        for i = 1, #list do if list[i] ~= expected[k][i] then return false end end
    end
    return true
end

local function checkIndexes(id)
    local state = redis.call('HGET', KEYS[1], 'state')
    local ready = redis.call('ZSCORE', KEYS[5], id)
    local scheduled = redis.call('ZSCORE', KEYS[6], id)
    local processing = redis.call('SISMEMBER', KEYS[8], id)
    local due = redis.call('HGET', KEYS[1], 'scheduledAt')
    if redis.call('SISMEMBER', KEYS[4], id) ~= 1 or
       ((state == 'scheduled') ~= (due ~= false)) or
       (state == 'enqueued' and (not ready or scheduled or processing ~= 0)) or
       (state == 'scheduled' and (ready or not scheduled or processing ~= 0)) or
       (state == 'processing' and (ready or scheduled or processing ~= 1)) or
       ((state == 'succeeded' or state == 'failed' or state == 'deleted') and (ready or scheduled or processing ~= 0)) or
       (state ~= 'enqueued' and state ~= 'scheduled' and state ~= 'processing' and state ~= 'succeeded' and state ~= 'failed' and state ~= 'deleted') then
        error('Inconsistent job indexes')
    end
    if ready then
        local sequence = redis.call('GET', KEYS[7])
        local n = tonumber(ready)
        if not sequence or not string.match(sequence, '^%d+$') or
           (#sequence > 1 and string.sub(sequence, 1, 1) == '0') or
           #sequence > 16 or tonumber(sequence) > 9007199254740991 or
           n < 1 or n % 1 ~= 0 or n > tonumber(sequence) then error('Invalid FIFO metadata') end
    end
    local last = cjson.decode(redis.call('LINDEX', KEYS[2], -1) or '{}')
    if last.state ~= state or last.at ~= redis.call('HGET', KEYS[1], 'updatedAt') then error('Invalid job history') end
end

local function incrementDecimal(value)
    local carry = 1
    local result = ''
    for i = #value, 1, -1 do
        local digit = tonumber(string.sub(value, i, i)) + carry
        carry = digit >= 10 and 1 or 0
        result = tostring(digit % 10) .. result
    end
    return (carry == 1 and '1' or '') .. result
end

-- A changed snapshot is a race only with preserved immutable data and history
-- plus an appended transition. A receipt alone does not prove progress.
local function checkProgress(json, id)
    local expected = cjson.decode(json)
    local original = {}
    for i = 1, #expected[1], 2 do original[expected[1][i]] = expected[1][i+1] end
    for _, field in ipairs({'id','name','args','maxAttempts','retryDelayMs','createdAt','creationIdentity'}) do
        if redis.call('HGET', KEYS[1], field) ~= original[field] then error('Job immutable data changed') end
    end
    local history = redis.call('LRANGE', KEYS[2], 0, -1)
    if #history <= #expected[2] then error('Job changed without progress') end
    for i = 1, #expected[2] do
        if history[i] ~= expected[2][i] then error('Job history prefix changed') end
    end
    local cycle = original.cycle or '0'
    local previous = cjson.decode(expected[2][#expected[2]])
    local transitions = {scheduled = {enqueued = true, deleted = true}, enqueued = {processing = true, deleted = true},
        processing = {scheduled = true, succeeded = true, failed = true, enqueued = true},
        succeeded = {enqueued = true}, failed = {enqueued = true}, deleted = {enqueued = true}}
    for i = #expected[2] + 1, #history do
        local change = cjson.decode(history[i])
        if not transitions[previous.state] or not transitions[previous.state][change.state] or
           type(change.reason) ~= 'string' or change.reason == '' or change.at < previous.at then
            error('Invalid progress transition')
        end
        if previous.state == 'succeeded' or previous.state == 'failed' or previous.state == 'deleted' then
            if change.reason ~= 'manual requeue' then error('Invalid cycle transition') end
            cycle = incrementDecimal(cycle)
        end
        previous = change
    end
    if (redis.call('HGET', KEYS[1], 'cycle') or '0') ~= cycle then error('Invalid progress cycle') end
    local attempts = redis.call('LRANGE', KEYS[3], 0, -1)
    if #attempts < #expected[3] then error('Attempt history shortened') end
    for i = 1, #expected[3] do
        if attempts[i] ~= expected[3][i] then
            local old = cjson.decode(expected[3][i])
            local current = cjson.decode(attempts[i])
            if i ~= #expected[3] or old.finishedAt ~= cjson.null or
               current.attempt ~= old.attempt or current.startedAt ~= old.startedAt or
               current.finishedAt == cjson.null or
               (current.outcome ~= 'succeeded' and current.outcome ~= 'failed' and current.outcome ~= 'interrupted') then
                error('Attempt history prefix changed')
            end
        end
    end
    local total = 0
    local count = 0
    local max = tonumber(redis.call('HGET', KEYS[1], 'maxAttempts'))
    for i, jsonChange in ipairs(history) do
        local change = cjson.decode(jsonChange)
        if change.reason == 'manual requeue' then count = 0 end
        if change.state == 'processing' then
            total = total + 1
            count = count + 1
            local attempt = cjson.decode(attempts[total] or '{}')
            if attempt.attempt ~= count or attempt.startedAt ~= change.at then error('Invalid progressed attempt') end
            if i < #history then
                local nextChange = cjson.decode(history[i+1])
                if attempt.finishedAt ~= nextChange.at or
                   (nextChange.state == 'enqueued' and (attempt.outcome ~= 'interrupted' or count >= max)) or
                   (nextChange.state == 'scheduled' and (attempt.outcome ~= 'failed' or count >= max)) or
                   (nextChange.state == 'succeeded' and attempt.outcome ~= 'succeeded') or
                   (nextChange.state == 'failed' and (count ~= max or
                       (attempt.outcome ~= 'failed' and attempt.outcome ~= 'interrupted'))) then
                    error('Invalid progressed attempt closure')
                end
            end
        end
    end
    if total ~= #attempts or tostring(count) ~= redis.call('HGET', KEYS[1], 'attempts') then error('Invalid progressed counter') end
    checkIndexes(id)
end
