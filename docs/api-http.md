# API HTTP e jobs da demo

[Início](../README.md) · [Arquitetura](arquitetura.md)

Este documento apresenta os contratos HTTP, a composição da demo e exemplos de chamadas. Inicie a aplicação conforme o [guia de execução local](execucao-local.md). Datas usam ISO-8601 UTC e IDs são strings opacas.

A demo disponibiliza criação dos três jobs da spec, listagem e detalhe de qualquer contrato persistido, deleção lógica, reenfileiramento manual, eventos e disponibilidade do motor.

A composição no `Program` registra catálogo com handlers, eventos, `IJobClient`, `IJobManager`, `JobServer` e uma conexão Redis compartilhada como singletons, além do adaptador de hosting. Todos usam o mesmo prefixo padrão. O contêiner possui e descarta a conexão depois de parar o servidor; não há conexão por pedido. `PORT` configura a escuta HTTP, `REDIS_CONNECTION` configura o armazenamento e `WORKER_COUNT` configura o único `JobServer` da aplicação. A leitura e validação da quantidade ocorrem na criação desse singleton, resolvido antes de iniciar o host e o processamento, para preservar o diagnóstico de configuração mesmo quando a inicialização falha.

## Endpoints

A demo adapta os contratos da biblioteca aos endpoints obrigatórios:

| Endpoint | Comportamento |
| --- | --- |
| `POST /demo/jobs` | Validar entrada, criar job e retornar `202 { "jobId": "..." }`. |
| `GET /api/jobs` | Retornar `200 { "items": [...] }` com `id`, `name`, `state`, `attempts`. |
| `GET /api/jobs/{id}` | Retornar o job completo, com históricos consistentes. |
| `DELETE /api/jobs/{id}` | Deletar logicamente um job pendente; retornar `200 { "id": "...", "state": "deleted" }`. |
| `POST /api/jobs/{id}/requeue` | Reenfileirar um finalizado; retornar `202 { "jobId": "..." }` com o mesmo ID. |
| `GET /health` | Informar se o servidor está apto a processar. |
| `GET /demo/events` | Retornar eventos da demo em ordem de registro. |

