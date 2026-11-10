-- ARGV: id, validated snapshot (empty for missing), operation, UTC, history JSON, due score for scheduled or next exact cycle for requeue.
-- Returns 1 applied, 2 absent, 3 conflict, 0 proven progress without writes.
checkTypes()
if #ARGV ~= 6 or (ARGV[3] ~= 'delete' and ARGV[3] ~= 'requeue') then error('Invalid management arguments') end
if ARGV[2] == '' then
    if redis.call('EXISTS', KEYS[1]) == 1 then return 0 end
    if redis.call('EXISTS', KEYS[2], KEYS[3]) ~= 0 or
       redis.call('SISMEMBER', KEYS[4], ARGV[1]) ~= 0 or
       redis.call('SISMEMBER', KEYS[8], ARGV[1]) ~= 0 or
       redis.call('ZSCORE', KEYS[5], ARGV[1]) or redis.call('ZSCORE', KEYS[6], ARGV[1]) then
        error('Orphaned job indexes')
    end
    return 2
end
if not snapshotMatches(ARGV[2]) then checkProgress(ARGV[2], ARGV[1]); return 0 end
checkIndexes(ARGV[1])
local state = redis.call('HGET', KEYS[1], 'state')
if state == 'scheduled' then
    -- Exact score was validated by C# and is included in the snapshot as due.
    if tonumber(redis.call('ZSCORE', KEYS[6], ARGV[1])) ~= tonumber(ARGV[6]) then error('Invalid scheduled score') end
end
if ARGV[3] == 'delete' then
    if state ~= 'enqueued' and state ~= 'scheduled' then return 3 end
else
    if state ~= 'succeeded' and state ~= 'failed' and state ~= 'deleted' then return 3 end
end
if ARGV[4] < redis.call('HGET', KEYS[1], 'updatedAt') then error('Management time precedes update') end
if ARGV[3] == 'delete' then
    redis.call('ZREM', state == 'scheduled' and KEYS[6] or KEYS[5], ARGV[1])
    redis.call('HSET', KEYS[1], 'state', 'deleted', 'updatedAt', ARGV[4])
else
    local sequence = redis.call('GET', KEYS[7]) or '0'
    local tail = redis.call('ZRANGE', KEYS[5], -1, -1, 'WITHSCORES')
    if not string.match(sequence, '^%d+$') or (#sequence > 1 and string.sub(sequence, 1, 1) == '0') or
       #sequence > 16 or tonumber(sequence) >= 9007199254740991 or
       (#tail > 0 and (tonumber(tail[2]) > tonumber(sequence) or tonumber(tail[2]) < 1 or tonumber(tail[2]) % 1 ~= 0)) then
        error('Invalid or exhausted FIFO sequence')
    end
    redis.call('INCR', KEYS[7])
    redis.call('ZADD', KEYS[5], redis.call('GET', KEYS[7]), ARGV[1])
    redis.call('HSET', KEYS[1], 'state', 'enqueued', 'attempts', 0, 'cycle', ARGV[6], 'updatedAt', ARGV[4])
    redis.call('HDEL', KEYS[1], 'attemptToken', 'attemptIndex', 'completionIdentity', 'promotionIdentity')
end
redis.call('HDEL', KEYS[1], 'scheduledAt')
redis.call('RPUSH', KEYS[2], ARGV[5])
return 1
