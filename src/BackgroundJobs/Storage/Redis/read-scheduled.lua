-- Read the current generation, indexes and snapshot atomically; no writes.
-- A changed score is validated against its current exact due before discarding.
checkTypes()
if #ARGV ~= 1 then error('Invalid selection arguments') end
checkIndexes(ARGV[1])
return {redis.call('HGETALL', KEYS[1]), redis.call('LRANGE', KEYS[2], 0, -1),
    redis.call('LRANGE', KEYS[3], 0, -1), redis.call('ZSCORE', KEYS[6], ARGV[1]) or ''}
