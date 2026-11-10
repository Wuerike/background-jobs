-- Promote a validated exact-time snapshot, assigning a fresh FIFO entry.
-- ARGV: id, snapshot, scheduled score, exact due UTC, now UTC, history JSON.
-- Returns {1} on promotion or {0, hash, history, attempts, current score} on proven progress.
checkTypes()
if #ARGV ~= 6 then return redis.error_reply('Invalid promotion arguments') end
local state = redis.call('HGET', KEYS[1], 'state')
if not snapshotMatches(ARGV[2]) then
    checkProgress(ARGV[2], ARGV[1])
    return {0, redis.call('HGETALL', KEYS[1]), redis.call('LRANGE', KEYS[2], 0, -1),
        redis.call('LRANGE', KEYS[3], 0, -1), redis.call('ZSCORE', KEYS[6], ARGV[1]) or ''}
end
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
redis.call('HSET', KEYS[1], 'state', 'enqueued', 'updatedAt', ARGV[5])
redis.call('HDEL', KEYS[1], 'scheduledAt')
redis.call('RPUSH', KEYS[2], ARGV[6])
return {1}
