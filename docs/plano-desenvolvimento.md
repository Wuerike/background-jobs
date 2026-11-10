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
- As pendências restantes estão resolvidas ou representadas no planejamento vigente, sem encobrir critérios não atendidos.

Uma etapa concluída não representa o aceite completo do projeto. A conclusão funcional depende de todos os critérios da spec.

### 2.4 Materiais temporários e limpeza

`.development/` é ignorada pelo Git e comporta os arquivos necessários à etapa. Seus materiais são efêmeros; o processo não depende de versioná-los nem de mantê-los após a conclusão.

Antes de limpar os materiais de uma etapa:

- Garantir que código e testes necessários à solução estejam nos diretórios permanentes do projeto.
- Incorporar decisões duradouras à arquitetura e instruções de uso ao README.
- Preservar, em artefatos duráveis, as evidências e informações do estudo exigidas pela spec.
- Representar pendências ainda relevantes no plano macro ou no refinamento da etapa responsável.

Remover apenas os materiais temporários da etapa concluída, preservando trabalhos em andamento e arquivos cuja finalidade ainda seja necessária. O encerramento da etapa inclui essa limpeza; não é necessário criar um arquivo histórico de refinamentos executados.

## 3. Mapa de entregas

| Etapa | Estado | Resultado esperado | Critério geral de conclusão | Dependências |
| --- | --- | --- | --- | --- |
| E1 — Criar e consultar | Concluída | Registrar uma função, validar e persistir pedidos, consultar seus dados pela biblioteca e HTTP. | ID retornado após persistência; entrada inválida rejeitada; dados e histórico inicial consultáveis. | Estrutura existente. |
| E2 — Executar um job | Planejada | Executar jobs com um worker, contexto, tentativas e eventos da demo. | Execução imediata demonstrável, com sucesso e falha terminal de uma tentativa; falha de uma função não encerra o worker. | E1. |
| E3 — Controlar concorrência | Planejada | Executar com quantidade configurável de workers e FIFO com um worker. | Critérios 3 e 4 da spec: ordem, exclusividade e limite de concorrência. | E2. |
| E4 — Agendar e repetir | Planejada | Executar agendamentos e retries com elegibilidade, atraso fixo e limite de tentativas. | Critérios 2, 5 e 6 da spec, incluindo horário passado e esgotamento. | E2 e E3. |
| E5 — Gerenciar o ciclo | Planejada | Listar, deletar pendentes e reenfileirar finalizados, preservando históricos. | Critério 7 da spec, incluindo conflitos e consistência das operações concorrentes. | E4. |
| E6 — Parar e recuperar | Planejada | Preservar dados, parar com segurança e recuperar tentativas interrompidas. | Critérios 8, 9 e 10 da spec em processos separados e com os mesmos dados; configuração de persistência reproduzível. | E5. |
| E7 — Validar indisponibilidade e evolução | Planejada | Manter consistência durante falhas de infraestrutura e compatibilidade dos contratos persistidos. | Cenários de infraestrutura e compatibilidade da arquitetura atendidos, incluindo confirmações ambíguas e ausência de handlers. | E6. |
| E8 — Fechar a entrega | Planejada | Entregar biblioteca, demo completa, exemplo sem HTTP e documentação reproduzível. | Todos os critérios de aceite e contratos HTTP verificados; evidências e informações do estudo disponíveis. | E1 a E7. |

Os critérios são resumidos; a spec e a arquitetura fornecem os requisitos completos. O refinamento de execução define os cenários concretos necessários a cada etapa.

A E1 está concluída: registro tipado, validação, criação persistente e consulta no Redis, além de criação e detalhe por HTTP na demo, estão implementados e cobertos por testes unitários, de integração e HTTP. A síntese das evidências e dos limites está na seção [Validar do README](../README.md#validar). A próxima etapa é E2 — Executar um job, que deve ser refinada em `.development/` antes da implementação. Execução de handlers e demais comportamentos do motor permanecem planejados.

## 4. Manutenção do plano macro

Atualizar este documento quando houver mudança de status, objetivo, escopo, prioridade, dependência, risco que afete o projeto ou metodologia de desenvolvimento. Detalhar ações, atualizar checklists e escolher assinaturas ou estruturas internas faz parte do refinamento em `.development/`.

Etapas podem ser adicionadas, removidas, divididas, combinadas ou reordenadas conforme necessário. Preservar a cobertura dos requisitos e manter os identificadores das etapas existentes quando possível, para facilitar as referências durante o trabalho.

Documentar apenas o estado atual, substituindo informações que deixaram de se aplicar. Manter arquitetura e plano coerentes com a spec imutável. Evitar duplicar informações de implementação ou depender de links para arquivos temporários que serão removidos.

Não definir estimativas fechadas para todo o projeto sem evidências do ritmo de desenvolvimento. Quando uma previsão for útil, identificá-la como estimativa e mantê-la atualizada.

## 5. Definição de pronto do projeto

O projeto está funcionalmente pronto quando atende aos critérios de aceite e contratos da spec. A entrega inclui biblioteca, demo, exemplo de uso sem HTTP, instruções reproduzíveis de instalação, execução, teste e reinício, além de evidências e limitações explícitas.

As informações do estudo exigidas pela spec incluem ferramentas e modelos, referências às conversas e artefatos, esforço humano aproximado, decisões, retrabalhos e tokens/custo quando disponíveis. Manter uma síntese atual e artefatos duráveis pertinentes, independentemente da limpeza de `.development/`. Identificar estimativas e informar dados não medidos.

O trabalho de criação e consulta no Redis e da fronteira HTTP usa Codex, Bash, .NET SDK, xUnit, Microsoft.AspNetCore.Mvc.Testing, StackExchange.Redis, Lua e Redis real, com curl e Python na demonstração HTTP, sem delegação a outros agentes. Modelo exato, tokens, custo e tempo humano não foram medidos nessas implementações. Os artefatos duráveis são o código, as suítes de teste e a documentação do projeto; as decisões técnicas estão nas seções 5.4 e 13 da arquitetura e a síntese das evidências está no README. As conversas no Codex são a fonte das solicitações e da execução; sua exportação integral e a consolidação dos dados humanos do estudo permanecem pendências da E8.
