# Análise da metodologia de desenvolvimento com IA

Data da análise: 11/11/2026. Autor da análise: Codex. Objeto: o trabalho de Wuerike no BackgroundJobs como prática de desenvolvimento com AI/LLM.

## Avaliação geral

A metodologia combina **especificação estável, exploração assistida de decisões, planejamento incremental e delegação progressiva da implementação**. O desenvolvedor exerce principalmente os papéis de responsável pelo produto, arquiteto e revisor: escolhe os limites, questiona propostas, pede evidências e corrige a direção. O agente assume grande parte da implementação, dos testes e da documentação.

O ponto mais forte é a capacidade de transformar problemas percebidos no trabalho em regras de processo: separação entre planejamento e documentação permanente, critérios de simplicidade, limpeza de materiais temporários e revisão cruzada de código, testes e requisitos. O principal ponto a melhorar é **encurtar o caminho entre uma decisão e uma demonstração real do resultado**, especialmente da experiência de instalação e uso. Há bastante rigor em contratos e persistência, mas algumas incompatibilidades de produto só aparecem depois de uma implementação extensa.

Como prática de IA, o material evidencia participação crítica e aprendizado técnico. Como avaliação de produtividade, ele ainda é incompleto: faltam horas humanas, uma referência sem agente e uma medida de retenção do conhecimento. Quantidade de código, testes ou tokens não resolve essas lacunas.

O custo equivalente estimado é **US$ 35,69 em API Standard**, incluindo cache, saída e adicional de contexto longo. **Isso não comprova o valor efetivamente pago**: o export não contém fatura, modalidade de contratação ou consumo financeiro da conta. A memória de cálculo está abaixo.

## Fontes, recorte e confiabilidade

- [Export original do Tokenmetro](tokenmetro.json): formato `tokenmetro/v1`, exportado em `2026-11-11T13:29:39.806Z`.
- [Dashboard e apresentação](tokenmetro.html): dados embutidos e narrativa de cinco dias de desenvolvimento; a história declara geração em `2026-11-11T13:30:48.974Z` pelo agente `codex`.
- Histórico Git completo alcançável por `git log --all`, incluindo as referências de checkpoints dos agentes. Ponto de corte: `cb4347f6daa4b07e05d488b3493914a7b9200c24`.
- Logs locais do Codex das sessões presentes no export, incluindo arquivos de continuação. Foram examinados os eventos de uso, metadados e respostas finais relevantes às revisões, ao dashboard e à execução local. Não foi necessário copiar os logs para o repositório.
- Spec, instruções do repositório, documentação de validação e trechos históricos de código foram consultados para interpretar as evidências. A aplicação e sua documentação permanecem fora do escopo de alteração desta análise.

As referências **P001–P126** identificam a posição, começando em 1, no array `prompts` do JSON. São referências deste estudo, não identificadores do planejamento da aplicação. Cada registro também contém ID e timestamp para conferência.

A apresentação é uma síntese gerada por IA, útil para orientação, mas não constitui confirmação independente das entregas. O histórico confirma o que foi versionado; os prompts mostram intenções e intervenções; as respostas das sessões mostram o que o agente relatou. Quando o relatório menciona testes aprovados, trata-se da evidência histórica consultada: a suíte não foi executada novamente para produzir este documento.

### O que os números realmente representam

| Medida | Resultado | Interpretação |
| --- | ---: | --- |
| Sessões no export | 43 | Sessões registradas; não equivalem a 43 unidades de trabalho completas. |
| Registros em `prompts` | 126 | Incluem 15 mensagens internas de controle de metas e um comando `clear`. Restam 110 registros conversacionais. |
| Registros com zero tokens | 7 | Não devem ser tratados como respostas completas do modelo. |
| Chamadas ao modelo | 1.267 | Conferidas com incrementos dos contadores dos logs. |
| Chamadas de ferramentas no export | 1.148 | Volume de operações, não medida de qualidade. |
| Tokens totais | 141.618.855 | Entrada e saída; raciocínio já está contido na saída. |
| Commits alcançáveis por todas as refs | 39 | 20 na história de `main` e 19 checkpoints automáticos fora dela. |
| Linhas adicionadas/removidas no histórico | 17.535 / 2.202 | Churn acumulado; não tamanho final nem produtividade líquida. |
| Dias com registros | 5 | 26–27/10 e 09–11/11, em `America/Sao_Paulo`. |
| Soma de `duration_ms` | 16h53min aproximadamente | Tempo atribuído pelo coletor, não horas de trabalho humano nem computação ativa. |

