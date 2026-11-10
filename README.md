# BackgroundJobs

Biblioteca em .NET 10, baseada em [docs/spec-base.md](docs/spec-base.md).

- `BackgroundJobs.slnx`: solução com biblioteca, demo, testes unitários e de integração.
- `src/BackgroundJobs`: registro tipado, criação e consulta, execução com um worker, persistência de tentativas e ciclo do servidor.
- `src/BackgroundJobs.Demo`: aplicação ASP.NET Core com os três jobs da spec, criação, detalhe, eventos e health.
- `tests/BackgroundJobs.UnitTests`: registro, preparação, modelos e orquestração, sem Redis.
- `tests/BackgroundJobs.IntegrationTests`: criação atômica, consulta consistente, índices e concorrência com Redis real.
- `tests/BackgroundJobs.HttpTests`: contratos HTTP, validação de entrada e integração com Redis real.

A biblioteca cria, consulta e executa jobs imediatos com um worker. Sucesso e falha terminal persistem estados, histórico e tentativas. Falhas com orçamento restante ficam `scheduled`, com elegibilidade igual ao fim mais atraso. Promoção de agendamentos, execução de novas tentativas, quantidade configurável de workers, listagem, deleção, reenfileiramento e recuperação após queda ainda não estão implementados.

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
var id = await client.EnqueueAsync(DemoJobs.LogMessage, new LogMessageArgs("Olá"));
await using var server = new JobServer(registry, connection, prefix);
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

A conexão é criada, compartilhada e descartada pelo chamador; os serviços não a descartam nem abrem uma conexão por pedido. Cliente, gerente e servidor aceitam `prefix`, com padrão `background-jobs`; use o mesmo em todos os componentes. Prefixos diferentes isolam armazenamentos lógicos; o exemplo usa `library-example` para isolar os dados da demo. São aceitas apenas letras ASCII, dígitos, ponto, hífen e sublinhado, sem valor vazio. Cliente e servidor também aceitam `timeProvider`, com padrão `TimeProvider.System`.

O ID retorna somente após confirmação da criação. A escrita inclui dados, histórico e índices na mesma operação. Jobs futuros ficam `Scheduled`, com data exata preservada, aguardando promoção, ainda não implementada. Não há loop próprio de retry/reconciliação de falhas de rede; uma falha de confirmação não deve ser interpretada como prova de ausência do pedido. Chamadas públicas independentes geram IDs distintos.

`JobServer` verifica handlers de todos os jobs armazenados, inclusive finalizados, antes de liberar o loop. `processing` residual impede prontidão com diagnóstico de recuperação pendente. `IsAvailable` informa disponibilidade e `LastError` expõe erro fatal. Erros de desserialização, validação ou função fecham a tentativa e não impedem outros jobs; falhas Redis encerram o loop sem reinvocar a função nem buscar outro job.

`StartAsync` inicia uma instância uma única vez e compartilha a inicialização entre chamadas repetidas. `StopAsync` fecha novas reservas e aguarda a reserva admitida, a função e sua confirmação; não cancela handlers. A parada é idempotente. Para outro ciclo de serviço, crie uma nova instância. `JobServerHostedService`, em `BackgroundJobs.Hosting`, oferece integração `IHostedService`; o host deve permitir espera ilimitada na parada, como na composição da demo.

## Redis local

