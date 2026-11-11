# Contratos e versionamento

[Início](../README.md) · [Arquitetura](arquitetura.md)

Este documento define a relação entre registros da aplicação, argumentos persistidos e compatibilidade de jobs após mudanças de código.

## Identidade e descritor persistido

O identificador representa um contrato estável de argumentos e comportamento. O sufixo `.v1` é uma convenção; a biblioteca não interpreta versões nem impõe esse formato. Os nomes obrigatórios da demo são os definidos na [spec](spec-base.md#42-jobs-da-demo).

O descritor identifica qual contrato executar e fornece seus dados. Ele não contém o código do método, sua assinatura CLR, nomes de assemblies, delegates, serviços de DI ou estado de closures. A aplicação fornece a implementação correspondente a cada inicialização.

Uma representação lógica do pedido é:

```json
{
  "name": "send-email.v1",
  "args": {
    "to": "pessoa@example.com"
  }
}
```

No Redis, os argumentos podem ocupar um campo do hash como string JSON. O storage trata esse conteúdo como dados opacos e não precisa conhecer o tipo C# dos argumentos.

A preparação interna disponível resolve o contrato pelo nome e tipo exato, serializa os argumentos, reconstrói `TArgs`, invoca o validador opcional e serializa a representação validada. O descritor contém somente nome e JSON independente dos objetos do chamador. A política controlada e imutável de `System.Text.Json` usa `camelCase` sensível a maiúsculas, rejeita campos desconhecidos e duplicidades inclusive aninhadas e não converte strings, frações ou notação decimal/exponencial em inteiros. Parâmetros obrigatórios de construtor são exigidos; parâmetros opcionais mantêm os padrões do contrato. Restrições de valores nulos são definidas pelo contrato e pelo validador.

O conteúdo Unicode bem formado é preservado sem normalização. Um conversor interno de strings rejeita UTF-16 com surrogates sem par usando `JobValidationException`, antes que a escrita JSON possa substituir o conteúdo. A regra vale para strings, valores aninhados e chaves de dicionários nas duas serializações da preparação, incluindo texto introduzido pelo validador. Para os dicionários de `[JsonExtensionData]` com valores `JsonElement` ou `object`, o resolvedor de metadados aplica a mesma checagem às chaves ao obter a propriedade para serialização. Essa escrita de nomes não usa o conversor de strings; a verificação no getter evita a substituição antes de produzir JSON, inclusive em objetos aninhados, sem percorrer o grafo de argumentos separadamente. `JobJsonNodeConverter` aplica a mesma verificação aos nomes e valores textuais da subárvore de `JsonNode` antes de delegar a escrita ao DOM nativo, pois `JsonObject`, `JsonArray` e `JsonValue` não usam o conversor de strings nessa escrita. A verificação respeita o limite de profundidade JSON e ocorre nas duas serializações, inclusive para nós presentes em dicionários ou introduzidos pelo validador. A leitura delega ao conversor nativo e exige uma forma compatível com o tipo declarado. Valores nulos continuam sujeitos ao contrato. JSON embutido em `JsonElement` segue as verificações nativas do serializador, que rejeitam escapes de surrogates sem par com `JsonException`. Não há captura em torno do validador que reclassifique suas exceções.

## Resolução e invocação

O worker confere o handler pelo nome antes da reserva e chama `JobHandlers.InvokeAsync` somente após a confirmação. O registro genérico constrói o adaptador com o conhecimento do tipo concreto dos argumentos:

```text
Registro pelo identificador do contrato
    → desserialização do JSON para TArgs
    → validação estrutural e de domínio
    → invocação e espera do handler com TArgs e JobContext
```

O contexto vem da tentativa persistida, não do JSON fornecido pelo cliente. Essa abordagem dispensa a descoberta da assinatura do método por reflexão em cada execução.

## Evolução de assinaturas e contratos

Um job armazenado será executado pelo código disponível no momento da execução. O descritor não preserva uma cópia da implementação original.

A assinatura C# e o contrato persistido são coisas diferentes. Uma alteração no método pode exigir ajuste do registro em tempo de compilação, sem mudar o JSON. Uma alteração nos argumentos pode permitir a compilação e falhar somente quando um pedido antigo for executado.

| Mudança | Consequência e regra |
| --- | --- |
| Renomear ou mover o método C#, mantendo o registro e o contrato | Compatível. |
| Adicionar campo opcional com padrão compatível | Pode manter o identificador, desde que a ausência preserve o comportamento esperado. |
| Adicionar campo obrigatório | Pedidos antigos não contêm o dado; exige novo contrato ou adaptação explícita compatível. |
| Renomear ou remover campo, ou trocar seu tipo | Pode causar erro ou perda silenciosa de valores; exige avaliar compatibilidade e criar novo contrato quando houver ruptura. |
| Alterar o significado de um campo | Pode executar incorretamente mesmo desserializando; também constitui quebra de contrato. |
| Remover identificador ainda referenciado | Deixa jobs sem handler, inclusive os que podem ser reenfileirados. |

Desserializar com sucesso não basta: campos ausentes ou desconhecidos podem passar despercebidos conforme a configuração adotada. Cada registro define uma política estável de serialização, os campos obrigatórios, os padrões permitidos e a validação dos valores. Uma atualização do DTO não pode tornar silenciosamente válidos argumentos que perderam seu significado.

Mudanças incompatíveis recebem outro identificador, sem redirecionamento automático de pedidos antigos para a versão mais recente. A versão está representada pelo próprio `name`; não é necessário acrescentar um campo público de versão ao contrato HTTP.

Por exemplo, a aplicação mantém `send-email.v1` associado a `SendEmailV1Args` e registra `send-email.v2` associado a `SendEmailV2Args`. Novas criações passam a usar `v2`; os jobs existentes continuam usando `v1` em agendamentos, retries, recuperação e reenfileiramento manual.

O handler de `v1` pode adaptar os argumentos para a implementação atual. Se `v2` exigir `templateId`, uma adaptação só é válida se houver uma regra de negócio para determinar esse dado, como um template legado compatível. A biblioteca não inventa valores nem converte contratos automaticamente.

## Validação, manutenção e migração

O delegate opcional de validação usa `Action<TArgs>`. Retorno normal aceita os argumentos; `JobValidationException`, pública e derivada diretamente de `Exception`, expressa rejeição esperada com mensagem legível. Valores inválidos das opções usam essa mesma exceção. Exceções inesperadas do validador, inclusive `ArgumentException`, `JsonException` e `NullReferenceException`, propagam como a mesma instância com mensagem e stack trace preservados. Não há captura genérica envolvendo desserialização e validação que as converta em entrada inválida. Falhas de configuração e infraestrutura mantêm sua classificação própria.

A demo define `LogMessageArgs(string Message)` e fornece seu validador: mensagem obrigatória, até 200 unidades UTF-16 (`string.Length`), string vazia permitida e conteúdo preservado sem trim ou normalização. A política do serializador exige a presença de `message` e seu tipo textual; o validador rejeita nulos e excesso de comprimento.

Aplicar validação em dois momentos:

1. **Criação:** verificar nome, compatibilidade do tipo/JSON, campos obrigatórios e regras dos argumentos antes de aceitar e persistir o pedido. Entrada HTTP inválida continua retornando `400`.
2. **Execução:** após reservar o job e antes de invocar a função, desserializar e validar de acordo com o contrato registrado. Uma falha fecha a tentativa com erro explícito e segue o limite e atraso de retry da spec, sem encerrar outros workers. A função não é chamada nesse caso e, portanto, não emite eventos de execução da demo.

O registro é concluído antes da inicialização do motor e permanece estável durante sua execução. Não há troca de handlers em tempo de execução.

A inicialização verifica todos os nomes referenciados no armazenamento. Se algum handler estiver ausente, o motor permanece indisponível e informa o nome e os jobs afetados; não libera workers, descarta pedidos nem consome tentativas para esse erro de configuração.

Finalizados também entram nessa verificação: `succeeded`, `failed` e `deleted` podem ser reenfileirados com nome e argumentos preservados. Esvaziar os pendentes não autoriza remover contratos antigos. Como não há retenção automática, sua manutenção pode ser necessária por tempo indeterminado.

A biblioteca não implementa migração automática de argumentos nem endpoints de migração. Se uma evolução futura exigir migração, ela deve ser explícita, verificável, preservar rastreabilidade e definir como lidar com jobs finalizados reenfileiráveis. Sem essa operação, a aplicação mantém os contratos e adaptadores antigos.

## Documentação relacionada

- [Uso da biblioteca](uso-biblioteca.md#registro-de-contratos): cadastro e API pública.
- [Processamento](processamento.md#inicialização-e-recuperação): verificação dos handlers.
- [Testes de compatibilidade](testes-e-validacao.md#compatibilidade-dos-contratos-persistidos): cenários verificados.
