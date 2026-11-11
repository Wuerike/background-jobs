# BackgroundJobs

Biblioteca em .NET 10, baseada em [docs/spec-base.md](docs/spec-base.md).

- `BackgroundJobs.slnx`: solução com biblioteca, demo, testes unitários e de integração.
- `src/BackgroundJobs`: registro tipado, criação, consulta e gerenciamento, agendamento, retries e execução com workers configuráveis, persistência de tentativas e ciclo do servidor.
- `src/BackgroundJobs.Demo`: aplicação ASP.NET Core com os três jobs da spec, criação, listagem, detalhe, deleção, reenfileiramento, eventos e health.
- `tests/BackgroundJobs.UnitTests`: registro, preparação, modelos e orquestração, sem Redis.
- `tests/BackgroundJobs.IntegrationTests`: criação atômica, consulta consistente, índices e concorrência com Redis real.
- `tests/BackgroundJobs.HttpTests`: contratos HTTP, validação de entrada e integração com Redis real.

A biblioteca cria, consulta, agenda e executa jobs com quantidade configurável de workers, padrão 4. Falhas com orçamento restante voltam à fila após o atraso fixo contado do fim da tentativa; sucesso ou esgotamento encerram o job. Estados, histórico e tentativas são persistentes. Listagem, deleção lógica de pendentes e reenfileiramento manual de finalizados estão disponíveis pela biblioteca e HTTP. Antes da prontidão, a recuperação fecha tentativas abertas como `interrupted`: reenfileira imediatamente quando resta orçamento ou finaliza em `failed` quando o limite foi atingido. A tentativa interrompida conta no limite; funções podem repetir efeitos externos após uma queda (at-least-once sujeito ao orçamento).

## Registro de contratos

O registro é configurado antes de usar a biblioteca e permanece estável durante o processamento. Um produtor precisa somente do contrato e de sua validação:

```csharp
using BackgroundJobs;
using BackgroundJobs.Demo.Jobs;

var registry = new JobRegistry();
registry.Contracts.Add(DemoJobs.LogMessage, DemoJobValidation.ValidateLogMessage);
```

A demo fornece `LogMessageArgs(string Message)`. A mensagem é obrigatória e aceita até 200 unidades UTF-16 (`string.Length`), incluindo string vazia; espaços e conteúdo Unicode bem formado são preservados. A biblioteca rejeita strings com surrogates sem par usando `JobValidationException`, inclusive em valores aninhados, chaves de dicionários e nomes e valores textuais de `JsonNode` (`JsonObject`, `JsonArray` e `JsonValue`), também nos dicionários de dados de extensão com `[JsonExtensionData]`, antes de escrever o texto no JSON. Os argumentos e validadores de outros jobs pertencem à aplicação consumidora.

Para associar uma implementação a um contrato conhecido, use `registry.Handlers.Register(job, handler)`. Para adicionar contrato novo e handler juntos, use `registry.Register(job, handler, validate)`. O handler tem assinatura `Func<TArgs, JobContext, Task>`; cadastro e preparação não o executam. Nomes são sensíveis a maiúsculas e cadastros duplicados são rejeitados.

O validador opcional é um `Action<TArgs>`: retorno normal aceita os argumentos; `JobValidationException` declara rejeição com mensagem legível. Outras exceções propagam sem conversão. As opções aceitam `MaxAttempts` de 1 a 10 (padrão 3), `RetryDelayMs` de 0 a 60000 (padrão 1000) e `RunAt` opcional. A preparação interna reconstrói e valida os argumentos, produz JSON independente dos objetos do chamador e usa uma leitura de `TimeProvider` para determinar estado e metadados. Ela pode ser verificada pelos testes unitários.

## Criar, executar e consultar sem HTTP

Com Redis iniciado, uma aplicação consumidora pode usar as interfaces públicas:

