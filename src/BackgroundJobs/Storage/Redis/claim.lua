-- Reserve a previously materialized candidate and open its attempt atomically.
-- ARGV: id, snapshot, cycle, token, start UTC, attempt JSON, history JSON, FIFO position.
-- Return 0 if the candidate lost the head race (including empty ready); 1 confirms.
-- Records without cycle use the initial cycle 0. No TTL, creation fields remain intact.
-- Repeating a claim is NOT a confirmation: the caller must not invoke on uncertainty.
checkTypes()
if #ARGV ~= 8 or ARGV[4] == '' then return redis.error_reply('Invalid claim arguments') end
local head = redis.call('ZRANGE', KEYS[5], 0, 0)
if head[1] ~= ARGV[1] then return 0 end
local position = tonumber(redis.call('ZSCORE', KEYS[5], ARGV[1]))
local sequence = redis.call('GET', KEYS[7])
if not position or position < 1 or position % 1 ~= 0 or position > 9007199254740991 or
   not sequence or not string.match(sequence, '^%d+$') or
   (#sequence > 1 and string.sub(sequence, 1, 1) == '0') or
   #sequence > 16 or tonumber(sequence) > 9007199254740991 or tonumber(sequence) < position then
    return redis.error_reply('Invalid FIFO metadata')
end
if position ~= tonumber(ARGV[8]) then return 0 end
checkSnapshot(ARGV[2])
checkMembership(ARGV[1], 0)
local state = redis.call('HGET', KEYS[1], 'state')
local n = tonumber(redis.call('HGET', KEYS[1], 'attempts'))
local max = tonumber(redis.call('HGET', KEYS[1], 'maxAttempts'))
if state ~= 'enqueued' or not n or not max or n >= max or
   (redis.call('HGET', KEYS[1], 'cycle') or '0') ~= ARGV[3] or
   redis.call('HEXISTS', KEYS[1], 'scheduledAt') == 1 then
    return redis.error_reply('Invalid claim precondition')
end
-- JSON and temporal values are produced and validated by C# before this point.
redis.call('ZREM', KEYS[5], ARGV[1])
redis.call('SADD', KEYS[8], ARGV[1])
redis.call('RPUSH', KEYS[3], ARGV[6])
redis.call('RPUSH', KEYS[2], ARGV[7])
redis.call('HSET', KEYS[1], 'state', 'processing', 'attempts', n+1,
    'updatedAt', ARGV[5], 'cycle', ARGV[3], 'attemptToken', ARGV[4],
    'attemptIndex', redis.call('LLEN', KEYS[3])-1)
redis.call('HDEL', KEYS[1], 'completionIdentity')
return 1
