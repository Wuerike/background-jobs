--[[
Operação atômica: criar um job pendente com seus dados, histórico inicial e índices.
As verificações e escritas executam sem intercalamento de outros comandos Redis.
Uma criação concluída inclui o ID em jobs e em exatamente um índice de pendentes:
ready para enqueued, com nova sequência FIFO; scheduled para agendamento futuro.
Nenhuma tentativa é aberta e nenhuma chave recebe TTL.

Entradas (preparadas e serializadas pela biblioteca C#):
  KEYS[1..7]: hash do job, lista de histórico, lista de tentativas, set jobs,
             sorted set ready, sorted set scheduled, contador sequence.
  ARGV[1..6]: id, name, args JSON, state, maxAttempts, retryDelayMs.
  ARGV[7..10]: createdAt, updatedAt, scheduledAt (vazio se ausente), score
              agendado (milissegundos Unix arredondados para cima; vazio se ausente).
  ARGV[11..12]: entrada inicial do histórico em JSON, identidade imutável da criação
               (SHA-256 calculado em C# sobre os dados originais, incluindo o ID).

Retorno: 1 confirma criação nova ou repetição equivalente já persistida. Mesmo ID
e identidade, com histórico existente, não alteram dados, histórico ou sequência.
Outro pedido com o mesmo ID gera erro; o script não gera IDs nem deduplica pedidos
com IDs diferentes. Não há garantia de execução exactly-once dos handlers.

Erros de precondição (tipos, parâmetros, colisão, resíduos ou contador) são
detectados antes da primeira escrita. Atomicidade não fornece rollback de
escritas anteriores a um erro de runtime; manter as verificações antes delas.
]]

-- Conferir tipos antes de usar comandos específicos ou alterar qualquer chave.
local types = {'hash', 'list', 'list', 'set', 'zset', 'zset', 'string'}
for i = 1, #KEYS do
    local kind = redis.call('TYPE', KEYS[i]).ok
    if kind ~= 'none' and kind ~= types[i] then
        return redis.error_reply('Invalid storage key type at position ' .. i)
    end
end
if #KEYS ~= 7 or #ARGV ~= 12 then return redis.error_reply('Invalid creation parameters') end
local state = ARGV[4]
local attempts = tonumber(ARGV[5])
local delay = tonumber(ARGV[6])
local score = tonumber(ARGV[10])
if ARGV[1] == '' or ARGV[2] == '' or ARGV[3] == '' or ARGV[7] == '' or
   ARGV[8] == '' or ARGV[11] == '' or ARGV[12] == '' or
   not attempts or attempts < 1 or attempts > 10 or attempts % 1 ~= 0 or
   not delay or delay < 0 or delay > 60000 or delay % 1 ~= 0 or
   (state ~= 'enqueued' and state ~= 'scheduled') or
   (state == 'enqueued' and ARGV[9] ~= '') or
   (state == 'scheduled' and (ARGV[9] == '' or not score or score ~= score or
      math.abs(score) > 9007199254740991 or score % 1 ~= 0)) then
    return redis.error_reply('Invalid creation parameters')
end
-- Reconhecer a mesma criação sem reaplicar seu estado inicial a um job existente.
if redis.call('EXISTS', KEYS[1]) == 1 then
    if redis.call('HGET', KEYS[1], 'creationIdentity') == ARGV[12] and
       redis.call('LLEN', KEYS[2]) > 0 then return 1 end
    return redis.error_reply('Job ID collision or inconsistent creation')
end
-- Um ID novo não pode aproveitar históricos ou posições de índices órfãos.
if redis.call('EXISTS', KEYS[2], KEYS[3]) > 0 or
   redis.call('SISMEMBER', KEYS[4], ARGV[1]) == 1 or
   redis.call('ZSCORE', KEYS[5], ARGV[1]) or redis.call('ZSCORE', KEYS[6], ARGV[1]) then
    return redis.error_reply('Orphaned job data')
end
-- Validar o contador antes das escritas e preservar scores FIFO inteiros exatos.
local sequence = redis.call('GET', KEYS[7])
if sequence and (not string.match(sequence, '^%d+$') or
   (#sequence > 1 and string.sub(sequence, 1, 1) == '0') or
   #sequence > 16 or tonumber(sequence) > 9007199254740991) then
    return redis.error_reply('Invalid FIFO sequence')
end
if state == 'enqueued' and sequence and tonumber(sequence) >= 9007199254740991 then
    return redis.error_reply('FIFO sequence exhausted')
end
-- Efetivar a criação: dados, histórico e índices ficam visíveis juntos ao concluir.
redis.call('HSET', KEYS[1], 'id', ARGV[1], 'name', ARGV[2], 'args', ARGV[3],
    'state', state, 'attempts', '0', 'maxAttempts', ARGV[5], 'retryDelayMs', ARGV[6],
    'createdAt', ARGV[7], 'updatedAt', ARGV[8], 'creationIdentity', ARGV[12])
if state == 'scheduled' then redis.call('HSET', KEYS[1], 'scheduledAt', ARGV[9]) end
redis.call('RPUSH', KEYS[2], ARGV[11])
redis.call('SADD', KEYS[4], ARGV[1])
if state == 'enqueued' then
    local nextSequence = redis.call('INCR', KEYS[7])
    redis.call('ZADD', KEYS[5], nextSequence, ARGV[1])
else
    redis.call('ZADD', KEYS[6], ARGV[10], ARGV[1])
end
return 1
