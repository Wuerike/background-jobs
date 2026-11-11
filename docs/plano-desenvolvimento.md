# Plano de desenvolvimento — BackgroundJobs

## 1. Objetivo e responsabilidades

Organizar o desenvolvimento em entregas incrementais, com visão geral do projeto, status, dependências e critérios de conclusão. Ajustar o plano conforme as necessidades concretas da implementação, preservando os requisitos da spec.

| Referência | Responsabilidade |
| --- | --- |
| [Especificação funcional](spec-base.md) | Requisitos, escopo e critérios de aceite. Imutável. |
| [Arquitetura](arquitetura.md) | Solução técnica e decisões vigentes. |
| Este plano | Metodologia, estratégia de entrega e situação das etapas. |
| `.development/` | Refinamentos e materiais temporários necessários à execução de cada etapa. |
| Código e testes | Comportamento implementado e sua verificação. |
| [README](../README.md) | Instruções de instalação, execução e uso disponíveis. |

O plano permanece no nível macro. Tarefas, checklists, investigações e escolhas de implementação da etapa ficam em `.development/`. A demo mínima faz parte das primeiras entregas para permitir observação do comportamento desde cedo.

## 2. Metodologia de desenvolvimento

### 2.1 Seleção e refinamento

Selecionar a próxima etapa considerando suas dependências e os riscos atuais. Refinar o trabalho necessário em `.development/`, consultando a spec, a arquitetura e o código existente.

Criar quantos arquivos forem úteis à etapa: plano de execução, ações menores, investigações, roteiros de validação, scripts experimentais ou outros materiais de apoio. Organizar por etapa quando isso facilitar o trabalho, por exemplo `.development/e1-criar-consultar/`. A estrutura deve ser proporcional à necessidade, sem exigir um conjunto fixo de arquivos.

O refinamento deve permitir entender o objetivo, as ações pendentes, a situação da execução e como validar a entrega. Ele é temporário e serve para executar e retomar o trabalho; o comportamento implementado é verificado no código e nos testes.

### 2.2 Execução e validação

1. Confirmar o objetivo e as dependências da etapa no código atual.
2. Refinar o escopo, as ações e a validação em `.development/`.
3. Implementar em mudanças pequenas e revisáveis, prezando pela simplicidade.
4. Validar os comportamentos relevantes e demonstrar o resultado integrado.
5. Consolidar decisões e informações duradouras nos locais pertinentes.
6. Atualizar o status e os impactos macro no plano.
7. Limpar os materiais temporários da etapa concluída.

Resolver dúvidas com investigações proporcionais ao risco. Garantias de atomicidade, persistência e consistência acompanham as operações desde sua implementação; a validação final verifica o conjunto da solução.

### 2.3 Estados e conclusão

Usar `planejada`, `em andamento`, `bloqueada` e `concluída` no mapa de entregas. Um bloqueio deve indicar a dependência que impede o avanço e a condição necessária para retomá-lo.

Uma etapa está concluída quando:

