# Validação da E1 — criar e consultar

## Entrega e evidências

A E1 está concluída: a biblioteca registra contratos tipados, valida pedidos, cria jobs persistentes no Redis e consulta detalhes sem precisar do catálogo de contratos. A demo compõe esses serviços e expõe `POST /demo/jobs` para `log-message` e `GET /api/jobs/{id}`. O código não executa handlers nesta entrega.

As garantias da preparação e do armazenamento estão em [validação da E1.2](validacao-e1.2.md). A fronteira HTTP está representada em `tests/BackgroundJobs.HttpTests/JobHttpTests.cs`:

| Garantia | Evidência executável |
| --- | --- |
| Criação `202` e detalhe `200`, persistência após confirmação, argumentos estruturados, padrões e opções explícitas | `Creation_and_detail_match_direct_typed_use`: criação pelo endpoint, leitura imediata pela biblioteca e HTTP, comparação com criação tipada, histórico inicial e ausência de execução do handler cadastrado. |
| Pedido imediato, passado, igual ao relógio e futuro, com precisão e offset UTC | O mesmo teste usa relógio fixo e inclui uma data futura com offset e fração de sete dígitos. |
| Nomes exatos, campos obrigatórios, desconhecidos e duplicados; JSON malformado, tipos e Unicode inválidos | `Invalid_input_is_rejected_before_persistence`: inclui duplicidades após decodificar escapes e valores aninhados incompatíveis. |
| Ausência versus `null`, faixas, forma inteira, overflow e datas com fuso obrigatório | `Options_preserve_types_duplicates_and_absence_vs_null`: rejeita opções nulas, frações, expoentes, strings numéricas e datas inválidas ou sem fuso. |
| Mensagem vazia e limite de 200 unidades UTF-16 | `Message_length_uses_library_validation`, além dos testes unitários de argumentos e Unicode da biblioteca. |
| Rejeição declarada `400`; defeitos de validadores `500` sem detalhes internos | `Validator_bugs_remain_internal_errors`: `JobValidationException`, `ArgumentException`, `JsonException` e `InvalidOperationException`, sem jobs persistidos. |
| ID opaco inexistente `404` e leitura sem contrato da demo | `Opaque_unknown_id_returns_json_404` e `Lookup_needs_no_demo_contract_and_preserves_structured_args`. |
| Estados, resultados, campos nulos, ordem de tentativas e datas UTC no JSON | `Detail_serializes_states_outcomes_nulls_and_utc_dates`: snapshots de todos os estados e resultados, incluindo tentativa aberta. Esses snapshots não representam execução do motor. |
| Corrupção e falhas Redis são erros internos, sem falsa inexistência ou sucesso | `Corrupt_storage_returns_generic_500_instead_of_not_found` e `Redis_failures_return_generic_500_for_creation_and_lookup`. |

Os testes HTTP usam `WebApplicationFactory<Program>` em ambiente Development, a configuração JSON e os endpoints reais da demo, serviços reais da biblioteca e Redis real com prefixo exclusivo por teste. A fixture descarta os recursos e apaga somente suas chaves, sem `FLUSHDB`. Casos de serialização usam um gerenciador com snapshot explícito. Falhas Redis são provocadas por uma conexão descartada, sem parar ou reconfigurar o armazenamento compartilhado.

## Reprodução e resultado

Com SDK .NET 10 e Redis disponível, seguir os comandos do README. Para uma instância na porta 16379:

```bash
dotnet restore BackgroundJobs.slnx
dotnet build BackgroundJobs.slnx --no-restore
REDIS_CONNECTION=127.0.0.1:16379 dotnet test BackgroundJobs.slnx --no-build
PORT=18081 REDIS_CONNECTION=127.0.0.1:16379 dotnet run --project src/BackgroundJobs.Demo --no-build --no-launch-profile
```

A validação usa SDK 10.0.114 e Redis 8.2.10 dedicado, com AOF, `appendfsync always` e `noeviction`. O servidor Redis é executado a partir da distribuição oficial compilada em `/tmp`; Docker não está disponível neste ambiente. O Compose permanece a opção documentada para desenvolvimento local.

Resultado: build sem avisos ou erros; 118 testes unitários, 53 testes de integração e 86 testes HTTP aprovados, totalizando 257, sem falhas ou testes ignorados. `git diff --check` sem erros de whitespace; a spec permanece inalterada.

A demonstração executa o bloco de comandos Bash do README contra a aplicação real na porta 18081, com a composição padrão dos serviços e `REDIS_CONNECTION` apontando para 16379. Os dois IDs retornados são consultados: o pedido imediato apresenta `enqueued`, padrões 3/1000 e `scheduledAt: null`; o pedido uma hora no futuro apresenta `scheduled`, opções 2/500 e instante UTC correspondente. Ambos têm argumentos estruturados, uma entrada de criação e nenhuma tentativa.

## Limites e pendências

- Criação HTTP está disponível somente para `log-message`. Os outros dois jobs da demo e a execução de handlers pertencem às etapas do motor.
- Não há worker, promoção de agendamentos, retry, listagem, deleção, reenfileiramento ou eventos nesta entrega. `/health` permanece planejado.
- Agendamentos permanecem pendentes mesmo quando o instante vence. Persistência após queda, recuperação, parada e `DATA_DIR` pertencem à E6; indisponibilidade e confirmações ambíguas, à E7.
- A próxima etapa é E2 — Executar um job. O aceite funcional completo depende de E2 a E8.

## Informações do estudo

O escopo é a solicitação “implemente E1.3”. As ferramentas usadas são Codex, Bash, .NET SDK, xUnit, Microsoft.AspNetCore.Mvc.Testing, StackExchange.Redis, Redis real, curl e Python para executar a demonstração documentada. Não houve delegação a outros agentes. A identificação exata do modelo, tokens, custo e tempo humano não foram medidos nesta execução.

Os artefatos duráveis são o código da demo, a suíte HTTP, este documento, os testes e a evidência do armazenamento, o README, a arquitetura e o plano macro. As decisões vigentes da fronteira HTTP estão na seção 13 da arquitetura. A conversa no Codex é a fonte da solicitação e da execução; a exportação integral e a consolidação dos dados humanos do estudo permanecem pendências da E8.