Uma única execução, P076, concentra aproximadamente **7h59min** desse tempo. Sem reconstruir pausas, esperas e atividade efetiva, seria incorreto afirmar que houve 16h53min de trabalho humano ou que o modelo computou continuamente por esse período. Tampouco o intervalo sem registros entre outubro e novembro prova abandono ou inatividade fora das ferramentas monitoradas.

Todos os 126 campos `response` do export estão vazios. Os logs locais foram importantes para recuperar o resultado das revisões e as explicações das correções. O export informa zero subagentes; isso descreve a instrumentação disponível e não demonstra que toda revisão foi intelectualmente independente.

## Como o trabalho é conduzido

O ciclo predominante é:

```mermaid
flowchart LR
    A[Requisito e limite] --> B[Discussão de decisões]
    B --> C[Refinamento temporário]
    C --> D[Implementação e testes pelo agente]
    D --> E[Review e uso pelo desenvolvedor]
    E --> F[Correções e consolidação]
    F --> A
```

Há duas modalidades de interação bem distintas:

1. **Exploração acompanhada.** O desenvolvedor solicita exemplos, alternativas e explicações antes de aceitar uma API. Isso aparece na identidade dos jobs, evolução de assinaturas, validação opcional, separação entre contratos e handlers e leitura por `JsonElement` (P009–P011 e P019–P061).
2. **Execução delegada por resultado.** Com os contratos mais estabilizados, o desenvolvedor solicita o refinamento a partir do código existente e entrega conjuntos inteiros de ações ao agente (P083–P107). Os commits de execução, concorrência, agendamento, gerenciamento e recuperação acompanham essa divisão funcional.

Essa transição faz sentido: a supervisão é maior quando a decisão define o produto e diminui quando há um contrato mais claro para implementar. A fragilidade é que a autonomia também permite consolidar interpretações não verificadas. O dashboard ilustra isso: “projeto separado” e “extensão opcional” deixaram espaço para um host executável, enquanto a experiência desejada era instalar um pacote e fazer um registro de serviços.

## Pontos fortes

### 1. Questionamento de decisões que realmente afetam o domínio

As perguntas sobre mudança de assinatura entre agendamento e execução, produtor e consumidor em deployments diferentes e ausência do tipo concreto na consulta são decisões arquiteturais relevantes. Elas antecedem a implementação consolidada em `5e1bca2` e ajudam a explicar contratos estáveis, registro explícito e separação de responsabilidades.

Há evidência de aprendizado ativo: o desenvolvedor pede alternativas, examina consequências e muda de posição quando considera uma solução melhor. A reconsideração do delegate de validação em P025–P026 é um exemplo pequeno; a discussão dos contratos entre aplicações em P029–P031 é mais estrutural.

**Manter:** discutir invariantes e experiência pública da biblioteca com esse nível de atenção. Esses são pontos nos quais uma decisão humana bem fundamentada evita trabalho posterior.

### 2. O planejamento é incremental e considera o estado real

O desenvolvedor pede que o refinamento considere o código já entregue, em vez de executar um plano inicial como se ele permanecesse completo e correto. P012, P015–P017 e a sequência de refinamentos de P083 em diante mostram essa intenção.

O Git oferece evidência correspondente: execução, concorrência, scheduler, gerenciamento, recuperação e reconciliação aparecem em commits distintos. Isso facilita compreender dependências e revisar mudanças de comportamento.

**Manter:** planejar o próximo incremento com informação atual. Não há evidência de benefício em detalhar antecipadamente toda a implementação.

### 3. As revisões produzem achados verificáveis

