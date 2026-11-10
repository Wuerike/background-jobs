-- Close a confirmed attempt and update state/history/indexes atomically.
-- ARGV: id, snapshot, cycle, token, identity, finish UTC, next state,
--       due UTC (or empty), score (or empty), closed attempt JSON, history JSON.
-- Return 1 for a new completion or exact replay. A different result/token/cycle
-- fails before writing. Identity includes the captured finish and result.
checkTypes()
if #ARGV ~= 11 then return redis.error_reply('Invalid completion arguments') end
if redis.call('HGET', KEYS[1], 'cycle') ~= ARGV[3] or
   redis.call('HGET', KEYS[1], 'attemptToken') ~= ARGV[4] then
    return redis.error_reply('Attempt identity mismatch')
end
if redis.call('HGET', KEYS[1], 'completionIdentity') == ARGV[5] then return 1 end
checkSnapshot(ARGV[2])
checkMembership(ARGV[1], 1)
local index = tonumber(redis.call('HGET', KEYS[1], 'attemptIndex'))
local score = tonumber(ARGV[9])
if redis.call('HGET', KEYS[1], 'state') ~= 'processing' or
   redis.call('ZSCORE', KEYS[5], ARGV[1]) or not index or index % 1 ~= 0 or
   index ~= redis.call('LLEN', KEYS[3])-1 or
   (ARGV[7] ~= 'succeeded' and ARGV[7] ~= 'failed' and ARGV[7] ~= 'scheduled') or
   (ARGV[7] == 'scheduled' and (ARGV[8] == '' or not score or score ~= score or math.abs(score) > 9007199254740991)) then
    return redis.error_reply('Invalid completion precondition')
end
redis.call('LSET', KEYS[3], index, ARGV[10])
redis.call('SREM', KEYS[8], ARGV[1])
redis.call('RPUSH', KEYS[2], ARGV[11])
redis.call('HSET', KEYS[1], 'state', ARGV[7], 'updatedAt', ARGV[6], 'completionIdentity', ARGV[5])
if ARGV[7] == 'scheduled' then
    redis.call('HSET', KEYS[1], 'scheduledAt', ARGV[8])
    redis.call('ZADD', KEYS[6], ARGV[9], ARGV[1])
else
    redis.call('HDEL', KEYS[1], 'scheduledAt')
end
return 1
