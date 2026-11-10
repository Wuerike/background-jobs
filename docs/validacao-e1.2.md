# Validação da E1.2 — criação e consulta no Redis

## Entrega e evidências

A biblioteca disponibiliza `RedisJobClient.EnqueueAsync` e `RedisJobManager.GetAsync`, sem execução de handlers. Os contratos do armazenamento estão representados nos testes permanentes abaixo. A E1 está concluída, com demonstração HTTP e evidências complementares em [validação da E1](validacao-e1.md).

| Garantia | Evidência executável |
| --- | --- |
| ID somente após confirmação; snapshot preparado; relógio lido uma vez; erros de preparação sem escrita | `tests/BackgroundJobs.UnitTests/ClientTests.cs`: confirmação controlada, mutação dos argumentos, opções inválidas, nome/tipo incompatível, Unicode inválido, ciclo de serialização e preservação das exceções do validador. |
| Falhas do armazenamento não viram sucesso ou inexistência | `ClientTests.Storage_failures_do_not_return_success_or_absence` e `RedisTests.Infrastructure_errors_propagate_without_modifying_shared_Redis`. |
| Criação e detalhe públicos sem handler, com padrões e limites | `RedisTests.Public_creation_and_detail_match_the_contract`: dados, estado, opções, horários, histórico inicial, ausência de tentativas e índices exclusivos. |
| Agendamento passado/igual/futuro, offsets e precisão | `RedisTests.Exact_dates_and_non_anticipating_scores` e `ClientTests.Score_rounds_up_without_losing_ticks_in_any_culture`: ticks, frações de milissegundo, extremos de data e cultura. |
| Preparação rejeitada não deixa estruturas parciais | `RedisTests.Invalid_preparation_leaves_no_keys`, com exceções esperadas, inesperadas e falha estrutural de serialização. |
| JSON estruturado, Unicode e independência dos objetos originais | `Public_creation_and_detail_match_the_contract`, `Structured_json_is_preserved_for_non_object_arguments` e `New_connection_reads_without_contracts_and_args_survive_disposal`. |
| Leitura sem catálogo, ID opaco, ausência e nova conexão | `New_connection_reads_without_contracts_and_args_survive_disposal` e `Opaque_ids_are_not_normalized`. |
| Ordem e unicidade dos scores FIFO, sem posição antecipada para agendados e sem TTL | `Fifo_sequence_ignores_scheduled_jobs_and_no_keys_expire`, `Last_exact_fifo_sequence_is_preserved_without_rounding` e `Parallel_creations_have_exclusive_indexes_and_complete_snapshots`. |
| Concorrência entre criação e consulta | `Concurrent_creation_reads_are_absent_or_complete`: somente inexistência ou detalhe completo. |
| Consistência do detalhe durante mudanças | `Concurrent_detail_never_combines_different_snapshots`: um escritor alterna snapshots completos atomicamente; estado, contador, históricos e tentativas lidos pertencem ao mesmo snapshot. |
| Repetição equivalente sem efeito e colisão sem sobrescrita | `Equivalent_retransmissions_are_no_ops_even_after_state_changes`: chamadas paralelas, histórico único, sequência única, estado preservado e comparação de todos os dados Redis via `DUMP`. |
| Precondições antes de escrita | `Wrong_key_types_are_rejected_before_any_write`, `Invalid_or_exhausted_sequence_leaves_no_partial_write` e `Missing_history_and_orphans_are_explicit_errors`: tipos de todas as chaves, contador inválido/esgotado e resíduos; comparação integral dos dados. |
| Corrupção distinta de inexistência | `Corrupt_hash_fails_instead_of_returning_partial_data` e `Missing_history_and_orphans_are_explicit_errors`: campos ausentes, JSON/estado/data/número inválidos, identidade incorreta e histórico ausente ou inválido. |
| Leitura de estados/resultados e históricos preservados | `Reader_preserves_all_states_outcomes_order_and_repeated_attempt_numbers`: todos os estados e resultados, erro, tentativa aberta e número repetido em outro ciclo, usando snapshots semeados. |
| Isolamento, configuração e limpeza | `Prefixes_isolate_data_and_invalid_configuration_is_rejected`; fixture usa prefixo único por teste e apaga somente suas chaves. |

## Reprodução

Com SDK .NET 10 e Redis iniciado:

```bash
dotnet restore BackgroundJobs.slnx
dotnet build BackgroundJobs.slnx --no-restore
REDIS_CONNECTION=127.0.0.1:16379 dotnet test BackgroundJobs.slnx --no-build
```

Use a porta real do seu Redis em `REDIS_CONNECTION`. O padrão dos testes é `127.0.0.1:6379`, compatível com o Compose documentado no README. A fixture exige Redis acessível e não ignora testes quando a infraestrutura está indisponível. Scripts são enviados por texto e não exigem pré-carregamento ou invalidação global de cache.

Resultado verificado: restore concluído; build com zero avisos e zero erros; **118 testes unitários e 53 testes de integração aprovados**, sem falhas ou testes ignorados. A suíte de integração completa executa em aproximadamente três segundos nesse ambiente, sem contar build e inicialização do runner. `git diff --check` não aponta erros de whitespace; a spec permanece sem alterações.

Ambiente de validação: SDK 10.0.114, Redis 8.2.10 em instância dedicada na porta 16379, AOF habilitado, `appendfsync always`, `maxmemory-policy noeviction`. Como Docker não está disponível no WSL desta sessão, o servidor usa a distribuição oficial compilada em `/tmp`, com Zig 0.14.1 e runtime libgcc 13.3; o código Redis não recebe alterações funcionais. O Compose permanece a forma documentada de preparar o ambiente local.

## Limites e pendências

- A leitura por nova conexão demonstra independência de memória local, sem provar recuperação após queda do processo ou reinício do Redis. Persistência com encerramento abrupto pertence à E6.
- A criação reconhece repetição equivalente no armazenamento, mas não há loop próprio de retry/reconciliação de rede. Confirmações ambíguas e falhas no meio das operações pertencem à E7.
- Os históricos de execução dos testes são semeados; workers, promoção de agendamentos, retry, deleção, reenfileiramento e recuperação ainda dependem das etapas responsáveis.
- `DATA_DIR` e a demonstração de reinício pertencem à E6. A integração HTTP está documentada na validação da E1.

## Informações do estudo

O escopo deste artefato é a criação e consulta no Redis, correspondente à solicitação “implemente a task E1.2”. O trabalho usa Codex, terminal Bash, .NET SDK, xUnit, StackExchange.Redis, scripts Lua e Redis real. Não houve delegação a outros agentes. A identificação exata do modelo, tokens, custo e tempo humano não foram medidos nesta execução.

Os artefatos duráveis são o código da biblioteca, os projetos de teste, este documento, a arquitetura, o README e o plano macro. As decisões implementadas estão na seção 5.4 da arquitetura: composição por construtor, conexão mantida pelo chamador, datas exatas separadas dos scores, identidade imutável de criação e scripts embutidos. As verificações preservam a classificação das exceções já produzidas pela preparação, incluindo `JsonException` de serialização estrutural e exceções inesperadas de validadores. A exportação da conversa integral e a consolidação dos dados humanos do estudo permanecem pendências da E8; a sessão de execução no Codex é a fonte da conversa.