As três revisões de P065, P068 e P070 relataram problemas concretos de preservação de UTF-16: strings, chaves de `JsonExtensionData` e árvores `JsonObject`, respectivamente. As respostas históricas registram builds aprovados e 84, 92 e 95 testes passando mesmo com essas lacunas.

O commit `5e1bca2` contém conversores de strings e `JsonNode`, além de `UnicodeTests.cs`, coerentes com os problemas identificados. Isso demonstra por que “os testes passaram” e “o contrato está atendido” precisam ser avaliações complementares.

**Manter:** exigir reprodução, comportamento esperado, comportamento observado e teste de regressão para cada achado. A instrução de confrontar código, docs e spec é uma das práticas mais eficazes deste histórico.

### 4. Correção do processo ao identificar desperdício

P081–P089 questionam relatórios permanentes, scripts auxiliares e referências a tarefas concluídas. P124–P126 tratam a documentação como informação organizada por responsabilidade. O resultado está visível em `8a603b0`, nas regras do repositório e em `cb4347f`.

O aspecto positivo não é apenas limpar arquivos: o desenvolvedor identifica o mecanismo que os produz e pede uma regra para evitar repetição. Isso é uma habilidade importante ao trabalhar com agentes que tendem a gerar documentação e scaffolding além da necessidade imediata.

### 5. Existe preocupação com correção além do caminho feliz

Os commits incorporam testes de concorrência, recuperação após encerramento, parada de processos, persistência e respostas ambíguas do Redis. No churn acumulado, `tests/` representa 7.480 linhas adicionadas, contra 5.026 em `src/`.

Esses volumes não provam qualidade por si, mas a variedade dos cenários é compatível com os riscos do domínio. A documentação registra 769 testes aprovados na entrega do dashboard, distribuídos entre unitários, integração e HTTP. É evidência histórica relevante, sem equivaler a uma auditoria atual de ausência de defeitos.

## Pontos a melhorar

### 1. Antecipar a experiência de consumo

**Evidência:** P112–P113 relatam um dashboard aparentemente sem estilo. A resposta local confirma que CSS e JavaScript estavam sendo entregues vazios. P114–P116 redefinem o consumo como NuGet com um registro de serviços. P118 mostra `make run` aguardando Redis ausente; a correção passa a prepará-lo e esperar prontidão.

**Avaliação:** o aceite técnico estava mais desenvolvido que o aceite da experiência real. A implementação do dashboard e sua reformulação acabaram reunidas em `b47ea63`; o Git isoladamente não revela toda essa iteração.

**Ação:** antes de completar uma extensão, implementar uma fatia mínima que seja instalada por um consumidor externo, publicada e aberta no navegador. Definir previamente o comando de execução, as chamadas públicas e a rota esperada. “Bonito” e “boa UX” precisam de exemplos observáveis: ações prioritárias, conteúdo, disposição e estados de erro.

### 2. Reduzir aprovações pequenas sem perder decisões importantes

**Evidência:** P034–P061 contêm várias perguntas sobre próximo item, destino de documentação, aprovação de tabelas e repetição de propostas. P046 e P061 repetem o pedido de revisão dos refinamentos. A instrução inicial “não tome decisões sozinho” favorece esse padrão.

**Avaliação:** esse acompanhamento tem valor pedagógico, mas cobra atenção e fragmenta o fluxo. Não é possível atribuir toda essa sequência a desperdício: parte dela é justamente a prática de raciocínio que motivou o projeto.

**Ação:** declarar o modo desejado por incremento: “exploração para aprender” ou “entrega com autonomia”. No segundo, delegar nomes internos, organização local e decisões reversíveis; reservar consulta para contrato público, persistência, dependências e mudança de escopo. Pedir uma proposta consolidada com poucas decisões pendentes.

### 3. Revisar famílias de riscos, evitando correções isoladas

**Evidência:** os três reviews de Unicode encontram variações da mesma classe de problema em superfícies diferentes.

**Avaliação:** as revisões funcionaram, mas a primeira correção poderia ter acionado uma busca sistemática pelos outros caminhos de serialização. Corrigir apenas o exemplo encontrado aumenta a chance de precisar de uma nova rodada completa.

