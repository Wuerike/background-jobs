# Testes e validação

[Início](../README.md) · [Arquitetura](arquitetura.md)

Este documento reúne a estratégia de testes, os comandos de reprodução, a cobertura da spec e as evidências técnicas disponíveis. Os resultados registrados são das execuções descritas; os comandos partem da raiz do repositório.

## Preparar e executar as suítes

Para os testes de processos, instale também o Redis nativo no Ubuntu/WSL:

```bash
sudo apt-get update
sudo apt-get install -y redis-server redis-tools
redis-server --version
```

O executável de referência desta validação é Redis 7.0.15 dos pacotes Ubuntu 24.04; confira a versão efetivamente instalada. O executável é usado para lançar processos dedicados; nenhum serviço Redis do sistema precisa estar ativo. Um container Redis atende às suítes de prefixos, mas não fornece o executável Linux ao host. Para usar outra instalação nativa, informe seu caminho absoluto por `REDIS_SERVER_EXECUTABLE` e confira a versão; não é necessário extrair binários ou loaders de containers.

A suíte completa requer Linux/WSL, Redis acessível por `REDIS_CONNECTION` e um executável `redis-server` para os testes de processos dedicados. Configure `REDIS_SERVER_EXECUTABLE` quando esse executável estiver fora do PATH. A seção de testes de processos abaixo descreve o comando separado e a opção de executar os contratos HTTP em outras plataformas.

```bash
dotnet msbuild build.proj -t:Build
dotnet test BackgroundJobs.slnx --no-build
```

Para compilar no WSL enquanto ferramentas Windows restauram o mesmo checkout, use uma pasta de artefatos própria e reutilize a opção nos comandos:

```bash
dotnet msbuild build.proj -t:Build -p:ArtifactsPath=/tmp/backgroundjobs-build
dotnet test BackgroundJobs.slnx --artifacts-path /tmp/backgroundjobs-build --no-build
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

A cobertura está nos [testes unitários](../tests/BackgroundJobs.UnitTests), nos [testes de persistência e processamento](../tests/BackgroundJobs.IntegrationTests) e nos [testes HTTP](../tests/BackgroundJobs.HttpTests). Eles verificam contratos e validação, reserva exclusiva, conclusão atômica e idempotente, índices, snapshots consistentes, isolamento de erros e parada coordenada. O aceite HTTP cobre sucesso imediato com históricos e eventos, início em até 2 segundos com worker livre, falha terminal, continuidade, agendamento futuro/passado e retry até sucesso ou esgotamento. A execução pela biblioteca também é verificada sem HTTP.

Os testes HTTP de criação/consulta isolam o motor na composição de teste. Os testes de execução usam o motor ativo; os de configuração preservam a criação real do `JobServer` pelo `Program`, isolando configuração por host e chaves pela conexão de teste. Todos param o servidor antes da limpeza. Não há flag de produto para desativar o motor.

## Testes de processos

Os testes de processos requerem Linux/WSL, .NET e um executável Redis com AOF (7.0.15 na regressão completa; 8.2.10 nas medições demonstrativas). Criam processos, diretórios e portas exclusivos e nunca reiniciam o serviço indicado por `REDIS_CONNECTION`. Configure o caminho quando `redis-server` não estiver no PATH:

```bash
REDIS_SERVER_EXECUTABLE=/caminho/redis-server \
  dotnet test tests/BackgroundJobs.HttpTests --no-build --filter Category=ProcessLifecycle
# Contratos HTTP sem os testes de sinais/processos:
REDIS_CONNECTION=127.0.0.1:6380 \
  dotnet test tests/BackgroundJobs.HttpTests --no-build --filter 'Category!=ProcessLifecycle'