O storage escolhido para os jobs é Redis. O `compose.yaml` usa a imagem oficial
`redis:8.2.10-alpine`, com volume persistente em `/data`, AOF habilitado e
`appendfsync always` para sincronizar as escritas em disco antes de responder.
A política `noeviction` impede a remoção automática de chaves por pressão de memória.
Referência: [persistência do Redis](https://redis.io/docs/latest/operate/oss_and_stack/management/persistence/).

Na raiz do projeto, com Docker e Docker Compose disponíveis, inicie o serviço:

```bash
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

O volume `redis-data` é mantido ao parar ou recriar o container. O comando
`docker compose down -v` também remove o volume e apaga os dados locais.

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

A demo executa explicitamente **um worker**. `WORKER_COUNT` (padrão 4 na spec) e `DATA_DIR` ainda não são consumidos. O Compose mantém os dados no volume `redis-data`.

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

Para demonstrar falha terminal, continuidade e retry pendente:

```bash
curl --fail-with-body -sS "$base/demo/jobs" -H 'Content-Type: application/json' \
  -d '{"job":"fail-n-times","params":{"failTimes":1},"options":{"maxAttempts":1}}'
curl --fail-with-body -sS "$base/demo/jobs" -H 'Content-Type: application/json' \
  -d '{"job":"log-message","params":{"message":"Execução após erro"}}'
curl --fail-with-body -sS "$base/demo/jobs" -H 'Content-Type: application/json' \
  -d '{"job":"fail-n-times","params":{"failTimes":1},"options":{"maxAttempts":3,"retryDelayMs":500}}'
```

Consulte os IDs até encontrar, respectivamente, `failed`, `succeeded` e `scheduled`. No último caso, há uma tentativa fechada com erro e `scheduledAt` exatamente 500 ms após seu fim; a execução de retry ainda não está implementada. Para criar agendamento futuro, acrescente `options.runAt` com um instante ISO-8601 UTC futuro: ele permanece pendente.

`GET /demo/events` retorna `{ "items": [...] }`, em ordem de registro, com `type`, `jobId`, `attempt`, `at` UTC e `detail`. Os handlers registram `started`, `finished` com `detail.outcome` igual a `ok` ou `error` e, para `log-message`, um evento `message`. São eventos em memória, independentes do histórico persistente, perdidos no reinício.

`GET /health` retorna `200 { "status": "ok" }` com o motor iniciado e operacional, ou `503 { "status": "unavailable" }` durante indisponibilidade/parada enquanto o HTTP estiver acessível. Erro fatal do motor fica disponível em `JobServer.LastError`. Uma aplicação ASP.NET Core cuja inicialização do servidor falha não libera sua escuta HTTP.

A entrada aceita somente `job`, `params` e `options`, com nomes sensíveis a maiúsculas, sem campos desconhecidos ou duplicados. `options` ausente ou `{}` usa os padrões 3/1000; `options: null` e campos de opção nulos são inválidos. Inteiros não aceitam strings, frações ou notação exponencial; `runAt` exige ISO-8601 com `Z` ou offset explícito. As garantias de Unicode do registro também se aplicam à criação HTTP.

Entrada inválida retorna `400` e ID inexistente retorna `404`, com `{ "error": "mensagem legível" }`. Falhas inesperadas retornam `500` com mensagem genérica e detalhes nos logs. Listagem, deleção e reenfileiramento permanecem planejados.

## Validar

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

A cobertura está nos [testes unitários](tests/BackgroundJobs.UnitTests), nos [testes de persistência e processamento](tests/BackgroundJobs.IntegrationTests) e nos [testes HTTP](tests/BackgroundJobs.HttpTests). Eles verificam contratos e validação, reserva exclusiva, conclusão atômica e idempotente, índices, snapshots consistentes, isolamento de erros e parada coordenada. O aceite HTTP cobre sucesso imediato com históricos e eventos, início em até 2 segundos com worker livre, falha terminal, continuidade e retry pendente. A execução pela biblioteca também é verificada sem HTTP.

Os testes HTTP de criação/consulta isolam o motor na composição de teste; uma suíte separada usa o motor ativo, com prefixo exclusivo compartilhado e parada antes da limpeza. Não há flag de produto para desativar o motor.

O resultado verificado é build sem avisos ou erros e **369 testes aprovados** (147 unitários, 105 de integração e 117 HTTP), sem falhas ou testes ignorados, com SDK 10.0.114 e Redis 8.2.10 dedicado na porta 16379, AOF, `appendfsync always` e `noeviction`. A demonstração com a aplicação real concluiu o pedido imediato em **94,66 ms** desde o início da criação e confirmou falha terminal, continuidade, retry pendente e nove eventos. Esses tempos são observações locais, sem garantia de tempo real.

Nesse ambiente WSL, sem Docker disponível, Redis usa a distribuição oficial compilada em `/tmp`, com Zig 0.14.1 e runtime libgcc 13.3. Para reprodução habitual, use o Compose documentado ou Redis externo com as mesmas configurações; configure `REDIS_CONNECTION` com a porta escolhida.

O critério 1 da spec está verificado. Configuração de concorrência, promoção e execução de retry, operações manuais, recuperação após queda e `DATA_DIR` ainda não estão implementados. As operações reconhecem reenvios equivalentes de criação/conclusão, mas não há reconciliação ou retomada automática após falha de confirmação. A parada básica foi verificada com sinais e Redis real; testes em processos separados e `kill -9` ainda estão pendentes.