```csharp
using BackgroundJobs;
using BackgroundJobs.Demo.Jobs;
using StackExchange.Redis;

var registry = new JobRegistry();
registry.Contracts.Add(DemoJobs.LogMessage, DemoJobValidation.ValidateLogMessage);
registry.Handlers.Register(DemoJobs.LogMessage, (args, context) =>
{
    Console.WriteLine(args.Message);
    return Task.CompletedTask;
});

using var connection = await ConnectionMultiplexer.ConnectAsync("127.0.0.1:6379");
const string prefix = "library-example";
IJobClient client = new RedisJobClient(registry.Contracts, connection, prefix);
IJobManager manager = new RedisJobManager(connection, prefix);
var id = await client.EnqueueAsync(DemoJobs.LogMessage, new LogMessageArgs("Olá"),
    new JobOptions { RunAt = DateTimeOffset.UtcNow.AddSeconds(1), MaxAttempts = 3, RetryDelayMs = 500 });
await using var server = new JobServer(registry, connection, prefix, workerCount: 1);
await server.StartAsync();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
JobDetails? detail;
do
{
    detail = await manager.GetAsync(id);
    if (detail!.State == JobState.Succeeded) break;
    await Task.Delay(20, timeout.Token);
} while (true);
Console.WriteLine($"{detail.Id}: {detail.State}, tentativa {detail.Attempts}");
await server.StopAsync();
```

O exemplo usa o contrato da demo, mas o consumidor pode definir seus próprios tipos e validadores. Um produtor que apenas cadastra `Contracts.Add` pode criar e consultar sem handler; ele não deve iniciar um executor com referências sem implementação. `GetAsync` não recebe registro e devolve `null` exclusivamente para ID inexistente; erros Redis e dados inválidos propagam como falhas.

`IJobManager` também funciona sem catálogo, handlers ou motor iniciado:

```csharp
IReadOnlyList<JobSummary> items = await manager.ListAsync();
JobOperationResult deletion = await manager.DeleteAsync(id);
JobOperationResult requeue = await manager.RequeueAsync(id);
```

`ListAsync` inclui todos os estados, sem ordem definida ou paginação pública. Cada resumo (`Id`, `Name`, `State`, `Attempts`) é lido atomicamente; a coleção não representa um único instante global durante mutações concorrentes. Campos inválidos ou um ID indexado sem registro geram erro, sem retornar lista parcial.

As mutações retornam `Applied`, `NotFound` ou `Conflict` somente após a decisão persistente. Deletar aceita `Scheduled`/`Enqueued`, mantém o detalhe consultável e impede execução até um reenfileiramento posterior. Reenfileirar aceita `Succeeded`/`Failed`/`Deleted`, mantém ID, contrato, JSON, opções e criação, zera o contador e coloca o job no fim da fila, sem reaplicar o agendamento original. A primeira tentativa de cada ciclo é 1; todos os históricos anteriores permanecem. IDs são opacos, sem normalização; nulo é erro de argumento. Falhas de infraestrutura são exceções, inclusive quando uma confirmação não chega: não se deve interpretar o erro como prova de ausência de efeito nem repetir automaticamente uma mutação incerta.

A conexão é criada, compartilhada e descartada pelo chamador; os serviços não a descartam nem abrem uma conexão por pedido. Cliente, gerente e servidor aceitam `prefix`, com padrão `background-jobs`; use o mesmo em todos os componentes. Prefixos diferentes isolam armazenamentos lógicos; o exemplo usa `library-example` para isolar os dados da demo. São aceitas apenas letras ASCII, dígitos, ponto, hífen e sublinhado, sem valor vazio. Cliente, gerente e servidor também aceitam `timeProvider`, com padrão `TimeProvider.System`. O servidor aceita `workerCount` como último argumento, com padrão 4 e mínimo 1; a quantidade fica fixa durante a vida da instância. Use `workerCount: 1` para FIFO pela sequência persistida de entrada na fila. Com vários workers, não há garantia da ordem exata de início ou término. A reserva Lua garante exclusividade por job; a quantidade de loops limita as funções simultâneas. Handlers e validadores podem executar simultaneamente e devem sincronizar seu estado compartilhado.

O ID retorna somente após confirmação da criação. A escrita inclui dados, histórico e índices na mesma operação. Jobs futuros ficam `Scheduled`, com data exata preservada, e o scheduler da biblioteca os promove à fila quando elegíveis, sem antecipar a data. A mesma promoção atende retries, atribuindo nova posição FIFO e preservando tentativas. O scheduler usa páginas de até 128 candidatos e polling ocioso de 100 ms, independente dos workers. Não há loop próprio de retry/reconciliação de falhas de rede; uma falha de confirmação não deve ser interpretada como prova de ausência do pedido. Chamadas públicas independentes geram IDs distintos.

