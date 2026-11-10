# BackgroundJobs

Biblioteca em .NET 10, baseada em [docs/spec-base.md](docs/spec-base.md).

- `BackgroundJobs.slnx`: solução com biblioteca, demo, testes unitários e de integração.
- `src/BackgroundJobs`: registro tipado, validação, criação persistente e consulta de detalhe no Redis.
- `src/BackgroundJobs.Demo`: aplicação ASP.NET Core que cadastra `log-message` e expõe criação e consulta por HTTP.
- `tests/BackgroundJobs.UnitTests`: registro, preparação, modelos e orquestração, sem Redis.
- `tests/BackgroundJobs.IntegrationTests`: criação atômica, consulta consistente, índices e concorrência com Redis real.
- `tests/BackgroundJobs.HttpTests`: contratos HTTP, validação de entrada e integração com Redis real.

A E1 disponibiliza `RedisJobClient` e `RedisJobManager` para criar e consultar jobs pela biblioteca, além de `POST /demo/jobs` e `GET /api/jobs/{id}` na demo. Execução, promoção de agendamentos, retry e demais operações permanecem planejados; os pedidos criados ficam pendentes no armazenamento.

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

## Criar e consultar sem HTTP

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
IJobClient client = new RedisJobClient(registry.Contracts, connection);
IJobManager manager = new RedisJobManager(connection);
var id = await client.EnqueueAsync(DemoJobs.LogMessage, new LogMessageArgs("Olá"));
var detail = await manager.GetAsync(id);
Console.WriteLine($"{detail!.Id}: {detail.State}"); // Enqueued; handler ainda não executado
```

O exemplo usa o contrato da demo, mas o consumidor pode definir seus próprios tipos e validadores. O cadastro de handler é opcional para criar e consultar. `GetAsync` não recebe registro e devolve `null` exclusivamente para ID inexistente; erros Redis e dados inválidos propagam como falhas.

A conexão é criada, compartilhada e descartada pelo chamador; os serviços não a descartam nem abrem uma conexão por pedido. Os dois construtores aceitam `prefix`, com padrão `background-jobs`. Use o mesmo prefixo para os serviços do mesmo armazenamento e prefixos diferentes para isolamento. São aceitas apenas letras ASCII, dígitos, ponto, hífen e sublinhado, sem valor vazio. O cliente também aceita `timeProvider`, com padrão `TimeProvider.System`.

O ID retorna somente após confirmação da criação. A escrita inclui dados, histórico e índices na mesma operação. Jobs futuros ficam `Scheduled`, com data exata preservada; não há motor para promovê-los nesta entrega. Não há loop próprio de retry/reconciliação de falhas de rede; uma falha de confirmação não deve ser interpretada como prova de ausência do pedido. Chamadas públicas independentes geram IDs distintos.

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

`WORKER_COUNT` e `DATA_DIR` pertencem às etapas do motor e de persistência/reinício; ainda não são consumidos. O Compose mantém os dados no volume `redis-data`.

Os diretórios de saída são centralizados na pasta `build/` da raiz por `Directory.Build.props`, usando o [layout de artefatos do SDK](https://learn.microsoft.com/pt-br/dotnet/core/sdk/artifacts-output):

- `build/bin/<projeto>/<configuração>/`: binários compilados.
- `build/obj/<projeto>/<configuração>/`: arquivos intermediários; os arquivos de restore ficam em `build/obj/<projeto>/`.

A pasta `build/` é ignorada pelo Git.


## Demonstrar criação e consulta HTTP

Com Redis e a demo em execução, execute em outro terminal Bash (os comandos usam `curl`, `python3` e GNU `date`, disponíveis no ambiente WSL documentado):

```bash
base=http://localhost:8080
immediate=$(curl --fail-with-body -sS "$base/demo/jobs" \
  -H 'Content-Type: application/json' \
  -d '{"job":"log-message","params":{"message":"Olá imediato"}}')
printf '%s\n' "$immediate"
immediate_id=$(printf '%s' "$immediate" | python3 -c 'import json,sys; print(json.load(sys.stdin)["jobId"])')
curl --fail-with-body -sS "$base/api/jobs/$immediate_id" | python3 -m json.tool

run_at=$(date -u -d '+1 hour' '+%Y-%m-%dT%H:%M:%SZ')
future=$(curl --fail-with-body -sS "$base/demo/jobs" \
  -H 'Content-Type: application/json' \
  -d "{\"job\":\"log-message\",\"params\":{\"message\":\"Olá futuro\"},\"options\":{\"runAt\":\"$run_at\",\"maxAttempts\":2,\"retryDelayMs\":500}}")
printf '%s\n' "$future"
future_id=$(printf '%s' "$future" | python3 -c 'import json,sys; print(json.load(sys.stdin)["jobId"])')
curl --fail-with-body -sS "$base/api/jobs/$future_id" | python3 -m json.tool
```

Cada criação retorna `202 { "jobId": "..." }`; cada consulta retorna `200` com `args` estruturado, opções efetivas, datas UTC e histórico inicial. O primeiro pedido fica `enqueued`; o segundo, `scheduled`, com `scheduledAt` igual ao instante informado. Ambos têm zero tentativas e permanecem pendentes, pois execução e promoção de agendamentos pertencem às próximas etapas.

A entrada aceita somente `job`, `params` e `options`, com nomes sensíveis a maiúsculas, sem campos desconhecidos ou duplicados. Apenas `log-message` está disponível: `params.message` é obrigatório e textual. `options` ausente ou `{}` usa os padrões; `options: null` e campos de opção nulos são inválidos. Inteiros não aceitam strings, frações ou notação exponencial; `runAt` exige ISO-8601 com `Z` ou offset explícito.

Para observar os erros:

```bash
curl -i -sS "$base/demo/jobs" -H 'Content-Type: application/json' \
  -d '{"job":"log-message","params":{"message":null}}'
curl -i -sS "$base/api/jobs/id-inexistente"
```

Entrada inválida retorna `400` e ID inexistente retorna `404`, sempre com `{ "error": "mensagem legível" }`. Falhas inesperadas retornam `500` com mensagem genérica; detalhes ficam nos logs do servidor. Listagem, deleção, reenfileiramento, eventos, outros jobs da demo e `/health` permanecem planejados.


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

A cobertura e os limites da entrega estão em [validação da E1](docs/validacao-e1.md) e [validação do armazenamento](docs/validacao-e1.2.md).
