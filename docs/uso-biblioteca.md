# Uso da biblioteca

[Início](../README.md) · [Arquitetura](arquitetura.md)

Este guia mostra como registrar contratos e consumir a biblioteca diretamente, sem ASP.NET Core ou HTTP. Para instalar os pacotes em outro projeto, consulte [build e pacotes](build-e-pacotes.md#distribuir-e-consumir-pacotes).

## Registro de contratos

O registro é configurado antes de usar a biblioteca e permanece estável durante o processamento. Um produtor precisa somente do contrato e de sua validação:

```csharp
using BackgroundJobs;

var logMessage = new JobDefinition<MessageArgs>("message.v1");
var registry = new JobRegistry();
registry.Contracts.Add(logMessage, args =>
{
    if (args.Message is null) throw new JobValidationException("Mensagem obrigatória.");
});

public sealed record MessageArgs(string Message);
```

As regras detalhadas de serialização, Unicode e compatibilidade estão em [contratos e versionamento](contratos-e-versionamento.md).

Para associar uma implementação a um contrato conhecido, use `registry.Handlers.Register(job, handler)`. Para adicionar contrato novo e handler juntos, use `registry.Register(job, handler, validate)`. O handler tem assinatura `Func<TArgs, JobContext, Task>`; cadastro e preparação não o executam. Nomes são sensíveis a maiúsculas e cadastros duplicados são rejeitados.

O consumidor fornece os tipos de argumentos, validadores e handlers a cada inicialização. Handlers e validadores podem executar simultaneamente e devem sincronizar estado compartilhado. A API disponível não oferece resolução de serviços com escopo por tentativa.

O validador opcional é um `Action<TArgs>`: retorno normal aceita os argumentos; `JobValidationException` declara rejeição com mensagem legível. Outras exceções propagam sem conversão. Agendamento e retry são configurados pelas [opções de criação](#opções-de-criação).

`JobDefinition<TArgs>` é imutável, exige nome e usa comparação ordinal sem normalização. Duplicidades, nomes desconhecidos e tipos incompatíveis geram `InvalidOperationException`; configuração com argumentos inválidos gera `ArgumentException` ou `ArgumentNullException`. O registro conjunto verifica as precondições antes de adicionar contrato e handler.

As interfaces públicas declaram criação tipada e gerenciamento independente do registro:

```csharp
Task<string> IJobClient.EnqueueAsync<TArgs>(
    JobDefinition<TArgs> job, TArgs args, JobOptions? options = null);
Task<JobDetails?> IJobManager.GetAsync(string jobId);
Task<IReadOnlyList<JobSummary>> IJobManager.ListAsync();
Task<JobOperationResult> IJobManager.DeleteAsync(string jobId);
Task<JobOperationResult> IJobManager.RequeueAsync(string jobId);
```

A consulta não exige contrato ou handler e usa `Args: JsonElement`. Ausência é `null`; falhas de leitura são erros. As implementações persistentes são `RedisJobClient` e `RedisJobManager`.

## Criar, executar e consultar sem HTTP

Com Redis iniciado, crie uma aplicação console fora do repositório. Na raiz deste checkout, os comandos abaixo geram um consumidor com referência somente à biblioteca; não é necessário publicar um pacote NuGet:

```bash
repo="$PWD"
consumer_dir="$(mktemp -d /tmp/background-jobs-consumer.XXXXXX)"
dotnet new console --framework net10.0 --output "$consumer_dir"
dotnet add "$consumer_dir" reference "$repo/src/BackgroundJobs/BackgroundJobs.csproj"
```

Substitua o `Program.cs` gerado pelo conteúdo completo abaixo. O registro e `EnqueueAsync` são suficientes para produzir o pedido; o restante demonstra execução e consulta pública sem abrir HTTP:

```csharp
using BackgroundJobs;
using StackExchange.Redis;

var message = new JobDefinition<MessageArgs>("message.v1");
var registry = new JobRegistry();
registry.Register(message, (args, context) =>
{
    Console.WriteLine($"{context.JobId}, tentativa {context.Attempt}: {args.Message}");
    return Task.CompletedTask;
});

using var connection = await ConnectionMultiplexer.ConnectAsync(
    Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "127.0.0.1:6379");
var prefix = "library-example-" + Guid.NewGuid().ToString("N");
IJobClient client = new RedisJobClient(registry.Contracts, connection, prefix);
IJobManager manager = new RedisJobManager(connection, prefix);
var id = await client.EnqueueAsync(message, new MessageArgs("Olá"));
var persisted = await manager.GetAsync(id)
    ?? throw new InvalidOperationException("Pedido não encontrado após confirmação.");
Console.WriteLine($"Persistido: {persisted.Id}, argumentos {persisted.Args}");

await using var server = new JobServer(registry, connection, prefix, workerCount: 1);
await server.StartAsync();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
JobDetails detail;
while (true)
{
    detail = await manager.GetAsync(id)
        ?? throw new InvalidOperationException("Pedido não encontrado.");
    if (detail.State is JobState.Succeeded or JobState.Failed) break;
    await Task.Delay(20, timeout.Token);
}
if (detail.State != JobState.Succeeded)
    throw new InvalidOperationException($"Job falhou: {detail.AttemptHistory.Last().Error}");
Console.WriteLine($"{detail.Id}: {detail.State}, tentativa {detail.Attempts}");
Console.WriteLine(string.Join(" → ", detail.History.Select(change => change.State)));
Console.WriteLine($"Resultado: {detail.AttemptHistory.Last().Outcome}");
await server.StopAsync();

public sealed record MessageArgs(string Message);
```

Execute com a conexão do Redis preparado:

```bash
REDIS_CONNECTION=127.0.0.1:6379 dotnet run --project "$consumer_dir"
```

A saída identifica o pedido persistido, argumentos, ID e tentativa recebidos pelo handler, estado `Succeeded`, histórico `Enqueued → Processing → Succeeded` e resultado da tentativa. O prefixo exclusivo é compartilhado pelos três componentes; guarde-o se quiser consultar os dados em outra execução. A conexão pertence ao consumidor e é descartada depois do servidor. Existe um executor por armazenamento; para outro ciclo de execução, crie uma nova instância de `JobServer`.

Um produtor que apenas cadastra `Contracts.Add` pode criar e consultar sem handler; ele não deve iniciar um executor com referências sem implementação. `GetAsync` não recebe registro e devolve `null` exclusivamente para ID inexistente; erros Redis e dados inválidos propagam como falhas.

## Opções de criação

`JobOptions` é imutável após inicialização e aceita `MaxAttempts` de 1 a 10 (padrão 3), `RetryDelayMs` de 0 a 60000 (padrão 1000) e `RunAt` opcional, do tipo `DateTimeOffset?`. Opções nulas na API C# usam os padrões. Por exemplo, depois de criar `client` e registrar `message` como no exemplo:

```csharp
var scheduledId = await client.EnqueueAsync(
    message,
    new MessageArgs("Mensagem agendada"),
    new JobOptions
    {
        RunAt = DateTimeOffset.UtcNow.AddMinutes(1),
        MaxAttempts = 3,
        RetryDelayMs = 1000
    });
```

O ID retorna após confirmação da persistência. Um horário futuro aguarda elegibilidade; passado ou igualdade com o instante de criação enfileiram imediatamente. O atraso de retry é contado do fim da tentativa. Chamadas públicas independentes criam IDs distintos.

## Consultar e gerenciar

`IJobManager` também funciona sem catálogo, handlers ou motor iniciado:

```csharp
IReadOnlyList<JobSummary> items = await manager.ListAsync();
JobOperationResult deletion = await manager.DeleteAsync(id);
JobOperationResult requeue = await manager.RequeueAsync(id);
```

`ListAsync` inclui todos os estados, sem ordem definida ou paginação pública. Cada resumo (`Id`, `Name`, `State`, `Attempts`) é lido atomicamente; a coleção não representa um único instante global durante mutações concorrentes. Campos inválidos ou um ID indexado sem registro geram erro, sem retornar lista parcial.

As mutações retornam `Applied`, `NotFound` ou `Conflict` somente após a decisão persistente. Deletar aceita `Scheduled`/`Enqueued`, mantém o detalhe consultável e impede execução até um reenfileiramento posterior. Reenfileirar aceita `Succeeded`/`Failed`/`Deleted`, mantém ID, contrato, JSON, opções e criação, zera o contador e coloca o job no fim da fila, sem reaplicar o agendamento original. A primeira tentativa de cada ciclo é 1; todos os históricos anteriores permanecem. IDs são opacos, sem normalização; nulo é erro de argumento. Falhas de infraestrutura são exceções, inclusive quando uma confirmação não chega: não se deve interpretar o erro como prova de ausência de efeito. A biblioteca reconcilia internamente a mesma chamada usando recibos persistidos; outra chamada manual é uma nova operação legítima.

## Conexão e configuração

A conexão é criada, compartilhada e descartada pelo chamador; os serviços não a descartam nem abrem uma conexão por pedido. Cliente, gerente e servidor aceitam `prefix`, com padrão `background-jobs`; use o mesmo em todos os componentes. Prefixos diferentes isolam armazenamentos lógicos; o exemplo gera um prefixo exclusivo para isolar seus dados. São aceitas apenas letras ASCII, dígitos, ponto, hífen e sublinhado, sem valor vazio. Cliente, gerente e servidor também aceitam `timeProvider`, com padrão `TimeProvider.System`. O servidor aceita `workerCount` como último argumento, com padrão 4 e mínimo 1; a quantidade fica fixa durante a vida da instância. Use `workerCount: 1` para FIFO pela sequência persistida de entrada na fila. Com vários workers, não há garantia da ordem exata de início ou término. A reserva Lua garante exclusividade por job; a quantidade de loops limita as funções simultâneas. Handlers e validadores podem executar simultaneamente e devem sincronizar seu estado compartilhado.

## Ciclo de vida do servidor

`StartAsync` compartilha a inicialização entre chamadas repetidas; uma instância inicia uma única vez. Antes da prontidão, o servidor verifica os handlers de todos os jobs armazenados, inclusive finalizados, e recupera tentativas interrompidas. `IsAvailable` informa a disponibilidade e `LastError` expõe o diagnóstico.

`StopAsync` é idempotente, impede novas admissões e aguarda as funções e as operações persistentes admitidas; não cancela handlers. A espera por funções é ilimitada. Para outro ciclo de serviço, crie uma nova instância de `JobServer`.

`JobServerHostedService`, em `BackgroundJobs.Hosting`, integra o servidor a `IHostedService`. Configure o host para aguardar a parada, como na demo, que usa `HostOptions.ShutdownTimeout = Timeout.InfiniteTimeSpan`.

As regras de prontidão, retry, recuperação e suspensão/retomada após falhas Redis estão em [processamento](processamento.md). Falha de confirmação não prova ausência de efeito. Efeitos externos dos handlers devem considerar a garantia **at-least-once sujeita ao limite de tentativas**.

## Documentação relacionada

- [Contratos e versionamento](contratos-e-versionamento.md): serialização, validação e compatibilidade.
- [Processamento](processamento.md): estados, execução e ciclo do servidor.
- [Persistência Redis](persistencia-redis.md): modelo e garantias de armazenamento.
