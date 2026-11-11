# Orientações para trabalhar no BackgroundJobs

Estas instruções se aplicam a todo o repositório.

## Documentos de referência

| Arquivo | Responsabilidade | Regra de manutenção |
| --- | --- | --- |
| [docs/spec-base.md](docs/spec-base.md) | Requisitos funcionais, escopo e critérios de aceite. | Imutável. Não editar, reformatar ou alterar requisitos. |
| [docs/arquitetura.md](docs/arquitetura.md) | Visão geral técnica, componentes, responsabilidades e principais decisões arquiteturais. | Manter a visão geral coerente com a implementação e apontar para os detalhes nos documentos temáticos. |
| [Documentos temáticos em docs/](README.md#documentação) | Guias de execução, build e uso; contratos, processamento, persistência, API HTTP, dashboard e validação. | Atualizar o documento responsável pelo assunto conforme o código, respeitando a spec e evitando duplicação. |
| [docs/plano-desenvolvimento.md](docs/plano-desenvolvimento.md) | Planejamento macro, metodologia, status, objetivos e dependências das etapas. | Atualizar quando houver progresso de uma etapa ou mudanças de direção, escopo, dependências ou metodologia. |
| `.development/` | Refinamentos, ações e materiais temporários de execução de cada etapa. | Criar quantos arquivos forem necessários e limpar os materiais da etapa ao concluí-la. Diretório ignorado pelo Git. |
| [README.md](README.md) | Visão geral do projeto, instalação, execução e uso básicos, além do índice da documentação. | Manter os primeiros passos reproduzíveis e o índice atualizado; encaminhar detalhes aos documentos temáticos. |

Antes de implementar, consultar a spec, a arquitetura, a etapa atual do plano e os documentos temáticos relacionados à mudança. O índice no README orienta essa consulta. Distinguir funcionalidades implementadas de trabalho planejado. Dúvidas ou incompatibilidades com a spec devem ficar explícitas no documento pertinente, sem modificar a spec para acomodar a implementação.

## Estilo e atualização da documentação

- Documentar apenas o estado atual: decisões vigentes, comportamento disponível, trabalho planejado e pendências atuais.
- Atualizar diretamente as seções afetadas. Remover informações que deixaram de se aplicar e manter os documentos coerentes entre si.
- Cada assunto detalhado tem um documento responsável. Nos demais documentos, usar sínteses e links; manter exemplos completos e procedimentos no guia correspondente. Ao reorganizar conteúdo, conferir links e preservar as âncoras referenciadas por documentos que não forem alterados.
- Não usar estilo de changelog, deltas, entradas cronológicas, comparações de antes/depois ou referências a estados anteriores da documentação ou da implementação.
- Explicar a justificativa da solução vigente quando isso ajudar a entendê-la, sem narrar alternativas abandonadas ou o histórico de alterações.
- Manter evidências e informações do estudo exigidas pela spec como síntese atual e referências aos artefatos pertinentes, sem transformá-las em changelog.
- A regra de documentação não elimina os históricos de jobs e tentativas exigidos pelo domínio.

### Independência do planejamento

- A spec define os requisitos. Código e testes representam o comportamento implementado e sua verificação. Documentação permanente descreve esse comportamento, suas garantias, limitações e decisões vigentes.
- Identificadores de etapas, tasks e checklists de execução ficam restritos a `docs/plano-desenvolvimento.md` e aos materiais temporários ativos em `.development/`.
- README, documentação técnica, código, comentários, nomes de testes, mensagens de erro e exemplos devem usar termos do domínio e referências a componentes ou comportamentos, sem vinculação a identificadores do planejamento, inclusive de trabalho futuro.
- Durante a implementação, atualizar diretamente a documentação afetada com base no código. Não aguardar o encerramento da etapa para retirar referências ao refinamento.
- Ao consolidar uma decisão ou evidência, preservar seu conteúdo útil e sua justificativa, sem transportar a identificação da task, a narrativa da execução ou dependências dos materiais temporários.
- Descrever pendências pelo comportamento ainda indisponível. Sua distribuição entre etapas e tasks pertence ao plano.
- Referências a requisitos e critérios da spec continuam permitidas, pois pertencem ao contrato permanente do projeto.

### Destino dos artefatos de validação

- Scripts exploratórios, roteiros, matrizes, checklists e relatórios produzidos para apoiar a execução ficam em `.development/`.
- Verificações necessárias à regressão integram as suítes de testes existentes.
- Instruções reproduzíveis, limitações e sínteses de evidências ficam nos documentos permanentes responsáveis pelo assunto, evitando duplicação.
- Um script ou documento auxiliar permanente precisa atender a um uso recorrente concreto, com responsabilidade própria e manutenção definida. A possibilidade de reutilização futura é insuficiente.
- Preservar evidências não exige preservar os arquivos usados para produzi-las. Ao concluir o trabalho, consolidar o conteúdo necessário e remover os materiais temporários, sem promover automaticamente refinamentos a documentação permanente.

## Refinamento e execução das etapas

- Manter o plano de desenvolvimento no nível macro. Refinar ações, checklists, investigações e validações da etapa em `.development/`.
- Criar quantos arquivos forem úteis e organizar por etapa conforme necessário, com estrutura proporcional ao trabalho.
- Consultar os materiais existentes em `.development/` ao retomar uma etapa. Mantê-los suficientes para identificar o objetivo, as ações pendentes, os bloqueios e o próximo passo.
- Tratar os refinamentos como apoio temporário à execução. A spec define os requisitos; código e testes representam o comportamento implementado e sua verificação.
- Atualizar a documentação permanente apenas com informações duradouras pertinentes: visão geral e principais decisões na arquitetura, detalhes e instruções nos documentos temáticos, sínteses de evidências em `docs/testes-e-validacao.md`, primeiros passos e índice no README e situação ou mudanças macro no plano.
- Ao concluir uma etapa, preservar código, testes, pendências relevantes e evidências ou informações do estudo exigidas pela spec antes de limpar seus materiais temporários.
- Limpar apenas os materiais da etapa concluída, preservando arquivos de outros trabalhos ou ainda necessários. Os refinamentos não precisam ser versionados ou arquivados após a conclusão.
- Aplicar também aos refinamentos textuais a regra de documentar o estado atual, sem changelog ou diário de alterações.

## Simplicidade de código

- Priorizar a solução mais simples que atenda aos requisitos e preserve a correção.
- Implementar o necessário para a entrega atual, respeitando as dependências do plano.
- Preferir código direto, nomes claros, funções coesas e responsabilidades explícitas.
- Criar abstrações, camadas e generalizações quando resolverem uma necessidade concreta; evitar estruturas para possibilidades futuras.
- Aproveitar as convenções e dependências existentes antes de introduzir novas ferramentas ou bibliotecas.
- Validar os comportamentos relevantes com testes proporcionais à mudança, incluindo persistência e concorrência quando afetadas.
- Manter as garantias exigidas de atomicidade, persistência e consistência ao simplificar a implementação.
