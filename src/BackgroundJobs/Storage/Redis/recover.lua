-- Recover one fully materialized open attempt. No write precedes validation.
-- ARGV: id, snapshot, recovery UTC, next state, interrupted attempt, history.
checkTypes()
if #ARGV ~= 6 then error('Invalid recovery arguments') end
if not snapshotMatches(ARGV[2]) then
    checkProgress(ARGV[2], ARGV[1])
    local original = cjson.decode(ARGV[2])
    local attempt = cjson.decode(redis.call('LINDEX', KEYS[3], #original[3]-1) or '{}')
    if attempt.outcome ~= 'interrupted' then error('Recovery attempt was not interrupted') end
    return {0, redis.call('HGETALL', KEYS[1]), redis.call('LRANGE', KEYS[2], 0, -1),
        redis.call('LRANGE', KEYS[3], 0, -1), redis.call('ZSCORE', KEYS[6], ARGV[1]) or ''}
end
checkIndexes(ARGV[1])
local n = tonumber(redis.call('HGET', KEYS[1], 'attempts'))
local max = tonumber(redis.call('HGET', KEYS[1], 'maxAttempts'))
local index = tonumber(redis.call('HGET', KEYS[1], 'attemptIndex'))
local token = redis.call('HGET', KEYS[1], 'attemptToken')
if redis.call('HGET', KEYS[1], 'state') ~= 'processing' or
   not n or not max or n < 1 or n > max or not token or token == '' or
   not index or index ~= redis.call('LLEN', KEYS[3])-1 or
   ARGV[3] < redis.call('HGET', KEYS[1], 'updatedAt') or
   ARGV[4] ~= (n < max and 'enqueued' or 'failed') then error('Invalid recovery precondition') end
local open = cjson.decode(redis.call('LINDEX', KEYS[3], index) or '{}')
local closed = cjson.decode(ARGV[5])
local change = cjson.decode(ARGV[6])
if open.attempt ~= n or open.finishedAt ~= cjson.null or open.outcome ~= cjson.null or open.error ~= cjson.null or
   closed.attempt ~= n or closed.startedAt ~= open.startedAt or closed.finishedAt ~= ARGV[3] or
   closed.outcome ~= 'interrupted' or type(closed.error) ~= 'string' or closed.error == '' or
   ARGV[3] < open.startedAt or change.state ~= ARGV[4] or change.at ~= ARGV[3] or
   type(change.reason) ~= 'string' or change.reason == '' then error('Invalid interrupted attempt') end
if ARGV[4] == 'enqueued' then
    local sequence = redis.call('GET', KEYS[7]) or '0'
    local tail = redis.call('ZRANGE', KEYS[5], -1, -1, 'WITHSCORES')
    if not string.match(sequence, '^%d+$') or (#sequence > 1 and string.sub(sequence, 1, 1) == '0') or
       #sequence > 16 or tonumber(sequence) >= 9007199254740991 or
       (#tail > 0 and (tonumber(tail[2]) > tonumber(sequence) or tonumber(tail[2]) < 1 or tonumber(tail[2]) % 1 ~= 0)) then
        error('Invalid or exhausted FIFO sequence')
    end
end
redis.call('LSET', KEYS[3], index, ARGV[5])
redis.call('RPUSH', KEYS[2], ARGV[6])
redis.call('SREM', KEYS[8], ARGV[1])
redis.call('HSET', KEYS[1], 'state', ARGV[4], 'updatedAt', ARGV[3])
redis.call('HDEL', KEYS[1], 'scheduledAt', 'attemptToken', 'attemptIndex', 'completionIdentity', 'promotionIdentity')
if ARGV[4] == 'enqueued' then
    redis.call('INCR', KEYS[7])
    redis.call('ZADD', KEYS[5], redis.call('GET', KEYS[7]), ARGV[1])
end
return {1}