**Ação:** diante de um defeito, pedir explicitamente quais entradas, representações e pontos de transformação compartilham a mesma premissa. Revisar a família e depois verificar o conjunto de correções. Encerrar quando os riscos identificados estiverem cobertos; não acumular revisões gerais sem nova hipótese.

### 4. Tornar o limite do escopo público mais explícito

**Evidência:** houve investimento significativo na preservação de Unicode, compatibilidade de contratos e composição entre aplicações. O dashboard foi solicitado explicitamente em P110 e é uma extensão além da interface gráfica excluída do escopo obrigatório da spec.

**Avaliação:** extensão autorizada e rigor técnico não são automaticamente excesso. O risco é ampliar garantias públicas sem avaliar quanto código, testes e manutenção elas passam a exigir. Não há dados para afirmar que o investimento em serialização foi desnecessário.

**Ação:** para cada garantia adicional, registrar o caso de uso que a exige, os tipos suportados, a política de erro e o teste que prova seu valor. Separar orçamento da entrega obrigatória e orçamento da exploração opcional, especialmente quando comparar workflows.

### 5. Melhorar a granularidade e a descrição dos commits

**Evidência:** há 20 commits em `main`; 19 registros adicionais são checkpoints automáticos. `b47ea63` adiciona 2.784 linhas e envolve dashboard, integração, build, Makefile e testes. `fb8afa8` é descrito como refatoração e melhoria de testes, mas também altera significativamente o motor e a persistência para reconciliação.

**Avaliação:** o histórico é razoavelmente organizado por capacidades, porém algumas mensagens subestimam mudanças de comportamento e alguns commits agrupam decisões que mereceriam revisão separada.

**Ação:** criar commits por comportamento verificável: integração do pacote, entrega dos assets, consulta do monitor e execução local, por exemplo. Descrever o efeito funcional na mensagem. Checkpoints automáticos continuam úteis para recuperação, mas devem ficar fora de métricas de entregas.

### 6. Medir aprendizado e esforço humano diretamente

**Evidência:** o histórico mostra perguntas relevantes e correções críticas, mas não registra minutos de leitura, revisão manual ou capacidade de reproduzir o raciocínio sem ajuda. O tempo do coletor contém um registro de quase oito horas.

**Ação:** registrar por incremento três medidas simples: tempo humano aproximado, intervenções que mudaram a solução e uma breve explicação escrita pelo desenvolvedor sobre uma decisão ou falha. Para testar aprendizado, explicar e alterar um comportamento pequeno sem pedir inicialmente a solução ao agente. Para testar produtividade, comparar tarefas semelhantes com critérios de aceite equivalentes.

## Distribuição do investimento

Agrupamento analítico dos registros, por assunto e ordem no export. Os custos desta tabela usam a tarifa Standard base, sem o adicional de contexto longo, para permitir reprodução diretamente pelo JSON. Não são custos exclusivos de código: incluem discussão, documentação, execução e testes do intervalo.

| Assunto | Registros | Tokens | US$ base |
| --- | --- | ---: | ---: |
| Fundação, arquitetura e organização do trabalho | P001–P018 | 4.530.394 | 1,4135 |
| Contratos, API, serialização e suas revisões | P019–P074 | 20.977.131 | 7,6086 |
| Persistência Redis e exposição HTTP inicial | P075–P082 | 6.738.222 | 1,6143 |
| Motor, concorrência, resiliência e fechamento funcional | P083–P109 | 67.198.551 | 14,9765 |
| Dashboard, correção visual e integração como pacote | P110–P116 | 34.987.750 | 6,3560 |
| Execução local, cliente HTTP, ajustes visuais e documentação | P117–P126 | 7.186.807 | 1,7218 |
| **Total** | **126 registros** | **141.618.855** | **33,6909** |

O dashboard ocupa sete registros, mas aproximadamente 24,7% dos tokens. Isso ilustra por que contar mensagens não mede esforço delegado. P111, sua execução inicial, equivale a US$ 2,4671 base; P116, execução da reformulação do consumo, a US$ 2,3442. Não seria correto classificar todo o segundo valor como desperdício: ele também implementa e verifica uma definição mais específica de produto.

