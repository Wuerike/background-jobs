-- A receipt is retained per logical operation, independently of later job cycles.
-- The snapshot proves the decision, not merely a compatible current state.
local receiptKey = KEYS[9]
local executorKey = KEYS[10]
local generation = ARGV[#ARGV]
ARGV[#ARGV] = nil
local operationIdentity = ARGV[#ARGV]
ARGV[#ARGV] = nil
KEYS[10] = nil
KEYS[9] = nil
checkTypes()
local kind = redis.call('TYPE', receiptKey).ok
if kind ~= 'none' and kind ~= 'string' then error('Invalid operation receipt type') end
local executorKind = redis.call('TYPE', executorKey).ok
if executorKind ~= 'none' and executorKind ~= 'string' then error('Invalid executor generation type') end
if generation ~= '' and redis.call('GET', executorKey) ~= generation then error('Executor generation expired') end
local function readReceipt()
    local value = redis.call('GET', receiptKey)
    if not value then return nil end
    local receipt = cjson.decode(value)
    if type(receipt) ~= 'table' or type(receipt.snapshot) ~= 'string' or receipt.identity ~= operationIdentity or receipt.result == nil then
        error('Invalid operation receipt')
    end
    if receipt.checksum ~= redis.sha1hex(operationIdentity .. '|' .. cjson.encode(receipt.result) .. '|' .. receipt.snapshot) then
        error('Operation receipt contents changed')
    end
    if receipt.snapshot ~= '' then
        if not snapshotMatches(receipt.snapshot) then checkProgress(receipt.snapshot, ARGV[1]) end
        checkIndexes(ARGV[1])
    elseif redis.call('EXISTS', KEYS[1]) == 1 then
        checkIndexes(ARGV[1])
    elseif redis.call('EXISTS', KEYS[2], KEYS[3]) ~= 0 or
        redis.call('SISMEMBER', KEYS[4], ARGV[1]) ~= 0 or redis.call('SISMEMBER', KEYS[8], ARGV[1]) ~= 0 or
        redis.call('ZSCORE', KEYS[5], ARGV[1]) or redis.call('ZSCORE', KEYS[6], ARGV[1]) then
        error('Orphaned receipt indexes')
    end
    return receipt.result
end
local recorded = readReceipt()
if recorded ~= nil then return recorded end
