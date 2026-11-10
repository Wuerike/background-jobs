-- Promote a validated exact-time snapshot, assigning a fresh FIFO entry.
-- ARGV: id, snapshot, scheduled score, exact due UTC, now UTC, history JSON, snapshot identity.
checkTypes()
if #ARGV ~= 7 then return redis.error_reply('Invalid promotion arguments') end
local state = redis.call('HGET', KEYS[1], 'state')
if redis.call('HGET', KEYS[1], 'promotionIdentity') == ARGV[7] then
    local expected = cjson.decode(ARGV[2])
    local original = {}
    for i = 1, #expected[1], 2 do original[expected[1][i]] = expected[1][i+1] end
    for _, field in ipairs({'id','name','args','maxAttempts','retryDelayMs','createdAt','creationIdentity'}) do
        if redis.call('HGET', KEYS[1], field) ~= original[field] then error('Promoted job changed') end
    end
    -- A receipt proves progress only together with the preserved history prefix.
    for k = 2, 3 do
        for i = 1, #expected[k] do
            if redis.call('LINDEX', KEYS[k], i-1) ~= expected[k][i] then error('Promoted history changed') end
        end
    end
    local promotion = cjson.decode(redis.call('LINDEX', KEYS[2], #expected[2]) or '{}')
    local last = cjson.decode(redis.call('LINDEX', KEYS[2], -1) or '{}')
    local ready = redis.call('ZSCORE', KEYS[5], ARGV[1])
    local scheduled = redis.call('ZSCORE', KEYS[6], ARGV[1])
    local processing = redis.call('SISMEMBER', KEYS[8], ARGV[1])
    if promotion.state ~= 'enqueued' or
       (promotion.reason ~= 'schedule eligible' and promotion.reason ~= 'retry eligible') or
       ((state == 'scheduled') ~= (redis.call('HEXISTS', KEYS[1], 'scheduledAt') == 1)) or
       last.state ~= state or last.at ~= redis.call('HGET', KEYS[1], 'updatedAt') or
       redis.call('SISMEMBER', KEYS[4], ARGV[1]) ~= 1 or
       (state == 'enqueued' and (not ready or scheduled or processing ~= 0)) or
       (state == 'processing' and (ready or scheduled or processing ~= 1)) or
       (state == 'scheduled' and (ready or not scheduled or processing ~= 0)) or
       ((state == 'succeeded' or state == 'failed') and (ready or scheduled or processing ~= 0)) or
       (state ~= 'enqueued' and state ~= 'processing' and state ~= 'scheduled' and state ~= 'succeeded' and state ~= 'failed') then
        return redis.error_reply('Inconsistent promoted job')
    end
    return 0
end
checkSnapshot(ARGV[2])
local n = tonumber(redis.call('HGET', KEYS[1], 'attempts'))
local max = tonumber(redis.call('HGET', KEYS[1], 'maxAttempts'))
if state ~= 'scheduled' or not n or not max or n >= max or
   redis.call('SISMEMBER', KEYS[4], ARGV[1]) ~= 1 or
   redis.call('SISMEMBER', KEYS[8], ARGV[1]) ~= 0 or
   redis.call('ZSCORE', KEYS[5], ARGV[1]) or
   tonumber(redis.call('ZSCORE', KEYS[6], ARGV[1])) ~= tonumber(ARGV[3]) or
   redis.call('HGET', KEYS[1], 'scheduledAt') ~= ARGV[4] or ARGV[4] > ARGV[5] then
    return redis.error_reply('Invalid promotion precondition')
end
local sequence = redis.call('GET', KEYS[7]) or '0'
local tail = redis.call('ZRANGE', KEYS[5], -1, -1, 'WITHSCORES')
if #tail > 0 and (tonumber(tail[2]) > tonumber(sequence) or tonumber(tail[2]) < 1 or tonumber(tail[2]) % 1 ~= 0) then
    return redis.error_reply('Inconsistent FIFO sequence')
end
if not string.match(sequence, '^%d+$') or (#sequence > 1 and string.sub(sequence, 1, 1) == '0') or
   #sequence > 16 or tonumber(sequence) >= 9007199254740991 then
    return redis.error_reply('Invalid or exhausted FIFO sequence')
end
redis.call('INCR', KEYS[7])
redis.call('ZREM', KEYS[6], ARGV[1])
redis.call('ZADD', KEYS[5], redis.call('GET', KEYS[7]), ARGV[1])
redis.call('HSET', KEYS[1], 'state', 'enqueued', 'updatedAt', ARGV[5], 'promotionIdentity', ARGV[7])
redis.call('HDEL', KEYS[1], 'scheduledAt')
redis.call('RPUSH', KEYS[2], ARGV[6])
return 1