O custo de tokens é baixo em termos absolutos nesta tabela, mas isso não mede o custo da atenção humana. O maior ganho provável de melhorar o fluxo está em reduzir revisões de propostas repetidas e incompatibilidades descobertas tarde. O histórico não permite quantificar esse ganho.

## Custo em dólares e memória de cálculo

### Tarifas e premissas

Preços públicos consultados em **11/11/2026**, por milhão de tokens, em API Standard. São uma referência atual aplicada ao consumo observado, não uma reconstrução de preços históricos da conta.

| Modelo | Entrada sem cache | Leitura de cache | Escrita de cache de 30 min | Escrita de cache de 12 h | Saída |
| --- | ---: | ---: | ---: | ---: | ---: |
| `gpt-6.1-sol` | US$ 2,00 | US$ 0,10 | US$ 2,50 | US$ 5,00 | US$ 10,00 |
| `gpt-6-astra` | US$ 10,00 | US$ 1,00 | US$ 12,50 | US$ 25,00 | US$ 50,00 |

Fonte: [tabela oficial de preços da OpenAI](https://developers.openai.com/api/docs/pricing).

Nos dois modelos, chamadas de API com mais de 272 mil tokens de entrada têm multiplicador de 2 na entrada/cache e de 1,5 na saída, aplicado à chamada inteira. Fast custa 2 vezes Standard. A página do Sol informa que consumo pela assinatura não recebe adicional de contexto longo. Fontes: [GPT-6.1 Sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol) e [GPT-6 Astra](https://developers.openai.com/api/docs/models/gpt-6-astra).

### Contagem reconciliada

| Categoria | GPT-6.1 Sol | GPT-6 Astra | Total |
| --- | ---: | ---: | ---: |
| Entrada sem cache | 4.071.793 | 42.375 | 4.114.168 |
| Leitura de cache | 135.983.488 | 444.032 | 136.427.520 |
| Escrita de cache registrada | 0 | 0 | 0 |
| Saída, incluindo raciocínio | 1.069.430 | 7.737 | 1.077.167 |
| Raciocínio, subconjunto da saída | 348.988 | 863 | 349.851 |
| Total, sem duplicar raciocínio | 141.124.711 | 494.144 | 141.618.855 |

No export, `input_tokens` já representa entrada **sem cache**. Nos logs brutos, `input_tokens` contém a entrada total, da qual se subtrai `cached_input_tokens`. A soma bruta da entrada é 140.541.688. Essa diferença de semântica precisa ser preservada na conversão.

Os tokens de raciocínio são cobrados como saída e já estão no contador de saída; somá-los novamente duplicaria parte do custo. Fonte: [guia oficial de modelos de raciocínio](https://developers.openai.com/api/docs/guides/reasoning).

```text
Custo base por modelo =
  (entrada_sem_cache × preço_entrada
   + leitura_cache × preço_cache
   + escrita_cache × preço_escrita
   + saída_total × preço_saída) / 1.000.000
```

| Parcela | GPT-6.1 Sol | GPT-6 Astra | Total US$ |
| --- | ---: | ---: | ---: |
| Entrada sem cache | 8,143586 | 0,423750 | 8,567336 |
| Leitura de cache | 13,5983488 | 0,444032 | 14,0423808 |
| Escrita de cache registrada | 0 | 0 | 0 |
| Saída total | 10,694300 | 0,386850 | 11,081150 |
| **Base Standard** | **32,4362348** | **1,254632** | **33,6908668** |
| Adicional por contexto longo | 1,9954096 | 0 | 1,9954096 |
| **API Standard com contexto longo** | **34,4316444** | **1,254632** | **35,6862764** |

Foram identificadas **44 chamadas** acima de 272 mil tokens de entrada, todas em Sol. A maior entrada por chamada foi 395.537 tokens. Nessas chamadas há 46.348 tokens de entrada sem cache, 14.916.736 de cache e 82.208 de saída. O adicional é:

```text
(46.348 × 2 + 14.916.736 × 0,10 + 82.208 × 10 × 0,5)
/ 1.000.000 = US$ 1,9954096
```

**Resultado principal arredondado: US$ 35,69.** Como cenário alternativo, se todo o mesmo consumo fosse cobrado em Fast, com as mesmas regras de contexto, seriam **US$ 71,37**. Os eventos examinados não informam `service_tier`; o modo atual desta sessão não permite inferir o modo das sessões históricas.

Cache corresponde a **97,07% da entrada**. O desconto é decisivo: na comparação base, cobrar toda a entrada como não cacheada elevaria o equivalente de US$ 33,69 para aproximadamente US$ 296,06. Esse é um contrafactual de preços, sem simular mudanças de comportamento ou latência.

### Limites da estimativa

- Não inclui assinatura, impostos, câmbio, infraestrutura local, tempo humano nem eventuais cobranças específicas de ferramentas. As 1.148 chamadas de ferramentas não são automaticamente 1.148 operações de API tarifadas.
- A geração da apresentação pode ter consumo próprio fora das sessões do projeto; não há tokens atribuídos a ela neste export. Esta análise também fica fora do corte.
- Escrita de cache é zero nos campos disponíveis, inclusive nos logs. Não foi inventada uma quantidade de escritas a partir das leituras.
- Não há evidência de descontos negociados, modalidade Fast/Flex/Batch, região de processamento ou preço efetivo da conta. O total principal adota Standard explicitamente.
- O custo real pago permanece **não determinado**. Para fechá-lo, seria necessário o extrato de cobrança/uso e a modalidade de contratação. O custo equivalente por tokens já pode ser calculado sem esses dados.

### Como a reconciliação foi feita

Os 43 caminhos `source_file` existem, mas dois não contêm todo o uso da sessão. Foram incluídos os arquivos de continuação das sessões `01a1c57b-ca78-7f23-8a59-3de9d16d0064` e `01a1cabe-b25d-7e02-9693-857725c9fbfd`.

Os eventos foram unidos por ID de sessão e ordenados por timestamp, limitados ao instante do export. Contadores cumulativos repetidos foram ignorados. A diferença entre contadores consecutivos produziu 1.267 incrementos; todos coincidiram com `last_token_usage`, sem incrementos negativos. A soma reconciliou exatamente com os campos de tokens do export. Também foram encontrados arquivos de outras sessões do workspace sem eventos de tokens; eles não justificam acrescentar consumo nem assumir custo zero de toda atividade ausente.

Para reproduzir a conta base, execute na raiz do repositório:

```bash
python3 - <<'PY'
import json
from decimal import Decimal as D
from pathlib import Path

x = json.loads(Path('.ai-workflow-analysis/tokenmetro.json').read_text())
prices = {
    'gpt-6.1-sol': (D('2'), D('0.10'), D('10')),
    'gpt-6-astra': (D('10'), D('1'), D('50')),
}
total = D(0)
for p in x['prompts']:
    assert p['cache_write_tokens'] == 0
    assert p['total_tokens'] == (
        p['input_tokens'] + p['cache_read_tokens'] + p['output_tokens'])
    if not p['total_tokens']:
        continue
    entry, cache, output = prices[p['model']]
    total += (p['input_tokens'] * entry
              + p['cache_read_tokens'] * cache
              + p['output_tokens'] * output) / D(1_000_000)
print('Standard base, USD:', total)
PY
```

O adicional de contexto longo exige os logs por chamada: não pode ser deduzido dividindo tokens agregados de um prompt pelo número de chamadas. A memória acima preserva as quantidades e a fórmula utilizadas.

## Ajustes propostos para o próximo exercício

| Prioridade | Ajuste | Como verificar o efeito |
| --- | --- | --- |
| Alta | Definir primeiro a experiência mínima de consumo e executá-la cedo. | Instalação externa, comando de execução e caminho principal demonstrados antes da implementação extensa. |
| Alta | Delegar decisões reversíveis e reservar perguntas para contratos e escopo. | Menos turnos de aprovação sem aumento de correções de direção. |
| Alta | Revisar classes de defeito após o primeiro achado. | Menos rodadas que descobrem variantes do mesmo problema. |
| Média | Fazer commits menores com mensagens sobre o comportamento entregue. | Uma capacidade e sua validação podem ser revisadas sem percorrer várias decisões independentes. |
| Média | Registrar tempo humano e evidência de aprendizado. | É possível comparar custo de atenção e explicar decisões sem consultar o agente. |
| Média | Acompanhar custo por resultado aceito e separar exploração opcional. | Comparações entre trabalhos usam escopo equivalente e contabilizam revisões/correções. |

Um pedido de execução mais preciso, adaptável a cada incremento:

> Implemente este comportamento a partir da spec e do código atual. Antes de ampliar a solução, demonstre o caminho mínimo de consumo em uma aplicação externa. Pode decidir detalhes internos reversíveis. Consulte o responsável pelo projeto se precisar mudar contrato público, persistência, dependências ou escopo. Valide os riscos relevantes e, ao encontrar um defeito, investigue os demais caminhos que compartilham sua causa. Ao concluir, apresente o resultado observável, as verificações executadas e as limitações. Consolide apenas informação durável na documentação responsável.

Para o objetivo de praticar IA, recomenda-se preservar também sessões deliberadas de exploração: escolher uma decisão de alto impacto, formular uma hipótese própria e usar o agente para confrontá-la com exemplos e contraexemplos. O material evidencia questionamento crítico; o próximo avanço é medir o aprendizado e antecipar a demonstração do produto esperado.

## Rastreabilidade do histórico completo

Foram considerados os 20 commits de `main` e as 19 entradas automáticas das referências `refs/agents/8a82a8dc-c7a1-453e-948c-792ca302d91b/checkpoints/turn/*`. Estas últimas correspondem ao checkpoint inicial e aos pares de início/fim de nove turnos; não foram contadas como entregas adicionais.

| Commit em `main` | Assunto observado |
| --- | --- |
| `f3f2fad` | Estrutura inicial. |
| `7569154` | Redis local e instruções. |
| `ecd9aff` | Documentação fundamental. |
| `5bb3ede` | Instalação do SDK no WSL. |
| `124e70b` | Exclusão das configurações do editor do versionamento. |
| `5e1bca2` | Contratos, registro, validação e serialização. |
| `602b278` | Persistência e gerenciamento com Redis. |
| `5202ca2` | Criação e consulta por HTTP. |
| `8a603b0` | Consolidação de evidências e remoção de documentos temporários. |
| `79e4d46` | Execução de jobs e testes. |
| `c2a117f` | Concorrência. |
| `5260622` | Agendamento e retry. |
| `9c8c0d9` | Gerenciamento do ciclo de vida. |
| `c6a85c6` | Recuperação e testes de processos. |
| `fb8afa8` | Indisponibilidade, reconciliação e evolução. |
| `806e74c` | Limites HTTP e consolidação da entrega. |
| `b47ea63` | Dashboard, integração e execução local. |
| `7119617` | Coleções HTTP e exemplos de uso. |
| `46640b1` | Simplificação da interface e atualização automática. |
| `cb4347f` | Documentação temática e guias de uso/validação. |

Para conferir o universo examinado: `git log --all --reverse --date=iso-strict --stat`, `git log main --oneline` e `git for-each-ref refs/agents`.

Os exports foram apenas renomeados, preservando conteúdo e identidade do desenvolvedor. SHA-256 dos arquivos:

```text
tokenmetro.json  5038dc025575cb29698b4507a819cd5c557d4f493bbf8eee1d96b41d109d09c2
tokenmetro.html  0a8064d8bf2d81af2fcc0ae74b6c9d43d280490c12caa55abb87ad9d4b01a555
```

### Relações entre prompts e commits

A aba **Prompts ↔ Commits** do [relatório integrado](workflow-analysis.html#traceability) permite explorar o histórico **por commit ou por sessão**. A lista das 43 sessões apresenta data, resumo do assunto, quantidade de registros e contagem de commits distintos no grupo selecionado. Cada sessão reúne seus commits sem duplicação e permite expandir os prompts para consultar o texto, as relações e as evidências. Um mesmo commit pode aparecer em várias sessões quando há contribuições de conversas diferentes; os dados preservam a relação precisa entre prompt e commit.

Há busca por sessão, prompt, texto, SHA e caminho de arquivo, filtros de vínculos e separação entre `main` e checkpoints. Os commits relacionados oferecem **Ver alterações no GitHub**, com acesso direto ao diff, e **Explorar relação**, que abre o detalhe interno com arquivos e prompts. A lista por commit também oferece o link direto para o GitHub. Ao selecionar um prompt no detalhe do commit, a interface abre sua sessão com aquele registro expandido e em foco. Checkpoints sem publicação confirmada permanecem sem link externo. A apresentação mantém sua navegação própria.

O [conjunto de relações](prompt-commits.json) complementa o export original, preservado com o hash acima. Contém 122 relações revisadas pelo Codex, envolvendo 120 registros e 18 dos 20 commits de `main`. Os seis registros sem vínculo independente incluem mensagens incompletas, limpeza de contexto e controles de metas interrompidas. Os dois commits de `main` sem atribuição tratam da instalação do SDK e do ignore do editor. Os 19 checkpoints pertencem a uma sessão ausente do export e não recebem links remotos sem confirmação de publicação.

A análise combina três fontes:

1. **Horário:** o intervalo entre commits de `main` fornece candidatos iniciais, sem excluir decisões e refinamentos anteriores. Os instantes são comparados em UTC e exibidos em `America/Sao_Paulo`.
2. **Evidências:** sessão, turno, comandos de edição e respostas dos logs locais são confrontados com os caminhos e o conteúdo dos commits. As referências guardam identificadores de sessão/chamada e horário; não incorporam os logs completos. A extração inclui arquivos de continuação das sessões.
3. **Revisão:** cada relação registra seu papel e sua justificativa. Planejamento, exploração, decisão, implementação, revisão, correção, documentação e pedido de commit têm significados distintos. Candidatos descartados por horário também ficam consultáveis.

P090 e P100 ilustram o ajuste: aparecem segundos antes de commits sobre o trabalho anterior, mas sua conversa e suas edições correspondem, respectivamente, aos commits de concorrência e recuperação. P060 contribui para três commits por definir o tratamento de erros aplicado aos contratos, à persistência e ao HTTP. Esses casos exigem relações de muitos para muitos e análise além do intervalo imediato.

“Relação revisada” indica interpretação fundamentada, não prova de autoria exclusiva ou atribuição de cada linha. Uma referência em comando de escrita pode ser leitura; uma edição pode ser reformulada antes do commit. Explorações de alternativas descartadas permanecem identificadas como contexto, sem afirmar que foram implementadas. Não há vínculos mantidos apenas por horário neste recorte; ausência de vínculo não prova ausência de contribuição.

O HTML incorpora o mesmo conjunto de dados do JSON e oferece seu download na explicação do método, funcionando offline para navegação e consulta de arquivos. Os links do GitHub usam SHAs completos dos commits de `main`, alcançáveis pela referência remota conferida. Para manter o relatório, atualizar o JSON de relações e sua cópia embutida em `window.__PROMPT_COMMITS__`, preservando os IDs de prompts do export; a aba contém seu componente e estilos no próprio HTML. Não há coleta automática de novos commits ou alteração do gerador do tokenmetro.

A validação do relatório confere os caminhos e as contagens de todos os 39 commits contra o Git, a integridade das relações e a igualdade entre o JSON e os dados embutidos. A verificação em Chromium cobre navegação entre commits e sessões, agrupamento dos prompts, deduplicação e contagem de commits, links diretos para o GitHub, foco no prompt selecionado, relações de muitos para muitos, exclusões por horário, filtros, registros sem vínculo, checkpoints, download, abertura da conversa, tema escuro e layout móvel. A navegação local não faz requisições de rede nem apresenta erros de JavaScript; os dados originais e o código da apresentação permanecem preservados. Essas verificações se referem ao relatório, sem reexecutar as suítes da biblioteca.