- O resultado esperado é demonstrável e seus critérios de conclusão estão atendidos.
- As verificações relevantes passaram e o comportamento está representado no código e nos testes adequados.
- Limitações e pendências atuais estão explícitas nos locais pertinentes.
- As decisões arquiteturais e instruções de uso afetadas estão atualizadas.
- Os artefatos permanentes descrevem comportamentos e decisões sem referências a etapas, tasks ou checklists temporários, conforme a regra de independência do planejamento no [AGENTS.md](../AGENTS.md#independência-do-planejamento). As evidências estão preservadas por requisito ou comportamento antes da remoção dos materiais da etapa.
- Cada artefato auxiliar de validação mantido fora de `.development/` tem finalidade duradoura identificável, conforme a regra de [destino dos artefatos de validação](../AGENTS.md#destino-dos-artefatos-de-validação).
- As pendências restantes estão resolvidas ou representadas no planejamento vigente, sem encobrir critérios não atendidos.

Uma etapa concluída não representa o aceite completo do projeto. A conclusão funcional depende de todos os critérios da spec.

### 2.4 Materiais temporários e limpeza

`.development/` é ignorada pelo Git e comporta os arquivos necessários à etapa. Seus materiais são efêmeros; o processo não depende de versioná-los nem de mantê-los após a conclusão.

Antes de limpar os materiais de uma etapa:

- Garantir que código e testes necessários à solução estejam nos diretórios permanentes do projeto.
- Incorporar decisões duradouras à arquitetura e instruções de uso ao README.
- Consolidar a síntese das evidências e informações do estudo exigidas pela spec nos documentos responsáveis pelo assunto, sem exigir a preservação dos arquivos temporários usados para produzi-las.
- Representar pendências ainda relevantes no plano macro ou no refinamento da etapa responsável.

Remover apenas os materiais temporários da etapa concluída, preservando trabalhos em andamento e arquivos cuja finalidade ainda seja necessária. O encerramento da etapa inclui essa limpeza; não é necessário criar um arquivo histórico de refinamentos executados.

## 3. Mapa de entregas

| Etapa | Estado | Resultado esperado | Critério geral de conclusão | Dependências |
| --- | --- | --- | --- | --- |
| E1 — Criar e consultar | Concluída | Registrar uma função, validar e persistir pedidos, consultar seus dados pela biblioteca e HTTP. | ID retornado após persistência; entrada inválida rejeitada; dados e histórico inicial consultáveis. | Estrutura existente. |
| E2 — Executar um job | Concluída | Executar jobs imediatos com um worker, contexto, reserva e conclusão persistentes, eventos da demo e indicação de disponibilidade. | Critério 1 da spec; sucesso e falha terminal com `maxAttempts=1`, históricos consistentes e continuidade após erro de uma função. Falhas com orçamento restante voltam a `scheduled`, elegíveis após o atraso fixo. | E1 concluída. |
| E3 — Controlar concorrência | Concluída | Configurar a quantidade de workers pela biblioteca e por `WORKER_COUNT` na demo, com padrão 4, FIFO com um worker e ciclo do servidor coordenado entre workers. | Critérios 3 e 4 da spec: ordem FIFO, sobreposição real, exclusividade e limite de concorrência; configuração validada, parada e falhas de infraestrutura coordenadas sem regressão da execução imediata. | E2 concluída; fila sequenciada e reserva atômica disponíveis. |
| E4 — Agendar e repetir | Concluída | Promover agendamentos elegíveis e executar retries com atraso fixo e limite de tentativas, integrados ao ciclo do servidor e à fila existente. | Critérios 2, 5 e 6 da spec, incluindo horário passado, latência, esgotamento e preservação de FIFO, exclusividade, históricos e limite de workers. | E2 e E3 concluídas. |
| E5 — Gerenciar o ciclo | Concluída | Listar todos os jobs, deletar logicamente pendentes e reenfileirar finalizados pela biblioteca e HTTP, preservando identidade e históricos. | Critério 7 e contratos HTTP de gerenciamento: contador reiniciado por ciclo, FIFO, conflitos, snapshots e consistência em disputas com workers, scheduler e outras operações manuais. | E4 concluída; índices persistidos, ciclos e tokens disponíveis. |
| E6 — Parar e recuperar | Concluída | Recuperar tentativas interrompidas antes de liberar o motor, preservar a parada coordenada e configurar os dados locais por `DATA_DIR`. | Critérios 8, 9 e 10 da spec em processos separados, reinício com os mesmos dados, recuperação retomável sem duplicar históricos e persistência reproduzível sem perda dos dados existentes. | E5 concluída. |
| E7 — Validar indisponibilidade e evolução | Concluída | Reconciliar confirmações ambíguas, suspender e retomar o processamento após indisponibilidade transitória e preservar contratos de jobs existentes durante evolução da aplicação. | Falhas antes/depois da escrita e respostas tardias verificadas; retomada sem reinvocar funções por erro de confirmação, duplicar transições ou recuperar funções ainda ativas; parada prevalece sobre retomada; cenários de compatibilidade da arquitetura demonstrados com dados persistidos. | E6 concluída; identidades, snapshots, ciclos, tokens e recuperação disponíveis. |
| E8 — Fechar a entrega | Planejada | Entregar biblioteca, demo completa, exemplo sem HTTP e documentação reproduzível. | Todos os critérios de aceite e contratos HTTP verificados; evidências e informações do estudo disponíveis. | E1 a E7. |

Os critérios são resumidos; a spec e a arquitetura fornecem os requisitos completos. O refinamento de execução define os cenários concretos necessários a cada etapa.

A E1 está concluída: registro tipado, validação, criação persistente e consulta no Redis, além de criação e detalhe por HTTP na demo, estão implementados e cobertos por testes unitários, de integração e HTTP. A síntese das evidências e dos limites está na seção [Validar do README](../README.md#validar).

A E2 está concluída: invocação do contrato persistido, reserva e conclusão atômicas, um worker e ciclo do servidor estão implementados pela biblioteca, com adaptador de hosting, três funções da demo, eventos e `/health`. O critério 1, falha terminal, continuidade, parada coordenada e diagnóstico de indisponibilidade estão verificados por testes unitários, Redis real e HTTP. A síntese das evidências e os comandos de reprodução estão no [README](../README.md#validar), e as informações do estudo estão na seção de definição de pronto deste plano. Os materiais temporários da etapa estão consolidados em código, testes e documentação permanente.

Uma falha só é terminal quando esgota `maxAttempts`; com orçamento restante, a tentativa fecha com erro e o job fica `scheduled`, com a elegibilidade calculada. Promoção de agendamentos, execução de retries e gerenciamento manual estão disponíveis; recuperação após queda, `DATA_DIR` e testes de parada ativa estão implementados na E6, com persistência, montagem e transferência verificadas. Reconciliação e retomada após confirmações ambíguas estão implementadas na E7.

### 3.1 E3 — Resultado e evidências

A E3 está concluída. A biblioteca configura uma quantidade fixa de workers por servidor, com padrão 4, e a demo repassa `WORKER_COUNT` validado ao único executor. Cada loop mantém no máximo uma reserva/tentativa em andamento, sem busca antecipada ou fila local. FIFO com um worker usa a sequência persistida; a reserva Lua garante exclusividade. Inicialização única, prontidão, falha fatal global e parada são coordenadas, incluindo reservas e conclusões aguardando confirmação; a reconciliação de operações incertas integra a E7.

Os critérios 3 e 4 foram demonstrados com a aplicação real: mensagens A/B/C em ordem com um worker e seis esperas com pico 2. Configuração ausente demonstrou pico 4. Os eventos não mostram invocação duplicada; todos os detalhes terminam com uma tentativa e histórico completo. A regressão vigente, as versões do ambiente e os comandos reproduzíveis estão no README; os testes usam Redis real com AOF, `appendfsync always` e `noeviction`.

As regressões verificam limites e progresso, disputas e intercalamentos de leitura/reserva/conclusão, snapshots e índices, isolamento de erros de funções, primeira falha fatal preservada, cancelamento de esperas ociosas, drenagem de operações admitidas, chamadas de ciclo de vida repetidas e execução de pendentes por nova instância após parada limpa. A síntese das medições e os comandos reproduzíveis estão no [README](../README.md#validar); as decisões estão na [arquitetura](arquitetura.md). Código, testes e documentação permanente preservam o conteúdo necessário ao encerramento; os materiais temporários da E3 estão consolidados e removidos. Agendamento e retry integram o motor.

### 3.2 E4 — Resultado e evidências

A E4 está concluída. Agendamento e retry estão implementados e verificados. A promoção atômica preserva dados, opções e tentativas, retira `scheduledAt`, acrescenta histórico e atribui nova sequência FIFO. O scheduler independente dos workers usa seleção indexada em páginas de 128, comparação exata em UTC e polling ocioso de 100 ms. Candidatos antigos são rejeitados pela geração da entrada na fila; falhas e parada fecham a admissão e aguardam operações admitidas, sem duplicar transições durante a reconciliação de confirmações incertas.

Os critérios 2, 5 e 6 foram demonstrados em processos reais com Redis dedicado vazio por cenário: agendamento sem início antecipado, horário passado imediato, duas falhas e sucesso com atraso mínimo de 500 ms e esgotamento em três tentativas junto de job saudável. A síntese das medições e as instruções reproduzíveis estão no [README](../README.md#validar). As regressões incluem precisão temporal e datas extremas, múltiplos lotes, FIFO, limites e exclusividade entre retries, argumentos inválidos, preservação de snapshots, corrupção sem escrita parcial e coordenação de inicialização, indisponibilidade e parada.

Listagem, deleção e reenfileiramento manual estão disponíveis; recuperação e reinício/parada em processos separados estão implementados na E6, com montagem/transferência Docker verificadas; reconciliação e retomada após confirmações ambíguas estão implementadas na E7. O scheduler suspende o processamento em falhas transitórias e encerra a instância em falhas permanentes. A regressão e o build vigentes estão descritos no README. As evidências estão consolidadas no código, nos testes, no README e na arquitetura; os materiais temporários da E4 estão removidos. A próxima etapa é E8 — Fechar a entrega.

### 3.3 E5 — Resultado e evidências

A biblioteca oferece listagem de resumos, deleção lógica de `scheduled`/`enqueued` e reenfileiramento de `succeeded`/`failed`/`deleted`; a demo expõe as três rotas de gerenciamento obrigatórias. O gerente independe de catálogo, handler e motor. As mutações decidem ausência, conflito e sucesso atomicamente, preservam ID, contrato, argumentos, opções, criação e históricos e aguardam confirmação. Reenfileirar incrementa o ciclo de 64 bits com precisão exata, zera o contador e atribui nova posição FIFO.

A validação usa Redis real e intercalamentos controlados para seleção, reserva, promoção, conclusão e mutações simultâneas. Candidatos de entradas anteriores não revivem deletados nem alteram ciclos novos; a seleção valida o score atual e mantém o progresso quando descarta uma página completa. Snapshot alterado exige prova de transição válida; corrupção e confirmação incerta não são tratadas como conflito ou repetição automática.

O critério 7 foi demonstrado em processos reais da demo e Redis dedicado vazio: agendado deletado sem eventos após o horário original; reenfileiramento com tentativas `[1, 2, 1, 2]`, histórico e erros preservados; reenfileiramento dos outros estados finais; `409`/`404`, listagem coerente e continuidade do motor. A síntese e a reprodução estão no [README](../README.md#validar); contratos e decisões estão na [arquitetura](arquitetura.md). As regressões duráveis são `ManagerTests`, `ManagementTests`, `ManagementConcurrencyTests` e `ManagementHttpTests`, junto das suítes existentes.

A E5 está concluída. A regressão e o build vigentes estão descritos no README. Código, testes, README, arquitetura e este plano preservam as entregas e evidências úteis; os materiais temporários da etapa estão consolidados e removidos. A montagem/transferência Docker está verificada na E6; recuperação, `DATA_DIR` e reinício/parada em processos separados estão implementados; reconciliação de confirmações ambíguas e evolução de contratos estão implementadas na E7. A próxima entrega é E8 — Fechar a entrega.

### 3.4 E6 — Resultado e evidências

A E6 está concluída. A recuperação tem transição atômica por job, tentativa `interrupted`, orçamento/ciclo preservados, invalidação de conclusões antigas e retomada parcial antes da prontidão. Seleções e confirmações integram a barreira de parada. Falhas permanentes são fatais para a instância; confirmações transitórias incertas são reconciliadas pela política da E7.

Os critérios 8–10 foram verificados com processos reais da demo e Redis dedicado: SIGKILL, reinício com os mesmos dados, orçamento restante/esgotado, SIGTERM/SIGINT e drenagem de um/dois workers com pendente preservado. Uma função de 35 s atravessa a janela padrão de parada do host e termina com saída 0. Recuperação parcial e perda de confirmação são verificadas por barreiras/proxies sobre Redis real.

`DATA_DIR` configura o bind para `/data`, com padrão `./data`. Configuração padrão/alternativa, caminho com espaços, montagem efetiva, recriação, isolamento entre diretórios e transferência de volume nomeado descartável foram demonstrados com Docker Desktop 4.73.0, Engine 29.4.3 e Compose v5.1.3 pelo CLI Windows. A cópia integral com `cp -a` preservou UID/GID e modos conferidos (999:1000, diretório AOF 0700 e arquivos 0600), com manifesto e hashes de todos os arquivos iguais; destino leu o mesmo histórico e executou um job novo, e o retorno à origem confirmou seus IDs e dados sem a escrita do destino. Os testes de sinais usam processos Linux/WSL.

A regressão e o build vigentes estão descritos no README. Código, `recover.lua`, testes de recuperação/processos, README e arquitetura preservam resultados, decisões e evidências úteis. Os materiais temporários da E6 estão consolidados e removidos, preservando trabalhos alheios. Reconciliação geral de confirmações ambíguas, retomada automática após indisponibilidade e evolução dos contratos integram a E7 concluída. A próxima entrega é E8 — Fechar a entrega.

### 3.5 E7 — Indisponibilidade e evolução

A E7 está concluída. A biblioteca reconcilia criação, reserva, conclusão, promoção, recuperação, deleção e reenfileiramento com envelopes preservados, recibos por operação e geração do executor. Chamadas públicas têm política limitada; o motor suspende e retoma o mesmo conjunto fixo de workers sem reinvocar funções por perda de confirmação. Falhas permanentes continuam explícitas. Parada e cancelamento da inicialização prevalecem sobre reconexão, aguardando funções e mantendo diagnóstico quando o resultado não pode ser confirmado.

As regressões sobre Redis real incluem escrita não enviada, commit sem resposta, comando tardio, falha na consulta de confirmação, corrupção e avanço posterior de tentativas/ciclos. A geração rejeita comandos do predecessor, e o checksum verifica integridade dos recibos. A demo mantém o PID ao retornar o Redis dedicado com AOF; health, função ativa, pendentes, retry vencido e agendado futuro são conferidos. SIGTERM/SIGINT durante suspensão preservam pendentes para o sucessor. O diagnóstico de configuração é validado antes de iniciar o host.

A evolução usa DTOs e registros produtores/consumidores distintos, com método substituído, padrão opcional compatível, adaptação semântica explícita e coexistência de versões. Retry, recuperação e reenfileiramento preservam contrato e JSON. Ausência de handlers nos seis estados, múltiplos nomes e varredura superior a 128 referências bloqueiam processamento sem consumir tentativas. Registros sem recibos e geração são utilizáveis sem migração. Erros de contrato/validação têm orçamento e isolamento verificados.

O build aprova sem avisos ou erros e a suíte completa aprova 733 testes (220 unitários, 357 de integração e 156 HTTP, incluindo 11 de processos), sem falhas ou ignorados, com SDK 10.0.114 e Redis 7.0.15 real com AOF, `appendfsync always` e `noeviction`. Código, regressões, arquitetura e README preservam as decisões, evidências e reprodução; os materiais temporários da etapa estão consolidados e removidos. Migração automática, troca de handlers em execução, retenção e múltiplos executores permanecem fora do escopo. A E8 mantém somente a consolidação geral da entrega e os dados humanos do estudo.

## 4. Manutenção do plano macro

Atualizar este documento quando houver mudança de status, objetivo, escopo, prioridade, dependência, risco que afete o projeto ou metodologia de desenvolvimento. Detalhar ações, atualizar checklists e escolher assinaturas ou estruturas internas faz parte do refinamento em `.development/`.

Etapas podem ser adicionadas, removidas, divididas, combinadas ou reordenadas conforme necessário. Preservar a cobertura dos requisitos e manter os identificadores das etapas existentes quando possível, para facilitar as referências durante o trabalho.

Documentar apenas o estado atual, substituindo informações que deixaram de se aplicar. Manter arquitetura e plano coerentes com a spec imutável. Evitar duplicar informações de implementação ou depender de links para arquivos temporários que serão removidos.

Não definir estimativas fechadas para todo o projeto sem evidências do ritmo de desenvolvimento. Quando uma previsão for útil, identificá-la como estimativa e mantê-la atualizada.

## 5. Definição de pronto do projeto

O projeto está funcionalmente pronto quando atende aos critérios de aceite e contratos da spec. A entrega inclui biblioteca, demo, exemplo de uso sem HTTP, instruções reproduzíveis de instalação, execução, teste e reinício, além de evidências e limitações explícitas.

As informações do estudo exigidas pela spec incluem ferramentas e modelos, referências às conversas e artefatos, esforço humano aproximado, decisões, retrabalhos e tokens/custo quando disponíveis. Manter uma síntese atual e artefatos duráveis pertinentes, independentemente da limpeza de `.development/`. Identificar estimativas e informar dados não medidos.

O trabalho de criação, consulta, execução, concorrência e fronteira HTTP usa Codex, Bash, .NET SDK, xUnit, Microsoft.AspNetCore.Mvc.Testing, StackExchange.Redis, Lua e Redis real, com curl e Python nas demonstrações HTTP, sem delegação a outros agentes. O agente é identificado como GPT-6; versão exata do modelo, custo e tempo humano não foram medidos. A contabilização disponível da execução de concorrência está registrada abaixo. A medição demonstrativa de latência usa `time.monotonic()` do Python; o aceite HTTP usa `Stopwatch`. Os artefatos duráveis são o código, os scripts Lua, as suítes de teste e a documentação do projeto; as decisões técnicas estão na arquitetura e a síntese das evidências está no [README](../README.md#validar). As conversas no Codex são a fonte das solicitações e da execução; sua exportação integral e a consolidação dos dados humanos do estudo permanecem pendências da E8.

A referência do refinamento de concorrência é a conversa Codex `01a1c69f-1a6a-7b81-950f-fada8c28cb66`; a execução está nesta conversa, solicitada como “execute todas as tasks” do refinamento de concorrência. Os artefatos de verificação duráveis incluem `ConcurrencyTests`, `ConcurrencyProcessingTests` e `ConcurrencyHttpTests`, além das suítes existentes. A validação usa barreiras e contadores para controlar intercalamentos, proxies de teste sobre Redis real e configuração por host para preservar a composição da demo. O modelo conhecido é GPT-6. A contabilização da meta de concorrência registra 144.165 tokens e 862 segundos (14 min 22 s); esses dados são do contador da meta, não uma medição de esforço humano. Variante exata, esforço humano, custo e quantificação de retrabalho não foram medidos. Os tempos locais e picos observados estão no README, sem estimativas de dados indisponíveis.

O refinamento de agendamento/retry tem como referência a conversa Codex `01a1c701-fa6c-7182-9d0e-fd12193fa45a`; a execução foi solicitada como “execute todas as tasks” de `.development/e4-agendar-repetir`. Código, scripts Lua, `SchedulerTests`, `SchedulingTests` e `SchedulingHttpTests` preservam o resultado e a verificação; as evidências úteis da demonstração estão consolidadas no README e seus materiais temporários estão removidos. A execução utiliza as ferramentas e o modelo GPT-6 já identificados, sem delegação a outros agentes. A contabilização parcial disponível da meta de agendamento/retry é de 120.562 tokens e 835 segundos (13 min 55 s), referente ao contador da meta durante a validação, não ao esforço humano nem à duração total do estudo. Custo, esforço humano, duração total e quantificação de retrabalho não foram medidos; não há estimativa substituindo esses dados.

A execução do gerenciamento tem como referência a conversa Codex `01a1c74f-eec4-7453-9f18-8bff1d97c3c9`, solicitada como “execute todas as tasks” de `.development/e5-gerenciar-ciclo`. Usa GPT-6, Bash, Python, .NET SDK 10.0.114, xUnit, ASP.NET Core, StackExchange.Redis, Lua e Redis 8.2.10, sem delegação a outros agentes. A contabilização parcial disponível da meta registra 292.324 tokens e 968 segundos (16 min 8 s), referentes ao contador da meta durante a validação, sem representar esforço humano ou duração total do estudo. Custo, esforço humano e quantificação de retrabalho não foram medidos. Código, testes e documentação permanente preservam as decisões e evidências; roteiros e logs de execução são materiais temporários.

A implementação de parada/recuperação foi solicitada como “execute todas as tasks” de `.development/e6-parar-recuperar`, cujo refinamento tem referência na conversa Codex `01a1c76a-39a2-78f2-9569-e7e1b79e5694`; a execução está na conversa `01a1c7b7-98c8-7070-8897-242eae75c14a`. Usa Codex/GPT-6, Bash, Python, .NET SDK 10.0.114, xUnit, ASP.NET Core, StackExchange.Redis, Lua, Redis 8.2.10 e Docker Desktop 4.73.0, Engine 29.4.3 e Compose v5.1.3 para montagem, recriação e transferência, além de Compose standalone v5.7.0 para configuração, sem delegação a outros agentes. Código, scripts, testes de recuperação e de processos e documentação são os artefatos duradouros. A contabilização parcial disponível da meta é de 266.376 tokens e 2.646 segundos (44 min 6 s); refere-se ao contador da meta durante a validação, sem representar esforço humano nem duração total do estudo. Tempo humano, custo e quantificação de retrabalho não foram fornecidos. A configuração e a transferência Docker estão verificadas com dados descartáveis, incluindo leitura/execução no destino e retorno à origem; sinais e queda de funções são verificados em Linux/WSL. Os materiais temporários estão consolidados em código, regressões e documentação permanente.

A execução de indisponibilidade e evolução foi solicitada como “execute todas as tasks” de `.development/e7-indisponibilidade-evolucao`, na conversa Codex `01a1c813-4506-7ed1-8eff-55cb6de0d00a`. Usa Codex/GPT-6, Bash, Python, navegação no código oficial de StackExchange.Redis 2.9.32, .NET SDK 10.0.114, xUnit, ASP.NET Core, Lua e Redis 7.0.15 nativo extraído de pacotes Ubuntu, sem delegação a outros agentes. A suíte usa proxies determinísticos para separar envio/commit/resposta e processos Redis próprios para queda, AOF, reconexão e sinais. As decisões vigentes estão na arquitetura; `RedisTransportTests`, `ReconciliationTests`, `AvailabilityTests`, `ContractEvolutionTests` e `AvailabilityProcessTests` preservam as provas em código. A contabilização disponível da meta durante a consolidação registra 254.630 tokens e 3.529 segundos (58 min 49 s), referentes ao contador da meta, sem representar esforço humano ou duração total do estudo. Variante exata do modelo, custo, tempo humano e quantificação de retrabalho não foram medidos. O README contém comandos reproduzíveis, versões e a síntese das 733 verificações aprovadas; os materiais temporários estão consolidados em artefatos duráveis.
