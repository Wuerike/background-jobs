# Persistência Redis

[Início](../README.md) · [Arquitetura](arquitetura.md)

Este documento descreve o modelo persistido, as operações atômicas e a configuração do Redis, incluindo o procedimento de preservação de dados. Os comandos partem da raiz do repositório.

- [Modelo de dados](#modelo-de-dados).
- [Interface interna de armazenamento](#interface-interna-de-armazenamento).
- [Operações atômicas e concorrência](#operações-atômicas-e-concorrência).
- [Redis local](#redis-local) e [preservação de volumes](#preservar-um-volume-nomeado-existente).
- [Durabilidade e retenção](#durabilidade-e-retenção).

## Modelo de dados

### Job

O modelo público acompanha a spec:

| Campo | Significado |
| --- | --- |
| `id` | String opaca gerada pela biblioteca. |
| `name` | Identificador estável do contrato registrado, incluindo sua versão lógica quando aplicável. |
| `args` | Argumentos em JSON. |
| `state` | Estado atual. |
| `attempts` | Tentativas iniciadas no ciclo atual. |
| `maxAttempts` | Limite incluindo a primeira execução. |
| `retryDelayMs` | Atraso fixo entre tentativas. |
| `scheduledAt` | Elegibilidade enquanto estiver em `scheduled`; `null` nos demais estados. |
| `createdAt` | Instante da criação original. |
| `updatedAt` | Instante da última atualização. |
| `history` | Mudanças de estado em ordem cronológica. |
| `attemptHistory` | Tentativas em ordem cronológica. |

As datas expostas pela API usam ISO-8601 UTC.

O modelo público disponível é `JobDetails`, com propriedades somente de leitura, argumentos clonados com vida útil independente e cópias não modificáveis dos históricos. `JobState` contém os seis estados; `JobStateChange` contém estado, momento UTC e motivo. `JobAttempt` contém número, início e fim UTC, resultado opcional (`JobAttemptOutcome`) e erro opcional; fim e resultado nulos representam tentativa aberta. A leitura consistente desses dados no Redis é implementada por `RedisJobManager`.

### Históricos e metadados internos

Cada mudança de estado, incluindo criação, registra `{ state, at, reason }`. Motivos devem identificar a operação, como criação, elegibilidade, início de execução, sucesso, falha, recuperação, deleção ou reenfileiramento manual.

Cada tentativa registra `{ attempt, startedAt, finishedAt, outcome, error }`. Enquanto estiver em execução, `finishedAt` e `outcome` são `null`. O resultado final é `succeeded`, `failed` ou `interrupted`.

Além dos campos públicos, persistir:

- **Ciclo de execução:** criação inicial ou cada reenfileiramento manual.
- **Token único da tentativa:** permite reconhecer e confirmar uma execução específica.
- **Sequência de entrada na fila:** estabelece FIFO sem depender de timestamps iguais.

A identidade interna de uma tentativa é `(jobId, ciclo, tentativa)`. Isso permite preservar tentativas antigas mesmo quando o reenfileiramento reinicia a numeração em 1. O ciclo e o token não precisam aparecer no contrato HTTP.

### Estruturas Redis

Usar um prefixo de chaves da biblioteca e as seguintes estruturas:

| Estrutura | Finalidade |
| --- | --- |
| Hash por job | Dados e estado atuais. |
| Listas por job | Histórico de estados e registros de tentativas. |
| Sorted set `ready` | Única fila de execução; score é a sequência de entrada. |
| Sorted set `scheduled` | Índice de elegibilidade; score é o instante agendado. |
| Set `processing` | Localizar jobs que precisam de recuperação após queda. |
| Índice de jobs | Listar todos os jobs, inclusive finalizados. |
| Contador global | Gerar a sequência crescente de entrada na fila. |

`scheduled` e `processing` são índices auxiliares, não filas adicionais.

Cada entrada ou reentrada em `ready` recebe uma nova sequência. Não aplicar TTL ou remoção automática aos jobs e seus históricos.

### Criação e leitura disponíveis

`RedisJobClient` recebe `JobContracts`, `IConnectionMultiplexer`, prefixo e `TimeProvider` opcional. `RedisJobManager` recebe conexão, prefixo e `TimeProvider` opcional. O chamador mantém e descarta a conexão compartilhada; os serviços não dependem de DI ou host. Prefixo padrão: `background-jobs`, com letras ASCII, dígitos, `.`, `_` e `-`; vazio e outros caracteres são rejeitados na composição.

`RedisJobStore` usa `StackExchange.Redis` 2.9.32 e scripts `.lua` embutidos no assembly, enviados por texto a `ScriptEvaluateAsync`, com chaves explícitas. Não mantém gerenciador próprio de cache de scripts, não usa `FireAndForget` e não implementa loop próprio de retries de rede.

Para o prefixo `p`, a representação persistida é:

| Chave | Conteúdo implementado |
| --- | --- |
| `p:job:<id>` | Hash: `id`, `name`, JSON em `args`, `state`, `attempts`, `maxAttempts`, `retryDelayMs`, `createdAt`, `updatedAt`, `creationIdentity`; `scheduledAt` somente em `scheduled`. |
| `p:job:<id>:history` | Lista de JSON `{state, at, reason}`, com entrada inicial obrigatória. |
| `p:job:<id>:attempts` | Lista de JSON `{attempt, startedAt, finishedAt, outcome, error}`; ausente enquanto vazia. |
| `p:jobs` | Set de IDs, incluindo finalizados; startup percorre `SSCAN` com páginas de 128. |
| `p:ready` | Sorted set com sequência inteira crescente, limitada a `2^53 - 1`. |
| `p:scheduled` | Sorted set com elegibilidade em milissegundos Unix, arredondada para cima por aritmética inteira. |
| `p:sequence` | Contador incrementado somente para entrada em `ready`. |
| `p:processing` | Set de IDs com tentativa aberta confirmada. |

Datas no hash e nos históricos usam formato `O` em UTC e cultura invariável, preservando ticks. Estados e resultados são textos minúsculos da spec. O score agendado nunca antecipa a data exata; no extremo superior de `DateTimeOffset`, pode representar o milissegundo seguinte ao último instante representável. A seleção compara scores no mesmo domínio temporal, sem reconstruir o instante arredondado como `DateTimeOffset`, e confere a data exata antes de promover.

A identidade de criação é SHA-256 de um array JSON determinístico dos dados originais, incluindo ID, opções, datas, argumentos e histórico inicial. A criação equivalente já confirmada retorna sem atualizar estado, fila, datas ou histórico; identidade diferente com mesmo ID falha. Chamadas públicas independentes recebem IDs próprios. A criação admite uma retransmissão equivalente após falha de transporte; a identidade e a integridade atuais são verificadas antes de confirmar o pedido.

O script de criação verifica tipos de todas as chaves, resíduos locais/índices, parâmetros e faixa/canonicalidade do contador antes de escrever. Isso é necessário porque Lua impede intercalamento, mas Redis não desfaz comandos anteriores a um erro de runtime. A criação grava hash, histórico e índices juntos, sem TTL. Tentativas e `processing` são criados somente quando há uma reserva real.

A leitura executa `HGETALL` e as duas listas em um único script. `JobCodec` materializa os modelos públicos, clona os argumentos e descarta os documentos JSON temporários; não resolve DTO ou valida regra de negócio na consulta. ID é opaco e não recebe trim nem validação de GUID; nulo é erro de argumento. Hash e listas locais ausentes representam inexistência. Tipos incorretos, resíduos locais sem hash, campos obrigatórios ausentes, histórico ausente e valores inválidos falham explicitamente. Corrupção na materialização produz `InvalidDataException` com ID e campo, sem expor valores ou mensagens do parser; erros Redis preservam sua classificação.

A leitura reconhece todos os estados, resultados de tentativa, tentativas abertas e numeração repetida entre ciclos. As operações disponíveis produzem todos os estados e múltiplos ciclos; snapshots semeados complementam somente formatos, corrupção e extremos.

### Metadados de execução e confirmação

A reserva acrescenta `cycle` (inicialmente `0`, inclusive para registros sem esse campo), `attemptToken` (GUID opaco) e `attemptIndex` (posição na lista preservada). A conclusão mantém esses campos e grava `completionIdentity`, SHA-256 do resultado capturado com ID, ciclo, token, tentativa fechada e transição. Reenvio do mesmo resultado confirma sem alterar datas, listas ou índices; outro token, ciclo ou resultado é rejeitado.

`PeekAsync` lê a cabeça e seu score em uma única operação sem remover, materializa o detalhe e confere metadados. Uma reentrada com nova posição durante essa leitura descarta o candidato antigo. O script de reserva verifica novamente a cabeça e compara integralmente hash e listas com o snapshot conferido. Assim, o handler recebe os argumentos correspondentes à reserva, mesmo com concorrência entre leitura e escrita. Um candidato perdido retorna sem escrever; precondição inválida gera erro explícito.

`processing-common.lua` concentra verificações de tipos e snapshots. Reserva e conclusão verificam a participação exclusiva nos índices, estado, orçamento e metadados antes da primeira escrita. A reserva também confere a posição FIFO e o contador. A conclusão confere a tentativa aberta, calcula o instante de retry em C# com precisão de ticks e rejeita overflow antes da escrita. Não há TTL.

`ScheduledCandidate` mantém detalhe, snapshot e score conferidos na seleção. A leitura de cada candidato captura atomicamente seu estado, índices, listas e score atual. Antes de descartar uma entrada que mudou, confere seus metadados atuais, inclusive a correspondência entre score e `scheduledAt`. A promoção valida orçamento e ausência de tentativa aberta e usa `promote.lua` para confirmar snapshot, índices e sequência antes de escrever. Cada promoção recebe sequência nova, incluindo retry: jobs já em `ready` permanecem à frente. A promoção preserva o resultado fechado; o recibo da conclusão permite confirmar esse resultado mesmo após nova tentativa ou ciclo manual, sem alterar a tentativa atual.

Uma promoção antiga só perde a disputa quando há evidência de progresso: dados imutáveis e prefixos dos históricos preservados, transições adicionais válidas, contador e ciclo coerentes e índices exclusivos. A tentativa aberta pode ter sido fechada, mas tentativas já finalizadas não mudam. O snapshot que comprova progresso também é materializado e validado em C#, incluindo datas e scores; mudança de estado ou recibo isolado não escondem corrupção. O reenfileiramento invalida tokens e posições da tentativa atual; mantém a identidade de criação e os recibos das operações confirmadas. A confirmação incerta usa recibo por operação e, quando necessário, uma retransmissão do mesmo envelope; outra chamada legítima tem identidade própria.

O snapshot completo é limitado a um job, não a uma varredura global. Históricos sem retenção crescem por exigência do domínio; sua leitura tem custo proporcional ao histórico daquele job.

### Listagem e operações manuais

A listagem percorre `jobs` por `SSCAN` em páginas de 128 e deduplica IDs. Cada item usa um único `HMGET` de `id`, `name`, `state` e `attempts`, sem carregar JSON de argumentos ou históricos. Retorna coleção independente e não modificável com todos os estados, incluindo `deleted`, sem ordem definida. Cada resumo representa um instante consistente; a coleção não oferece snapshot global durante criações ou mutações concorrentes. Índice órfão, tipo incorreto ou campo projetado inválido são erros, sem omissão silenciosa ou lista parcial.

`JobOperationResult` distingue `Applied`, `NotFound` e `Conflict`; corrupção e infraestrutura são exceções. O manager não exige catálogo, handler ou servidor. Nulo é erro de argumento e as demais strings são IDs opacos. Ausência é decidida atomicamente após conferir também resíduos locais e índices.

`manage.lua` compara o snapshot validado e decide a transição na mesma operação que atualiza os índices e o histórico. Deleção aceita somente `scheduled`/`enqueued`, remove a pendência correspondente, limpa `scheduledAt` e registra `deleted`, data UTC e motivo, preservando contador, tentativas e dados. Reenfileiramento aceita somente `succeeded`/`failed`/`deleted`, zera `attempts`, incrementa o ciclo e acrescenta entrada no fim de `ready`, sem reaplicar o agendamento original. Argumentos, contrato, opções, criação, identidade de criação e ambos os históricos são preservados.

Ciclo ausente equivale a zero. O ciclo é inteiro canônico não negativo de 64 bits, incrementado com aritmética verificada em C# e enviado como texto; Lua não arredonda esse incremento. A sequência FIFO é limitada a `2^53 - 1`, validada antes da primeira escrita. A posição da tentativa nova é o final da lista acumulada. Uma leitura de relógio por preparação de mutação admissível define a data; retrocesso em relação à atualização persistida é erro explícito.

Se o snapshot mudar por uma transição comprovada, o script não escreve e o manager repete apenas a leitura/preparação para decidir pelo estado atual. Não repete uma escrita cujo resultado ficou incerto. Operações independentes não são idempotentes: uma segunda deleção de `deleted` conflita; reenfileiramentos podem ter sucesso em ciclos distintos se houver finalizações intercaladas.

## Interface interna de armazenamento

`IJobStore` oferece criação, consulta, listagem e mutações manuais. `IProcessingStore` define `ReadReferencesAsync`, `HasProcessingAsync`, `ReadRecoveryAsync`, `TryRecoverAsync`, `ReadScheduledAsync`, `TryPromoteAsync`, `PeekAsync`, `TryClaimAsync` e `CompleteAsync`; `RedisJobStore` implementa ambos. A separação permite controlar o processamento em testes sem implementar operações de criação sem uso naquele cenário.

Esses contratos expressam operações de domínio porque cada transição precisa manter a consistência entre job, índices, tentativa e histórico. Um repositório CRUD genérico não representa essas garantias.

`JobCandidate` reúne o detalhe materializado, o snapshot exato do hash e listas, o ciclo interno e a posição FIFO capturada junto do ID da cabeça. `ClaimedJob` reúne os argumentos conferidos, número, ciclo, token e início confirmados. `AttemptCompletion` captura o fim e o erro uma vez; erro nulo significa sucesso. O worker reconstrói os argumentos persistidos, sem reutilizar objetos do produtor.

`RecoveryCandidate` mantém o detalhe e o snapshot completo da tentativa interrompida; `TryRecoverAsync` distingue recuperação confirmada de candidato cujo histórico comprova avanço válido da mesma tentativa.

A preparação usa uma única leitura de `TimeProvider` para `createdAt`, `updatedAt`, histórico inicial e comparação de elegibilidade. Após validar as [opções de criação](uso-biblioteca.md#opções-de-criação), o pedido preparado começa com zero tentativas e histórico de tentativas vazio. Horário futuro produz `scheduled` e `scheduledAt` UTC com precisão de ticks; ausência, passado ou igualdade produzem `enqueued` e `scheduledAt = null`. A confirmação da criação valida identidade imutável, dados e índices atuais; uma retransmissão conserva o mesmo ID.

## Operações atômicas e concorrência

Toda mudança de estado atualiza, na mesma operação atômica, os índices afetados, a tentativa quando aplicável e o histórico correspondente.

Lua é usado porque uma operação de domínio envolve várias estruturas Redis e verificações condicionais. Na criação, verificar a existência do ID, gravar o job e seu histórico e inseri-lo nos índices precisam ocorrer sem intercalamento de outros comandos. Chamadas separadas poderiam permitir que outra operação observasse dados incompletos ou alterasse uma condição entre sua verificação e a escrita. O mesmo princípio permite consultar estado, contador e históricos de um único momento.

O gerador da biblioteca produz os IDs. O script de criação protege a verificação e a gravação do mesmo ID contra concorrência: rejeita pedidos com identidade diferente e reconhece repetições equivalentes sem duplicar histórico ou posição na fila. Essa garantia é restrita ao mesmo ID e à mesma identidade de criação. Chamadas públicas independentes geram IDs próprios; a execução dos handlers segue a garantia at-least-once da spec. A operação de reserva descrita em [reserva de execução](#reserva-de-execução) garante exclusividade; a execução usa N loops de workers e um loop independente de scheduler.

As operações implementadas são:

| Script | Operação atômica e garantia |
| --- | --- |
| [`create.lua`](../src/BackgroundJobs/Storage/Redis/create.lua) | Verifica as precondições e cria hash, histórico inicial, índice geral e participação exclusiva em `ready` ou `scheduled`; para `ready`, atribui uma sequência FIFO na mesma execução. Repetição equivalente confirma sem novas escritas. |
| [`get.lua`](../src/BackgroundJobs/Storage/Redis/get.lua) | Lê hash, histórico e tentativas em um snapshot consistente, sem misturar momentos distintos. Distingue ausência de resíduos locais; a materialização C# valida o conteúdo. |
| [`promote.lua`](../src/BackgroundJobs/Storage/Redis/promote.lua) | Confere snapshot, elegibilidade, tipos, índices, orçamento e sequência antes de retirar de `scheduled`, atribuir nova posição em `ready`, limpar `scheduledAt` e acrescentar histórico; preserva tentativas e identidade de conclusão. |
| [`manage.lua`](../src/BackgroundJobs/Storage/Redis/manage.lua) | Decide ausência, conflito ou mutação manual com snapshot, tipos e índices conferidos; preserva históricos e atribui ciclo/posição novos somente no reenfileiramento. |
| [`read-scheduled.lua`](../src/BackgroundJobs/Storage/Redis/read-scheduled.lua) | Captura snapshot e score atuais de um candidato selecionado, permitindo validar ou descartar a geração sem misturar leituras. |
| [`claim.lua`](../src/BackgroundJobs/Storage/Redis/claim.lua) | Confere cabeça, snapshot e índices; retira de `ready`, inclui em `processing`, abre tentativa e registra a transição. |
| [`recover.lua`](../src/BackgroundJobs/Storage/Redis/recover.lua) | Fecha a tentativa interrompida, invalida a posse da tentativa aberta e reenfileira sem atraso ou finaliza por orçamento; repetição comprovada não escreve. |
| [`complete.lua`](../src/BackgroundJobs/Storage/Redis/complete.lua) | Fecha tentativa, remove de `processing`, persiste sucesso, falha terminal ou agendamento de retry; confirma reenvios equivalentes. |
| [`receipt.lua`](../src/BackgroundJobs/Storage/Redis/receipt.lua) | Confere geração e recibo da operação, checksum e progresso íntegro antes da reapresentação; o invólucro de `RedisJobStore` persiste a decisão junto da transição. |

Essas operações usam scripts Lua versionados junto ao código da biblioteca e executados por `RedisJobStore`. Os scripts verificam precondições antes das escritas e trabalham com lotes limitados. As chaves acessadas devem ser fornecidas explicitamente à chamada do script.

O Redis executa scripts de forma atômica, permitindo verificações e atualizações sem intercalamento de outros clientes. Scripts longos bloqueiam o servidor e devem ser evitados. A biblioteca também precisa suportar o recarregamento dos scripts quando seu cache não estiver disponível. Referência: [scripting com Lua no Redis](https://redis.io/docs/latest/develop/programmability/eval-intro/).

Atomicidade não fornece rollback de comandos já executados quando um erro de runtime ocorre no script. As precondições conhecidas são verificadas antes da primeira escrita; falhas de infraestrutura e confirmações ambíguas seguem o tratamento descrito em [retry e falhas](processamento.md#retry-e-falhas).

### Reserva de execução

A leitura de um candidato não autoriza sua execução. `TryClaimAsync` confirma atomicamente:

1. Que o job continua `enqueued` e é o primeiro candidato elegível da fila.
2. A remoção de `ready` e a inclusão em `processing`.
3. O incremento de `attempts` e a criação da tentativa aberta com seu token.
4. A mudança para `processing` e a inclusão do histórico.

A leitura da cabeça captura ID e score na mesma resposta. Antes de comparar o snapshot, a reserva confere essa geração da fila: se o mesmo ID tiver voltado por retry ou reenfileiramento manual com uma nova posição, o candidato antigo perde sem escrita ou consumo de tentativa. Posição inválida e alterações no snapshot da mesma entrada continuam sendo erros. Se outro worker tiver assumido o candidato, a operação não altera dados e o worker busca outro. A função só é invocada após a confirmação da reserva.

### Disputa entre deleção e execução

- Se a deleção vencer, o job passa a `deleted` e sai dos índices de pendentes; a reserva deixa de ser possível.
- Se a reserva vencer, o job passa a `processing`; a deleção retorna conflito.

Assim, depois de confirmada uma deleção, aquele job não pode começar a executar sem um reenfileiramento manual posterior.

### Confirmação de resultado

A conclusão verifica o estado e o token da tentativa antes de fechar seu registro. Repetir uma confirmação não pode duplicar o histórico nem modificar uma tentativa de outro ciclo.

As consultas de detalhe obtêm uma visão consistente entre estado, contador, histórico e tentativas usando o script de leitura atômica.

## Redis local

O storage escolhido para os jobs é Redis. O [`compose.yaml`](../compose.yaml) usa a imagem oficial
`redis:8.2.10-alpine`, com montagem bind persistente em `/data`, AOF habilitado e
`appendfsync always` para sincronizar as escritas em disco antes de responder.
A política `noeviction` impede a remoção automática de chaves por pressão de memória.
Referência: [persistência do Redis](https://redis.io/docs/latest/operate/oss_and_stack/management/persistence/).

Para dados existentes em volume nomeado, execute primeiro o [procedimento de preservação](#preservar-um-volume-nomeado-existente). Na raiz do projeto, com Docker e Docker Compose disponíveis, inicie o serviço:

```bash
mkdir -p ./data
docker compose up -d --wait redis
docker compose exec redis redis-cli ping
```

O comando de ping deve retornar `PONG`. O serviço fica disponível apenas em
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
mkdir -p "$PWD/data/dados locais"
DATA_DIR="$PWD/data/dados locais" REDIS_PORT=6380 docker compose up -d --wait redis
DATA_DIR="$PWD/data/dados locais" REDIS_PORT=6380 docker compose config
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

## Durabilidade e retenção

Os pedidos são aceitos somente após confirmação da operação persistente. `appendfsync always` sincroniza o AOF a cada escrita e aumenta o custo de I/O. Jobs, históricos e recibos não têm TTL nem retenção automática. Os recibos e a geração do executor que protegem confirmações ambíguas estão descritos em [falhas de infraestrutura](processamento.md#falhas-de-infraestrutura).

## Documentação relacionada

- [Processamento](processamento.md): coordenação das transições e tratamento de falhas.
- [Execução local](execucao-local.md): inicialização da aplicação e reinício.
- [Testes e validação](testes-e-validacao.md): evidências de persistência e concorrência.