`JobServer` verifica handlers de todos os jobs armazenados, inclusive finalizados, antes de liberar os workers. Resíduos válidos de `processing` são recuperados antes da prontidão; corrupção mantém o servidor indisponível. `IsAvailable` informa disponibilidade e `LastError` expõe erro fatal. Erros de desserialização, validação ou função fecham a tentativa e não impedem outros jobs; falhas Redis nos workers ou no scheduler fecham a admissão de reservas e promoções e cancelam suas esperas ociosas, sem reinvocar funções. O primeiro diagnóstico é preservado; reservas já admitidas continuam sendo aguardadas e, quando confirmadas, executam e tentam persistir o resultado.

`StartAsync` inicia uma instância uma única vez e compartilha a inicialização entre chamadas repetidas. `StopAsync` fecha novas recuperações, promoções e reservas e aguarda inicialização, scheduler e operações admitidas, funções e confirmações; não cancela handlers. A parada é idempotente. Para outro ciclo de serviço, crie uma nova instância. `JobServerHostedService`, em `BackgroundJobs.Hosting`, oferece integração `IHostedService`; o host deve permitir espera ilimitada na parada, como na composição da demo.

## Redis local

O storage escolhido para os jobs é Redis. O `compose.yaml` usa a imagem oficial
`redis:8.2.10-alpine`, com volume persistente em `/data`, AOF habilitado e
`appendfsync always` para sincronizar as escritas em disco antes de responder.
A política `noeviction` impede a remoção automática de chaves por pressão de memória.
Referência: [persistência do Redis](https://redis.io/docs/latest/operate/oss_and_stack/management/persistence/).

Para dados existentes em volume nomeado, execute primeiro o [procedimento de preservação](#preservar-um-volume-nomeado-existente). Na raiz do projeto, com Docker e Docker Compose disponíveis, inicie o serviço:

```bash
mkdir -p ./data
docker compose up -d --wait redis
docker compose exec redis redis-cli ping
```

O segundo comando deve retornar `PONG`. O serviço fica disponível apenas em
`127.0.0.1:6379`, sem senha, para desenvolvimento local. A aplicação .NET executada
no host deverá usar a connection string `127.0.0.1:6379`. A demo usa `REDIS_CONNECTION` para acessar esse armazenamento; o padrão é `127.0.0.1:6379`.

Se a porta já estiver ocupada, escolha outra ao iniciar:

```bash
REDIS_PORT=6380 docker compose up -d --wait redis
```

Nesse caso, a connection string será `127.0.0.1:6380`.

Para consultar logs e parar o serviço preservando os dados:

```bash
docker compose logs -f redis
docker compose down
```

`DATA_DIR` configura uma montagem bind para `/data`; ausente ou vazia usa `./data`, relativo ao diretório do arquivo Compose. O diretório deve existir e permitir escrita pelo usuário Redis do container; não aplique permissões amplas. O entrypoint da imagem oficial ajusta o proprietário quando iniciado como root; em ambientes que impedem esse ajuste, prepare o proprietário antes de iniciar. `/data/` na raiz do repositório é ignorado pelo Git. Para outro caminho, inclusive com espaços:

```bash
mkdir -p "$PWD/dados locais"
DATA_DIR="$PWD/dados locais" REDIS_PORT=6380 docker compose up -d --wait redis
DATA_DIR="$PWD/dados locais" REDIS_PORT=6380 docker compose config
```

Use os mesmos valores de diretório e porta em cada comando Compose. `docker compose down` e a recriação do container preservam os arquivos no host. Um diretório vazio diferente representa outro armazenamento; não execute dois Redis no mesmo diretório. Exportar `DATA_DIR` somente para o processo .NET não move os dados de um Redis externo. Nesse caso, configure o diretório e AOF no próprio servidor e mantenha a mesma `REDIS_CONNECTION` no reinício.

Para conferir o padrão e a montagem efetiva:

```bash
mkdir -p ./data
docker compose config
docker compose up -d --wait redis
docker inspect "$(docker compose ps -q redis)" --format '{{json .Mounts}}'
docker compose exec redis redis-cli CONFIG GET appendonly appendfsync maxmemory-policy dir
```

### Preservar um volume nomeado existente

Registre IDs e detalhes dos jobs para comparar depois. Antes de iniciar o bind sobre dados existentes, pare todos os executores/produtores e o container Redis. Identifique o volume do projeto correto pela etiqueta e confira seus containers; não escolha um volume apenas pelo nome sugerido:

```bash
docker volume ls --filter label=com.docker.compose.volume=redis-data
# Preencha com o nome efetivo conferido na saída e no inspect:
SOURCE_VOLUME=nome_do_volume_existente
docker volume inspect "$SOURCE_VOLUME"
docker ps -a --filter volume="$SOURCE_VOLUME"
docker compose stop redis
```

Confirme que nenhum container usando esse volume permanece ativo. Copie todo o conteúdo persistido, incluindo subdiretórios, manifesto e segmentos do AOF multipartes, para um destino **vazio**; mantenha a origem:

```bash
mkdir -p "$PWD/dados preservados"
export DATA_DIR="$PWD/dados preservados"
docker volume inspect "$SOURCE_VOLUME" >/dev/null &&
docker run --rm --entrypoint sh \
  --mount "type=volume,source=$SOURCE_VOLUME,target=/source,readonly" \
  --mount "type=bind,source=$DATA_DIR,target=/destination" \
  redis:8.2.10-alpine -c 'test -z "$(ls -A /destination)" && cp -a /source/. /destination/'
docker compose up -d --wait redis
```

A cópia preserva permissões e recusa mesclar dados. Confira montagem, configuração, IDs, argumentos, opções, tentativas e históricos pela API antes de liberar escritores; execute um novo job de controle. Mantenha o volume original até verificar a leitura e execução pelo destino. Não use `down -v`, não exclua a origem e não copie apenas um suposto `appendonly.aof`. Para retorno, pare os escritores e o Redis do bind e monte o volume original em `/data` num Redis com a mesma imagem e opções AOF; não execute simultaneamente origem e destino. A origem conserva somente o estado no instante da cópia; escritas feitas depois no destino precisam ser preservadas antes de qualquer retorno.

Com escritores parados, uma forma de iniciar a origem preservada é:

```bash
docker compose down
docker volume inspect "$SOURCE_VOLUME" >/dev/null &&
docker run -d --name background-jobs-redis-retorno \
  -p "127.0.0.1:${REDIS_PORT:-6379}:6379" \
  --mount "type=volume,source=$SOURCE_VOLUME,target=/data" \
  redis:8.2.10-alpine redis-server --appendonly yes --appendfsync always \
  --save "" --maxmemory-policy noeviction
```

Consulte os IDs e históricos esperados antes de voltar a liberar escritores. O diretório do bind continua preservado.

A montagem e a transferência foram verificadas com Docker Desktop, Redis 8.2.10 e dados descartáveis. Confirme a conexão com o daemon em `docker version` antes de executar os comandos; no WSL, o CLI Linux requer integração com a distribuição. A validação de armazenamento pelo CLI do Windows usa caminhos Windows para o arquivo Compose e `DATA_DIR`.

## SDK no WSL

O projeto requer o SDK .NET 10. Para usar o SDK instalado no sistema Ubuntu 24.04
do WSL, configure o repositório de pacotes da Microsoft e instale o SDK:

```bash
sudo apt-get update
sudo apt-get install -y ca-certificates wget
wget https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
sudo dpkg -i /tmp/packages-microsoft-prod.deb
rm /tmp/packages-microsoft-prod.deb
sudo apt-get update
sudo apt-get install -y dotnet-sdk-10.0
dotnet --version
```

O último comando deve mostrar uma versão `10.0.x`. A instalação fica no WSL e
não requer manter o SDK dentro do repositório. Para conferir todos os SDKs
instalados, use `dotnet --list-sdks`.

## Compilar e executar

```bash
dotnet build BackgroundJobs.slnx
dotnet run --project src/BackgroundJobs.Demo --no-launch-profile
```

A demo escuta na porta definida por `PORT` (padrão `8080`) e usa `REDIS_CONNECTION` (padrão `127.0.0.1:6379`). Para uma porta alternativa do Redis e da demo:

```bash
PORT=8081 REDIS_CONNECTION=127.0.0.1:6380 dotnet run --project src/BackgroundJobs.Demo --no-launch-profile
```

A demo usa `WORKER_COUNT`, com padrão **4**. Um valor fornecido precisa ser inteiro positivo de 32 bits; vazio, formato inválido, zero, negativo ou overflow impedem a inicialização com mensagem que identifica a configuração. Para FIFO, inicie com um worker; para observar concorrência, use dois:

```bash
WORKER_COUNT=1 dotnet run --project src/BackgroundJobs.Demo --no-launch-profile
# Após parar a aplicação, iniciar com dois workers:
WORKER_COUNT=2 dotnet run --project src/BackgroundJobs.Demo --no-launch-profile
```

Use apenas um executor por armazenamento. `DATA_DIR` configura o diretório do Redis no Compose; `REDIS_CONNECTION` aponta a biblioteca e a demo para esse serviço.

Os diretórios de saída são centralizados na pasta `build/` da raiz por `Directory.Build.props`, usando o [layout de artefatos do SDK](https://learn.microsoft.com/pt-br/dotnet/core/sdk/artifacts-output):

- `build/bin/<projeto>/<configuração>/`: binários compilados.
- `build/obj/<projeto>/<configuração>/`: arquivos intermediários; os arquivos de restore ficam em `build/obj/<projeto>/`.

A pasta `build/` é ignorada pelo Git.


## Demonstrar execução HTTP

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

Os contratos disponíveis são:

| `job` | `params` obrigatórios |
| --- | --- |
| `succeed-after` | `{"delayMs":0}`; inteiro de 0 a 60000. |
| `fail-n-times` | `{"failTimes":1}`; inteiro de 0 a 10. Falha com `demo failure` enquanto `context.Attempt <= failTimes`. |
| `log-message` | `{"message":"Olá"}`; texto de até 200 unidades UTF-16. |

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

`GET /demo/events` retorna `{ "items": [...] }`, em ordem de registro, com `type`, `jobId`, `attempt`, `at` UTC e `detail`. Os handlers registram `started`, `finished` com `detail.outcome` igual a `ok` ou `error` e, para `log-message`, um evento `message`. São eventos em memória, independentes do histórico persistente, perdidos no reinício.

`GET /health` retorna `200 { "status": "ok" }` com o motor iniciado e operacional, ou `503 { "status": "unavailable" }` durante indisponibilidade/parada enquanto o HTTP estiver acessível. Erro fatal do motor fica disponível em `JobServer.LastError`. Uma aplicação ASP.NET Core cuja inicialização do servidor falha não libera sua escuta HTTP.

A entrada aceita somente `job`, `params` e `options`, com nomes sensíveis a maiúsculas, sem campos desconhecidos ou duplicados. `options` ausente ou `{}` usa os padrões 3/1000; `options: null` e campos de opção nulos são inválidos. Inteiros não aceitam strings, frações ou notação exponencial; `runAt` exige ISO-8601 com `Z` ou offset explícito. As garantias de Unicode do registro também se aplicam à criação HTTP.

Entrada inválida retorna `400`, ID inexistente retorna `404` e operação incompatível com o estado atual retorna `409`, com `{ "error": "mensagem legível" }`. Falhas inesperadas retornam `500` com mensagem genérica e detalhes nos logs.

Para gerenciar jobs pela demo:

```bash
curl --fail-with-body -sS "$base/api/jobs"
# Use o ID de um job scheduled ou enqueued:
curl --fail-with-body -sS -X DELETE "$base/api/jobs/SEU_JOB_ID"
# Use o ID de um job succeeded, failed ou deleted:
curl --fail-with-body -sS -X POST "$base/api/jobs/SEU_JOB_ID/requeue"
```

A listagem retorna `200 { "items": [{ "id": "...", "name": "log-message", "state": "deleted", "attempts": 0 }] }`, incluindo `deleted`. Deleção retorna `200 { "id": "...", "state": "deleted" }`; reenfileiramento retorna `202 { "jobId": "..." }` com o mesmo ID. As mutações não exigem corpo. A resposta confirma a operação aplicada; o motor pode avançar o estado antes da próxima consulta. Uma segunda deleção de `deleted` retorna `409`; requeue de pendente ou `processing` também retorna `409`. Erros mantêm `{ "error": "mensagem legível" }`.

## Validar

A suíte completa requer Linux/WSL, Redis acessível por `REDIS_CONNECTION` e um executável `redis-server` para os testes de processos dedicados. Configure `REDIS_SERVER_EXECUTABLE` quando esse executável estiver fora do PATH. A seção de testes de processos abaixo descreve o comando separado e a opção de executar os contratos HTTP em outras plataformas.

```bash
dotnet restore BackgroundJobs.slnx
dotnet build BackgroundJobs.slnx
dotnet test BackgroundJobs.slnx --no-build
```

As suítes de integração e HTTP exigem Redis real disponível; inicie-o com `docker compose up -d --wait redis`. A integração falha explicitamente se não conseguir conectar, sem ignorar testes.

```bash
# Somente unitários, sem Redis
dotnet test tests/BackgroundJobs.UnitTests --no-build

# Integração e HTTP com porta alternativa
REDIS_CONNECTION=127.0.0.1:6380 dotnet test tests/BackgroundJobs.IntegrationTests --no-build
REDIS_CONNECTION=127.0.0.1:6380 dotnet test tests/BackgroundJobs.HttpTests --no-build
```

`REDIS_CONNECTION` usa `127.0.0.1:6379` por padrão e deve corresponder à porta escolhida em `REDIS_PORT` no Compose. Cada teste usa prefixo exclusivo e limpa somente suas chaves, sem `FLUSHDB` ou mudanças na configuração do servidor. Um Redis externo equivalente também pode ser usado.

A cobertura está nos [testes unitários](tests/BackgroundJobs.UnitTests), nos [testes de persistência e processamento](tests/BackgroundJobs.IntegrationTests) e nos [testes HTTP](tests/BackgroundJobs.HttpTests). Eles verificam contratos e validação, reserva exclusiva, conclusão atômica e idempotente, índices, snapshots consistentes, isolamento de erros e parada coordenada. O aceite HTTP cobre sucesso imediato com históricos e eventos, início em até 2 segundos com worker livre, falha terminal, continuidade, agendamento futuro/passado e retry até sucesso ou esgotamento. A execução pela biblioteca também é verificada sem HTTP.

Os testes HTTP de criação/consulta isolam o motor na composição de teste. Os testes de execução usam o motor ativo; os de configuração preservam a criação real do `JobServer` pelo `Program`, isolando configuração por host e chaves pela conexão de teste. Todos param o servidor antes da limpeza. Não há flag de produto para desativar o motor.

O resultado verificado é build sem avisos ou erros e **653 testes aprovados** (208 unitários, 292 de integração e 153 HTTP, incluindo oito de processos), sem falhas ou testes ignorados, com SDK 10.0.114 e Redis 8.2.10 (porta 16379 para testes de prefixos e portas isoladas nos testes de processos), AOF, `appendfsync always` e `noeviction`. A cobertura inclui limite e progresso dos workers, disputa pela mesma cabeça, FIFO com timestamps iguais, isolamento de erros de função, falha fatal global, espera por reservas/conclusões pendentes, inicialização e parada coordenadas. A integração verifica índices, snapshots e execução dos pendentes por nova instância após parada limpa.

A demonstração em processos reais usou um Redis dedicado vazio por cenário, com as mesmas configurações de persistência e portas isoladas:

| Configuração | Pedidos demonstrados | Pico observado | Eventos | Tentativas por job |
| --- | --- | --- | --- | --- |
| `WORKER_COUNT=1` | Mensagens A/B/C, nessa ordem | 1 | 9 | 1 |
| `WORKER_COUNT=2` | Seis esperas de 750 ms | 2 | 12 | 1 |
| Ausente (padrão 4) | Seis esperas de 750 ms | 4 | 12 | 1 |

Cada cenário confirmou detalhes `succeeded`, histórico completo e ausência de invocação duplicada. Pedidos adicionais com espera zero concluíram em **100,76 ms**, **93,20 ms** e **47,25 ms**, respectivamente, medidos desde o início da criação até a consulta final. O pico é calculado pela ordem de registro dos eventos, sem ordenar timestamps. Esses tempos são observações locais, sem garantia de tempo real.

A demonstração de agendamento e retry também usou processos reais da demo, um worker e Redis dedicado vazio por cenário, com AOF, `appendfsync always` e `noeviction`:

| Comportamento | Resultado observado |
| --- | --- |
| Agendamento três segundos no futuro | Início 25,49 ms após `runAt`, sem antecipação; `succeeded` com uma tentativa. |
| Horário passado | Criação até consulta terminal em 119,88 ms, com uma tentativa. Essa janela inclui o início. |
| Duas falhas e sucesso | Três tentativas; intervalos persistidos de 617,33 e 613,09 ms para atraso de 500 ms. |
| Esgotamento junto de mensagem saudável | Três falhas; intervalos de 620,12 e 610,97 ms; mensagem `succeeded` normalmente. |

Os eventos correlacionados por ID e tentativa confirmaram entrada e término, sem invocações duplicadas. Os detalhes preservaram argumentos, opções, erros e históricos completos, com `scheduledAt` nulo nos terminais. São medições locais, sem garantia de tempo real. As regressões duráveis estão em [SchedulingTests](tests/BackgroundJobs.IntegrationTests/SchedulingTests.cs), [SchedulerTests](tests/BackgroundJobs.UnitTests/SchedulerTests.cs) e [SchedulingHttpTests](tests/BackgroundJobs.HttpTests/SchedulingHttpTests.cs). Elas incluem precisão de ticks e extremos de datas, promoção limitada com múltiplos lotes, FIFO de retries, orçamento, exclusividade e limite de workers, corrupção sem escritas parciais, candidatos de gerações antigas, parada/falha durante seleção e confirmação e continuidade após parada limpa.

O critério de operações manuais foi demonstrado com processos reais da demo e Redis dedicado vazio, um worker, AOF, `appendfsync always` e `noeviction`:

| Comportamento | Resultado observado |
| --- | --- |
| Deletar mensagem agendada três segundos no futuro | `200`, detalhe/listagem em `deleted`, nenhum evento até atravessar o horário original com margem de 250 ms. |
| Reenfileirar `fail-n-times` após sucesso | Mesmo ID, tentativas `[1, 2, 1, 2]`, prefixos dos históricos idênticos e erros preservados; intervalos de retry de 612,54 e 611,78 ms para atraso de 500 ms. |
| Reenfileirar `deleted` e `failed` | Execução do deletado com tentativa 1; falha terminal preservada e nova tentativa 1 no segundo ciclo do job esgotado. |
| Estados incompatíveis e ausência | `409` para nova deleção de `deleted`, deleção de finalizado/ativo e reenfileiramento de pendentes/ativo; `404` nas duas mutações para ID desconhecido. |
| Listagem estabilizada e continuidade | Cinco resumos coerentes com os detalhes, 20 eventos correlacionados e health operacional. |

São observações locais, sem garantia de tempo real. Para reproduzir, crie uma mensagem com `runAt` três segundos à frente, delete-a e consulte eventos depois desse horário; em seguida execute `fail-n-times` com `failTimes: 1`, `maxAttempts: 2`, `retryDelayMs: 500`, capture o detalhe final, reenfileire o mesmo ID e compare os prefixos de `history`/`attemptHistory`. As regressões duráveis estão em [ManagementTests](tests/BackgroundJobs.IntegrationTests/ManagementTests.cs), [ManagementConcurrencyTests](tests/BackgroundJobs.IntegrationTests/ManagementConcurrencyTests.cs), [ManagerTests](tests/BackgroundJobs.UnitTests/ManagerTests.cs) e [ManagementHttpTests](tests/BackgroundJobs.HttpTests/ManagementHttpTests.cs). Elas cobrem todos os estados, listagem paginada internamente e deduplicada, confirmação incerta sem repetição, corrupção sem escrita parcial, ciclos de 64 bits, limites FIFO, snapshots, disputas controladas com reserva/promoção e progresso após descarte de uma página inteira de agendamentos.

A validação usa Redis 8.2.10. Os testes de sinais executam processos nativos Linux/WSL; na verificação, o executável e suas bibliotecas vieram da imagem oficial `redis:8.2.10-alpine`. Para reprodução, forneça um executável nativo pelo PATH ou por `REDIS_SERVER_EXECUTABLE`. O armazenamento dos testes de prefixos pode usar o Compose ou Redis externo equivalente, configurado por `REDIS_CONNECTION`.

Recuperação automática e configuração `DATA_DIR` estão implementadas. A inicialização verifica todos os handlers, inclusive de finalizados, e a associação dos índices antes de recuperar resíduos em lotes de até 128. Cada recuperação preserva dados/ciclo/históricos, fecha a última tentativa e invalida a confirmação antiga. Corrupção e relógio anterior à atualização falham explicitamente; uma falha mantém o motor indisponível e preserva `LastError`. Recuperações já confirmadas permanecem e uma nova instância retoma as restantes. Não há reconciliação ou retomada automática após falha de confirmação.

Na parada, `SIGTERM` e `SIGINT` fecham a admissão, aguardam funções e persistência sem cancelá-las; pendentes permanecem para outra instância. O host da demo tem espera ilimitada. `SIGKILL` encerra imediatamente; no próximo início, uma função interrompida pode executar novamente. Eventos em memória pertencem apenas à instância que os registrou; a recuperação não sintetiza `finished`.

Os critérios 8–10 foram demonstrados por processos reais em Linux/WSL, com Redis 8.2.10 dedicado por cenário, AOF, `appendfsync always` e `noeviction`:

| Comportamento | Resultado observado |
| --- | --- |
| SIGKILL da demo e reinício de Redis normal/forçado | Jobs finais, deletado, ciclo manual, argumentos, opções e históricos idênticos; agendado distante preservado. Vencido concluiu 126,98 ms e 105,50 ms após prontidão, respectivamente. |
| SIGKILL durante função com três tentativas disponíveis | Tentativa 1 `interrupted`, tentativa 2 executada e sucesso; sem passagem por `scheduled` nem espera dos 60 s de retry configurado. |
| SIGKILL durante função com `maxAttempts=1` | `failed` com uma tentativa `interrupted`, sem novos eventos; reenfileiramento manual preservou o registro e executou nova tentativa 1. |
| SIGTERM com uma função de 35 s e pendente | Drenagem em 35,02 s, saída 0; ativa terminou em sucesso, pendente permaneceu com zero tentativas e executou após reinício. |
| SIGINT com um worker / SIGTERM com dois workers | Drenagens de 4,02 s / 4,04 s, saída 0, todas as funções ativas confirmadas e pendente preservado. |
| Diretórios nativos isolados e cópia AOF multipartes | Outro diretório vazio não revelou os dados; cópia integral de manifesto/base/incremental reiniciou com o mesmo histórico, aceitou novo job e preservou os arquivos de origem. |
| Bind Compose padrão e alternativo com espaços | Montagens em `/data`, porta e configuração conferidas no container; recriação preservou os dados, outro diretório começou vazio e o retorno ao primeiro preservou seus quatro jobs. |
| Transferência de volume nomeado para bind | Cópia integral com `cp -a`, UID/GID e modos preservados (diretório AOF 0700, arquivos 0600), manifesto presente e hashes de todos os arquivos iguais antes de iniciar o destino; mesmos IDs, argumentos, opções, estados e históricos após reiniciar. Novo job executou no destino; retorno à origem confirmou seu conjunto exato de quatro IDs, sem incluir a escrita feita no destino. |

São observações locais, sem garantia de tempo real. A regressão permanente está em [ProcessLifecycleTests](tests/BackgroundJobs.HttpTests/ProcessLifecycleTests.cs), com processos controlados por [ProcessHost](tests/BackgroundJobs.HttpTests/ProcessHost.cs). [RecoveryTests de integração](tests/BackgroundJobs.IntegrationTests/RecoveryTests.cs) verificam ambos os orçamentos, FIFO, múltiplos ciclos, 270 resíduos em lotes limitados, deduplicação, metadados/índices corrompidos sem escrita parcial, tempo/sequência/ciclo extremos, confirmação antiga, recuperação parcial e resposta perdida. [RecoveryTests unitários](tests/BackgroundJobs.UnitTests/RecoveryTests.cs) verificam checagens completas de handlers, ordem de inicialização, prontidão, falhas e parada com operações admitidas.

Os testes de processos requerem Linux/WSL, .NET e um executável Redis com AOF (8.2.10 usado na validação). Criam processos, diretórios e portas exclusivos e nunca reiniciam o serviço indicado por `REDIS_CONNECTION`. Configure o caminho quando `redis-server` não estiver no PATH:

```bash
REDIS_SERVER_EXECUTABLE=/caminho/redis-server \
  dotnet test tests/BackgroundJobs.HttpTests --no-build --filter Category=ProcessLifecycle
# Contratos HTTP sem os testes de sinais/processos:
REDIS_CONNECTION=127.0.0.1:6380 \
  dotnet test tests/BackgroundJobs.HttpTests --no-build --filter 'Category!=ProcessLifecycle'
```

A suíte completa também executa os testes de processos; ausência do executável ou plataforma incompatível produz erro explícito, sem ignorar aceite. A configuração e a execução de montagem, recriação e transferência foram verificadas com Docker Desktop 4.73.0, Engine 29.4.3 e Compose v5.1.3 pelo CLI do Windows. `DATA_DIR` padrão e alternativo com espaços, AOF multipartes, isolamento e retorno à origem foram comprovados em recursos descartáveis. As provas de sinais e de queda durante funções usam processos Linux/WSL. Os critérios 1–10 da spec estão verificados; a garantia de efeitos externos permanece at-least-once sujeita ao orçamento.