Os campos do detalhe e dos históricos estão descritos no [modelo de dados](persistencia-redis.md#modelo-de-dados). Deleção aceita `scheduled` e `enqueued`; reenfileiramento aceita `succeeded`, `failed` e `deleted`, preserva o ID e os históricos e reinicia o contador de tentativas. As [operações de gerenciamento](uso-biblioteca.md#consultar-e-gerenciar) também estão disponíveis sem HTTP.

## Entrada e validação

A criação aceita `job`, `params` e `options`, conforme a spec. `options` é opcional, assim como seus campos:

- `runAt`: instante futuro ou passado; ausente significa execução imediata.
- `maxAttempts`: inteiro de 1 a 10, padrão 3.
- `retryDelayMs`: inteiro de 0 a 60.000, padrão 1000.

O parser HTTP inspeciona o documento JSON original antes de construir `LogMessageArgs`, `SucceedAfterArgs` ou `FailNTimesArgs` e `JobOptions`, preservando a informação de duplicidades e de ausência versus `null`. Os nomes são sensíveis a maiúsculas e não são normalizados. Campos desconhecidos e duplicados são rejeitados em cada objeto, incluindo nomes equivalentes após decodificar escapes. Objetos aninhados não fazem parte do contrato atual de argumentos.

`job` e `params` são obrigatórios. `params.message` é obrigatório e textual para `log-message`; `params.delayMs` é obrigatório, inteiro de 0 a 60000 para `succeed-after`; `params.failTimes` é obrigatório, inteiro de 0 a 10 para `fail-n-times`. Validadores registrados na biblioteca também conferem as faixas e o comprimento da mensagem. `options` ausente ou `{}` usa os padrões; `options: null` e campos de opção nulos são inválidos. Inteiros exigem representação inteira de 32 bits, sem strings, frações ou expoentes; faixas são validadas pela biblioteca. `runAt` exige data ISO-8601 com `Z` ou offset explícito e é normalizado para UTC.

O endpoint chama a criação tipada da biblioteca e traduz `JobValidationException` para `400`, preservando sua mensagem. Capturas de erros de JSON ficam restritas ao parser; `ArgumentException`, `JsonException` e outras falhas inesperadas de validadores produzem `500`, com mensagem genérica e detalhes apenas nos logs. O middleware aplica o mesmo corpo genérico às falhas de armazenamento na criação e consulta. O detalhe chama diretamente `GetAsync(id)`, sem resolver contratos ou reconstruir DTOs; serializa propriedades e enums em `camelCase`, datas UTC e argumentos como JSON estruturado.

As rotas manuais não exigem corpo nem consultam o detalhe depois da escrita: a resposta confirma a operação persistida, mesmo que o motor já tenha avançado. Traduzem `NotFound`/`Conflict` para `404`/`409`, sem afetar a disponibilidade do motor. Uma falha restrita à requisição propaga ao middleware genérico; não é registrada como falha fatal do executor. A listagem usa a projeção da biblioteca e a mesma serialização camelCase/enums textuais do detalhe.

## Erros e disponibilidade

Os erros HTTP usam `{ "error": "mensagem legível" }`:

| Código | Situação |
| --- | --- |
| `400` | Entrada inválida. |
| `404` | ID inexistente. |
| `409` | Operação incompatível com o estado atual. |
| `500` | Falha inesperada, com mensagem genérica. |

A listagem HTTP retorna todos os jobs, sem filtros ou paginação.

`/health` retorna `200 { "status": "ok" }` somente após as verificações iniciais e com todos os workers operacionais. Durante recuperação não há prontidão; falhas observadas de armazenamento tornam o servidor indisponível. Durante inicialização, indisponibilidade de infraestrutura ou parada, retorna `503 { "status": "unavailable" }` enquanto o HTTP estiver disponível.

Uma aplicação cuja inicialização do servidor de jobs falha não libera sua escuta HTTP. O diagnóstico do motor fica disponível em `JobServer.LastError`.

## Funções e eventos da demo

| Função | Parâmetros obrigatórios | Comportamento |
| --- | --- | --- |
| `succeed-after` | `delayMs`, inteiro de 0 a 60.000 | Esperar e terminar com sucesso. |
| `fail-n-times` | `failTimes`, inteiro de 0 a 10 | Falhar com `demo failure` enquanto `context.Attempt <= failTimes`; depois ter sucesso. |
| `log-message` | `message`, string de até 200 unidades UTF-16 (`string.Length`), inclusive vazia | Registrar a mensagem e terminar com sucesso. |

O contador de `fail-n-times` vem exclusivamente do contexto fornecido pela biblioteca.

`DemoJobHandlers` implementa as três funções com espera assíncrona e erro exato `demo failure`. `DemoEvents` mantém os eventos em memória, com inclusão e snapshot sincronizados para preservar a ordem de registro:

- `started` ao entrar na função.
- `finished` ao sair, com `detail.outcome` igual a `ok` ou `error`.
- `message` para `log-message`, com a mensagem em `detail.message`.

`GET /demo/events` retorna `200 { "items": [...] }` em ordem de registro. Cada evento inclui `type`, `jobId`, `attempt`, `at` UTC e `detail`. Os eventos são independentes do histórico persistido, ficam em memória e são perdidos no reinício. Uma execução interrompida pode deixar `started` sem `finished`.

## Exemplos de uso

### Requests no Bruno

Abra as coleções [.bruno/demo-app](../.bruno/demo-app/opencollection.yml) e [.bruno/backgound-jobs-lib](../.bruno/backgound-jobs-lib/opencollection.yml) no Bruno e selecione o ambiente `Local` em cada uma. Ambas usam `baseUrl = http://localhost:8080`; ajuste essa variável se alterar a porta da demo.

A coleção `demo-app` contém um request para cada tipo de job (`succeed-after`, `fail-n-times` e `log-message`) e a consulta de eventos. Edite os parâmetros e as opções no corpo JSON. Na aba Vars de cada request de criação, `scheduleDelaySeconds` controla quando executar: `0` enfileira imediatamente, `30` agenda para daqui a 30 segundos e um valor negativo demonstra horário passado. O script calcula `runAt` em UTC a cada envio. `delayMs` representa a duração da função `succeed-after`, independentemente do agendamento.

A coleção `backgound-jobs-lib` contém health, listagem, detalhe, deleção e reenfileiramento. A biblioteca é consumida em processo; esses requests usam os endpoints de gerenciamento expostos pela demo. Copie o `jobId` retornado pela criação para a variável `jobId` do ambiente `Local` dessa coleção; também é possível usar um `id` da listagem. As variáveis das duas coleções têm escopos separados.

Para experimentar a deleção, crie um job com `scheduleDelaySeconds = 60` e delete-o antes do horário. Para reenfileirar, use um job em `succeeded`, `failed` ou `deleted`. As regras de estado e as respostas esperadas estão na aba Docs de cada request.

### Requests com curl

Com Redis e a demo em execução, use outro terminal:

```bash
base=http://localhost:8080
curl --fail-with-body -sS "$base/health"
curl --fail-with-body -sS "$base/demo/jobs" \
  -H 'Content-Type: application/json' \
  -d '{"job":"succeed-after","params":{"delayMs":0}}'
# Use o jobId retornado:
curl --fail-with-body -sS "$base/api/jobs/SEU_JOB_ID"
curl --fail-with-body -sS "$base/demo/events"
```

A criação retorna `202 { "jobId": "..." }` após persistência. A consulta seguinte pode encontrar o job em execução ou finalizado. O detalhe inclui histórico `enqueued → processing → succeeded` e tentativa 1 com início, fim, resultado e erro. Para observar `processing` manualmente, use `delayMs: 5000` e consulte durante a espera.

### FIFO e concorrência

Para verificar FIFO, inicie a demo com `WORKER_COUNT=1` e envie mensagens sequencialmente:

```bash
for message in A B C; do
  curl --fail-with-body -sS "$base/demo/jobs" -H 'Content-Type: application/json' \
    -d "{\"job\":\"log-message\",\"params\":{\"message\":\"$message\"}}"
done
curl --fail-with-body -sS "$base/demo/events"
```

Os eventos `message` devem mostrar A/B/C nessa ordem. Após uma parada limpa, inicie com `WORKER_COUNT=2` e crie seis esperas:

```bash
for i in 1 2 3 4 5 6; do
  curl --fail-with-body -sS "$base/demo/jobs" -H 'Content-Type: application/json' \
    -d '{"job":"succeed-after","params":{"delayMs":1000}}'
done
curl --fail-with-body -sS "$base/demo/events"
```

Consulte os seis IDs até `succeeded`, cada um com uma tentativa. Na sequência de eventos desses IDs, incremente um contador em `started` e decremente em `finished`: o pico deve atingir 2 sem ultrapassá-lo, cada ID deve começar uma vez e o saldo final deve ser zero. Sem `WORKER_COUNT`, o mesmo cenário usa quatro workers e atinge pico 4. Os testes HTTP reproduzem essas verificações com os jobs reais e a composição do `Program`.

### Agendamento

Para demonstrar agendamento futuro e passado, gere as datas UTC com Python 3 e consulte os IDs retornados:

```bash
future=$(python3 -c 'from datetime import datetime,timedelta,timezone; print((datetime.now(timezone.utc)+timedelta(seconds=3)).isoformat())')
past=$(python3 -c 'from datetime import datetime,timedelta,timezone; print((datetime.now(timezone.utc)-timedelta(seconds=3)).isoformat())')
for run_at in "$future" "$past"; do
  curl --fail-with-body -sS "$base/demo/jobs" -H 'Content-Type: application/json' \
    -d "{\"job\":\"succeed-after\",\"params\":{\"delayMs\":0},\"options\":{\"runAt\":\"$run_at\"}}"
done
```

O futuro começa somente a partir de `runAt`; o passado fica pronto imediatamente. Com worker livre e sem backlog, ambos devem começar em até dois segundos após elegibilidade. O histórico do futuro começa em `scheduled → enqueued → processing`; fora de `scheduled`, `scheduledAt` é nulo.

### Retry e esgotamento

Para demonstrar retry com sucesso, esgotamento e continuidade:

```bash
curl --fail-with-body -sS "$base/demo/jobs" -H 'Content-Type: application/json' \
  -d '{"job":"fail-n-times","params":{"failTimes":2},"options":{"maxAttempts":3,"retryDelayMs":500}}'
curl --fail-with-body -sS "$base/demo/jobs" -H 'Content-Type: application/json' \
  -d '{"job":"fail-n-times","params":{"failTimes":10},"options":{"maxAttempts":3,"retryDelayMs":500}}'
curl --fail-with-body -sS "$base/demo/jobs" -H 'Content-Type: application/json' \
  -d '{"job":"log-message","params":{"message":"Execução após erro"}}'
```

Consulte os IDs até `succeeded`, `failed` e `succeeded`, respectivamente. Os dois primeiros têm exatamente três tentativas. O primeiro preserva duas falhas com `demo failure` e sucesso na terceira; o segundo preserva três falhas. Compare `attemptHistory[i].startedAt` com `attemptHistory[i-1].finishedAt`: a diferença deve ser de pelo menos 500 ms. Cada retry passa por `scheduled → enqueued` e entra no fim da fila; atraso zero também preserva essas transições, que podem ser rápidas demais para observar por polling.

### Gerenciamento

Para gerenciar jobs pela demo:

```bash
curl --fail-with-body -sS "$base/api/jobs"
# Use o ID de um job scheduled ou enqueued:
curl --fail-with-body -sS -X DELETE "$base/api/jobs/SEU_JOB_ID"
# Use o ID de um job succeeded, failed ou deleted:
curl --fail-with-body -sS -X POST "$base/api/jobs/SEU_JOB_ID/requeue"
```

A listagem retorna `200 { "items": [{ "id": "...", "name": "log-message", "state": "deleted", "attempts": 0 }] }`, incluindo `deleted`. Deleção retorna `200 { "id": "...", "state": "deleted" }`; reenfileiramento retorna `202 { "jobId": "..." }` com o mesmo ID. As mutações não exigem corpo. A resposta confirma a operação aplicada; o motor pode avançar o estado antes da próxima consulta. Uma segunda deleção de `deleted` retorna `409`; requeue de pendente ou `processing` também retorna `409`. Erros mantêm `{ "error": "mensagem legível" }`.

## Documentação relacionada

- [Especificação funcional](spec-base.md#4-aplicação-demonstrativa-e-http): requisitos da API.
- [Uso da biblioteca](uso-biblioteca.md): operações equivalentes sem HTTP.
- [Processamento](processamento.md): garantias de execução e prontidão.
- [Testes e validação](testes-e-validacao.md): regressões e evidências de aceite.