```

A suíte completa também executa os testes de processos; ausência do executável ou plataforma incompatível produz erro explícito, sem ignorar aceite. A configuração e a execução de montagem, recriação e transferência foram verificadas com Docker Desktop 4.73.0, Engine 29.4.3 e Compose v5.1.3 pelo CLI do Windows. `DATA_DIR` padrão e alternativo com espaços, AOF multipartes, isolamento e retorno à origem foram comprovados em recursos descartáveis. As provas de sinais e de queda durante funções usam processos Linux/WSL. Os critérios 1–10 da spec estão verificados; a garantia de efeitos externos permanece at-least-once sujeita ao orçamento.

## Estratégia de validação

### Camadas de testes

| Grupo | Objetivo |
| --- | --- |
| Unitários | Regras de transição, validação, limites, retry, recuperação e ciclos de tentativas. |
| Integração com Redis real | Atomicidade, persistência, índices, FIFO e disputas entre operações. |
| Aceite pela API | Contratos HTTP e comportamento observado nos eventos da demo. |
| Processos separados | Encerramento forçado, reinício com os mesmos dados e parada graciosa. |

Usar `TimeProvider` nos testes unitários de tempo. Os testes de aceite medem horários reais e respeitam a tolerância definida pela spec. Cada cenário começa com armazenamento isolado, exceto quando exige preservação e reinício.

As evidências observadas estão em [resultados e ambiente](#resultados-e-ambiente) e nas seções por comportamento abaixo.

### Cobertura dos critérios de aceite

| Critério da spec | Evidência esperada |
| --- | --- |
| 1. Execução imediata | Estado final, histórico de estados, tentativa e eventos de início/fim. |
| 2. Agendamento | Início nunca anterior a `runAt`; horário passado tratado como imediato; tolerância de início atendida. |
| 3. FIFO | Mensagens A, B e C observadas nessa ordem com um worker. |
| 4. Concorrência | Sobreposição de funções, máximo de dois workers e ausência de execução simultânea duplicada do mesmo job. |
| 5. Retry com sucesso | Tentativas 1, 2 e 3; duas falhas; sucesso; atraso mínimo contado do fim da tentativa anterior. |
| 6. Esgotamento | Três falhas do job problemático sem impedir o sucesso de outro job. |
| 7. Operações manuais | Deleção impede execução; reenfileiramento reinicia contador e preserva históricos; conflitos retornam `409`. |
| 8. Persistência | Jobs, argumentos, estados e históricos preservados após encerramento abrupto e reinício. |
| 9. Interrupção | Tentativa `interrupted`; nova execução apenas se houver orçamento; `maxAttempts=1` termina em `failed`. |
| 10. Parada | Função ativa aguardada; pendente não iniciado durante parada; execução do pendente após reinício. |

Além dos cenários, verificar corpos de erro, campos obrigatórios, opções padrão, UTC, consistência das consultas e histórico preservado em múltiplos reenfileiramentos.

### Compatibilidade dos contratos persistidos

A suíte `ContractEvolutionTests` verifica os seguintes cenários da arquitetura:

| Cenário | Resultado esperado |
| --- | --- |
| Renomear o método C# e preservar o registro | Job antigo continua executável com o mesmo descritor. |
| Agendar `v1`, implantar código com `v1` e `v2`, executar | Job antigo usa `v1`; novos pedidos podem usar `v2`. |
| Adaptar `v1` para a implementação atual | Conversão preserva o significado dos dados e os padrões definidos pelo contrato antigo. |
| Introduzir campo opcional compatível | JSON antigo continua válido e mantém o comportamento esperado. |
| Argumentos incompatíveis na criação | Pedido rejeitado antes de persistir; HTTP retorna `400`. |
| JSON persistido falha na desserialização ou validação | Handler não é chamado; tentativa registra erro e segue o retry; demais jobs continuam. |
| Ausência de handler referenciado na inicialização | Motor indisponível; nome e jobs afetados identificados; nenhuma tentativa consumida. |
| Handler ausente referenciado somente por job finalizado | Verificação também detecta a incompatibilidade com futuro reenfileiramento. |
| Reenfileirar job antigo com `v2` disponível | Nome e argumentos de `v1` preservados; contador reiniciado; históricos anteriores mantidos. |
| Retry ou recuperação de job antigo | Mesmo contrato usado novamente, sem redirecionamento para a versão mais recente. |

## Resultados e ambiente

A verificação por suíte aprova **769 testes** (226 unitários, 374 de integração e 169 HTTP, incluindo 11 de processos), sem falhas ou ignorados. Restore e build aprovam com SDK 10.0.114; o build não apresenta avisos nem erros. O ambiente é Ubuntu 24.04 em Linux/WSL, com Redis **7.0.15** dos pacotes oficiais Ubuntu: armazenamento dedicado com AOF na porta 16389 e processos isolados com portas próprias. As validações de Docker e as medições demonstrativas identificadas abaixo usam Redis **8.2.10**.

| Suíte | Aprovados | Duração reportada pelo runner |
| --- | ---: | --- |
| Unitários | 226 | 594 ms |
| Integração | 374 | 43 s |
| HTTP, incluindo processos e recursos estáticos | 169 | 1 min 53 s |

## Dashboard e consumidores

A entrega de CSS, módulos da interface e runtime Blazor é verificada em `Development` e `Production`, exigindo conteúdo e MIME type corretos. O monitoramento tem regressões em `MonitoringTests` e `DashboardHttpTests`: projeções e dados inválidos, seis estados, retry, paginação, filtros, ciclos com timestamps iguais, ausência de executor/catálogo, autorização, texto escapado e assets Blazor. A consulta de 1.000 jobs, compartilhada por três chamadas concorrentes, atende ao orçamento local de 2 segundos; alterações de estado preservam consistência por job. O registro da extensão preserva o gerente da aplicação e usa seu próprio prefixo configurado.

A interface integrada à Demo consumidora foi exercitada em Chromium via Playwright, em desktop e celular, com Redis real e os seis estados, incluindo retry e ciclos de reenfileiramento. Foram conferidos assets, circuito Blazor, links e filtros preservados, tentativas por ciclo e menu móvel sem transbordamento. A Demo publicada cria, executa e consulta jobs pela API e mostra os mesmos dados em `/dashboard/`. Uma aplicação externa publicada instala apenas o NuGet, registra `AddBackgroundJobsDashboard()` e mantém sua API própria, sem páginas Razor, propriedades extras ou mapeamentos de UI. O consumidor exclusivo do núcleo executa sem ASP.NET Core ou dashboard.

As regressões de integração do pacote verificam também a rota personalizada, a preservação de outras rotas e o login/política fallback do consumidor. A UI conserva tratamento de ausência, conflito, resultado incerto, conteúdo escapado, pausa e indisponibilidade; as ações continuam usando as operações atômicas da biblioteca. A auditoria axe-core das telas verificadas de visão geral, agenda e detalhes de falha, ciclos e interrupção não aponta violações.

Os contratos HTTP incluem a aceitação dos limites `delayMs=0/60000` e `failTimes=0/10` com persistência antes do `202`, sem executar o handler no teste de fronteira. O [exemplo console do guia de uso](uso-biblioteca.md#criar-executar-e-consultar-sem-http) também compila e executa fora do checkout, com referência apenas à biblioteca e sem HTTP, confirmando ID, argumentos, contexto e histórico por consulta pública. Os recursos Lua são carregados do assembly sem copiar `src/`.

## Execução HTTP e preservação de dados

A reprodução HTTP com um worker e Redis 8.2.10 confirmou health, criação, detalhe, listagem, eventos, deleção, reenfileiramento e erros `400/404/409`. O pedido imediato concluiu em 129,77 ms desde o início da criação até a consulta terminal; os intervalos de retry foram 615,175 e 626,009 ms para atraso de 500 ms. Foram registrados 13 eventos correlacionados por ID/tentativa. Recriar o Compose com o mesmo diretório de dados (caminho com espaços) preservou os detalhes completos de cinco jobs, incluindo argumentos, opções, estados, tentativas e históricos; outro diretório começou vazio, e o retorno ao primeiro manteve seu conjunto sem a escrita do diretório independente. Montagem em `/data` e configuração efetiva foram conferidas. Essas durações são observações técnicas locais e não medem esforço humano.

As suítes verificam os dez critérios da spec, incluindo processos reais para SIGKILL/SIGTERM/SIGINT, recuperação com ambos os orçamentos e drenagem de função de 35 s. A cobertura inclui limite e progresso dos workers, disputa pela mesma cabeça, FIFO com timestamps iguais, isolamento de erros de função, falha fatal global, espera por reservas/conclusões pendentes, inicialização e parada coordenadas. A integração verifica índices, snapshots e execução dos pendentes por nova instância após parada limpa.

## Indisponibilidade e reconciliação

As regressões de [reconciliação](../tests/BackgroundJobs.IntegrationTests/ReconciliationTests.cs) controlam falha antes do envio, commit sem resposta, comando tardio e perda da leitura de confirmação sobre Redis real. Conferem que cada criação, tentativa, transição e ciclo manual é aplicado uma vez por operação lógica, inclusive após avanço concorrente, e que corrupção não confirma sucesso. A [disponibilidade](../tests/BackgroundJobs.IntegrationTests/AvailabilityTests.cs) verifica repetidos episódios de suspensão, limite fixo de workers, diagnóstico, inicialização e cancelamento do host.

Os [cenários de processos durante indisponibilidade](../tests/BackgroundJobs.HttpTests/AvailabilityProcessTests.cs) mantêm o PID da demo, derrubam e reiniciam somente seu Redis dedicado e conferem health 503/200, conclusão da função ativa sem reinvocação, pendentes, retry vencido e agendado futuro. SIGTERM/SIGINT durante suspensão deixam o pendente para outra instância; uma tentativa cuja conclusão ficou sem confirmação é recuperada como `interrupted`. Os Redis dedicados usam AOF, `appendfsync always` e `noeviction`, e reiniciam com cache de scripts vazio.

Para reproduzir esses cenários com o build disponível e `redis-server` no PATH (ou `REDIS_SERVER_EXECUTABLE` configurado):

```bash
dotnet test tests/BackgroundJobs.HttpTests --no-build --filter 'FullyQualifiedName~Redis_returns_to_same_demo_process|FullyQualifiedName~Stop_during_redis_outage'
```

## Evolução de contratos

A [evolução de contratos](../tests/BackgroundJobs.IntegrationTests/ContractEvolutionTests.cs) usa dados gravados por outro registro/DTO: método substituído, campo opcional com padrão compatível, versões coexistentes e adaptador explícito que preserva o significado dos valores. Retry, recuperação e reenfileiramento mantêm o nome/JSON antigos; quebras de formato ou validação esgotam o orçamento sem chamar o handler nem bloquear jobs saudáveis. Ausência de handler nos seis estados, múltiplos nomes e mais de 128 referências bloqueia qualquer execução; o registro restaurado permite continuar. Dados sem os novos recibos e geração são lidos e executados sem migração. Mudança incompatível exige outro identificador; não há migração automática de argumentos ou troca de handlers em execução.

## FIFO e concorrência

A demonstração em processos reais usou um Redis dedicado vazio por cenário, com as mesmas configurações de persistência e portas isoladas:

| Configuração | Pedidos demonstrados | Pico observado | Eventos | Tentativas por job |
| --- | --- | --- | --- | --- |
| `WORKER_COUNT=1` | Mensagens A/B/C, nessa ordem | 1 | 9 | 1 |
| `WORKER_COUNT=2` | Seis esperas de 750 ms | 2 | 12 | 1 |
| Ausente (padrão 4) | Seis esperas de 750 ms | 4 | 12 | 1 |

Cada cenário confirmou detalhes `succeeded`, histórico completo e ausência de invocação duplicada. Pedidos adicionais com espera zero concluíram em **100,76 ms**, **93,20 ms** e **47,25 ms**, respectivamente, medidos desde o início da criação até a consulta final. O pico é calculado pela ordem de registro dos eventos, sem ordenar timestamps. Esses tempos são observações locais, sem garantia de tempo real.

## Agendamento e retry

A demonstração de agendamento e retry também usou processos reais da demo, um worker e Redis dedicado vazio por cenário, com AOF, `appendfsync always` e `noeviction`:

| Comportamento | Resultado observado |
| --- | --- |
| Agendamento três segundos no futuro | Início 25,49 ms após `runAt`, sem antecipação; `succeeded` com uma tentativa. |
| Horário passado | Criação até consulta terminal em 119,88 ms, com uma tentativa. Essa janela inclui o início. |
| Duas falhas e sucesso | Três tentativas; intervalos persistidos de 617,33 e 613,09 ms para atraso de 500 ms. |
| Esgotamento junto de mensagem saudável | Três falhas; intervalos de 620,12 e 610,97 ms; mensagem `succeeded` normalmente. |

Os eventos correlacionados por ID e tentativa confirmaram entrada e término, sem invocações duplicadas. Os detalhes preservaram argumentos, opções, erros e históricos completos, com `scheduledAt` nulo nos terminais. São medições locais, sem garantia de tempo real. As regressões duráveis estão em [SchedulingTests](../tests/BackgroundJobs.IntegrationTests/SchedulingTests.cs), [SchedulerTests](../tests/BackgroundJobs.UnitTests/SchedulerTests.cs) e [SchedulingHttpTests](../tests/BackgroundJobs.HttpTests/SchedulingHttpTests.cs). Elas incluem precisão de ticks e extremos de datas, promoção limitada com múltiplos lotes, FIFO de retries, orçamento, exclusividade e limite de workers, corrupção sem escritas parciais, candidatos de gerações antigas, parada/falha durante seleção e confirmação e continuidade após parada limpa.

## Operações manuais

O critério de operações manuais foi demonstrado com processos reais da demo e Redis dedicado vazio, um worker, AOF, `appendfsync always` e `noeviction`:

| Comportamento | Resultado observado |
| --- | --- |
| Deletar mensagem agendada três segundos no futuro | `200`, detalhe/listagem em `deleted`, nenhum evento até atravessar o horário original com margem de 250 ms. |
| Reenfileirar `fail-n-times` após sucesso | Mesmo ID, tentativas `[1, 2, 1, 2]`, prefixos dos históricos idênticos e erros preservados; intervalos de retry de 612,54 e 611,78 ms para atraso de 500 ms. |
| Reenfileirar `deleted` e `failed` | Execução do deletado com tentativa 1; falha terminal preservada e nova tentativa 1 no segundo ciclo do job esgotado. |
| Estados incompatíveis e ausência | `409` para nova deleção de `deleted`, deleção de finalizado/ativo e reenfileiramento de pendentes/ativo; `404` nas duas mutações para ID desconhecido. |
| Listagem estabilizada e continuidade | Cinco resumos coerentes com os detalhes, 20 eventos correlacionados e health operacional. |

São observações locais, sem garantia de tempo real. Para reproduzir, crie uma mensagem com `runAt` três segundos à frente, delete-a e consulte eventos depois desse horário; em seguida execute `fail-n-times` com `failTimes: 1`, `maxAttempts: 2`, `retryDelayMs: 500`, capture o detalhe final, reenfileire o mesmo ID e compare os prefixos de `history`/`attemptHistory`. As regressões duráveis estão em [ManagementTests](../tests/BackgroundJobs.IntegrationTests/ManagementTests.cs), [ManagementConcurrencyTests](../tests/BackgroundJobs.IntegrationTests/ManagementConcurrencyTests.cs), [ManagerTests](../tests/BackgroundJobs.UnitTests/ManagerTests.cs) e [ManagementHttpTests](../tests/BackgroundJobs.HttpTests/ManagementHttpTests.cs). Elas cobrem todos os estados, listagem paginada internamente e deduplicada, confirmação incerta com reconciliação sem efeitos duplicados, corrupção sem escrita parcial, ciclos de 64 bits, limites FIFO, snapshots, disputas controladas com reserva/promoção e progresso após descarte de uma página inteira de agendamentos.

As medições demonstrativas de execução, agendamento, operações manuais e recuperação apresentadas nesta seção usam Redis 8.2.10. Os testes de sinais executam processos nativos Linux/WSL; a versão de cada execução da suíte está identificada na síntese de resultados acima. Para reprodução, forneça um executável nativo pelo PATH ou por `REDIS_SERVER_EXECUTABLE`. O armazenamento dos testes de prefixos pode usar o Compose ou Redis externo equivalente, configurado por `REDIS_CONNECTION`.

## Recuperação e parada

Recuperação automática e configuração `DATA_DIR` estão implementadas. A inicialização verifica todos os handlers, inclusive de finalizados, e a associação dos índices antes de recuperar resíduos em lotes de até 128. Cada recuperação preserva dados/ciclo/históricos, fecha a última tentativa e invalida a posse da tentativa aberta, preservando recibos de conclusões já confirmadas. Corrupção e relógio anterior à atualização falham explicitamente; uma falha mantém o motor indisponível e preserva `LastError`. Recuperações já confirmadas permanecem e uma nova instância retoma as restantes. Falhas transitórias durante inicialização ou processamento permitem reconciliação e retomada automática. A retomada de comunicação não marca funções vivas como interrompidas. Recibos por operação e geração do executor protegem transições e comandos atrasados; os metadados não aparecem no JSON HTTP, não expiram e crescem com as operações e históricos. Continua obrigatório usar um executor por armazenamento.

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

São observações locais, sem garantia de tempo real. A regressão permanente está em [ProcessLifecycleTests](../tests/BackgroundJobs.HttpTests/ProcessLifecycleTests.cs), com processos controlados por [ProcessHost](../tests/BackgroundJobs.HttpTests/ProcessHost.cs). [RecoveryTests de integração](../tests/BackgroundJobs.IntegrationTests/RecoveryTests.cs) verificam ambos os orçamentos, FIFO, múltiplos ciclos, 270 resíduos em lotes limitados, deduplicação, metadados/índices corrompidos sem escrita parcial, tempo/sequência/ciclo extremos, confirmação antiga, recuperação parcial e resposta perdida. [RecoveryTests unitários](../tests/BackgroundJobs.UnitTests/RecoveryTests.cs) verificam checagens completas de handlers, ordem de inicialização, prontidão, falhas e parada com operações admitidas.

## Documentação relacionada

- [Execução local](execucao-local.md): preparação da demo e do Redis.
- [API HTTP](api-http.md#exemplos-de-uso): reprodução manual dos comportamentos.
- [Persistência Redis](persistencia-redis.md): configuração e preservação dos dados.
- [Processamento](processamento.md): garantias, falhas e limitações.
- [Plano de desenvolvimento](plano-desenvolvimento.md#5-definição-de-pronto-do-projeto): responsabilidade pelas informações do estudo.
