--[[
Operação atômica: ler um snapshot do job, histórico de estados e tentativas.
As três estruturas são lidas sem intercalamento de escritas, evitando combinar
estado e contador de um momento com históricos de outro. Nenhuma chave é alterada.

Entradas: KEYS[1..3] = hash do job, lista de histórico, lista de tentativas.
Não recebe ARGV, contratos ou handlers.

Retorno: {} quando hash e listas locais estão ausentes; caso exista hash, retorna
{HGETALL(job), LRANGE(history, 0, -1), LRANGE(attempts, 0, -1)}.
Uma lista de tentativas ausente representa uma lista vazia.

Tipos incorretos ou listas locais sem hash geram erro Redis, distinguindo resíduos
de inexistência. A materialização em C# valida campos e conteúdo do snapshot,
inclusive histórico inicial obrigatório; um retorno com hash não prova validade.
]]

-- Rejeitar tipos incompatíveis antes de montar o snapshot.
local types = {'hash', 'list', 'list'}
for i = 1, 3 do
    local kind = redis.call('TYPE', KEYS[i]).ok
    if kind ~= 'none' and kind ~= types[i] then
        return redis.error_reply('Invalid storage key type at position ' .. i)
    end
end
-- Ausência só é válida se também não houver resíduos nas listas locais.
if redis.call('EXISTS', KEYS[1]) == 0 then
    if redis.call('EXISTS', KEYS[2], KEYS[3]) > 0 then
        return redis.error_reply('Orphaned job history')
    end
    return {}
end
-- Obter as três estruturas na mesma execução, preservando a ordem das listas.
return {redis.call('HGETALL', KEYS[1]), redis.call('LRANGE', KEYS[2], 0, -1),
    redis.call('LRANGE', KEYS[3], 0, -1)}
