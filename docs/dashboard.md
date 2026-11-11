# Dashboard opcional

[Início](../README.md) · [Arquitetura](arquitetura.md)

Este guia cobre a instalação, o uso e as decisões técnicas da extensão de monitoramento. O pacote é opcional e o núcleo funciona sem ASP.NET Core ou interface.

## Instalação e acesso

`BackgroundJobs.Dashboard` é um pacote opcional para aplicações ASP.NET Core .NET 10 que já usam a biblioteca. Ele disponibiliza `/dashboard/` na mesma aplicação com um registro:

```csharp
using BackgroundJobs.Dashboard;

builder.Services.AddBackgroundJobsDashboard();
```

A aplicação instala o pacote NuGet e mantém sua configuração normal de jobs. O dashboard reutiliza o `IConnectionMultiplexer` registrado no contêiner e o prefixo padrão `background-jobs`; não registra workers, handlers, scheduler ou recuperação. Componentes, CSS, JavaScript, runtime Blazor, rotas e middleware são responsabilidade do pacote. Não é necessário adicionar páginas Razor, editar propriedades do projeto ou chamar métodos de mapeamento na aplicação.

A Demo consome `BackgroundJobs` e `BackgroundJobs.Dashboard` por `PackageReference` e contém esse único registro adicional. Após compilar com o [fluxo de pacotes locais](build-e-pacotes.md), inicie-a e abra `http://127.0.0.1:8080/dashboard/`:

```bash
REDIS_CONNECTION=127.0.0.1:6379 \
  dotnet run --project src/BackgroundJobs.Demo --no-build --no-launch-profile
```

O painel inclui indicadores dos seis estados, fila, execução, agendamentos/retries, busca por ID/nome, período UTC, ordenação e paginação. Detalhes mostram argumentos JSON copiáveis, opções, tempos, erros, tentativas por ciclo e histórico. Ações de reenfileiramento e deleção têm confirmação e usam as decisões atômicas da biblioteca.

## Configuração

A configuração opcional permanece na mesma chamada:

```csharp
builder.Services.AddBackgroundJobsDashboard(
    prefix: "minha-aplicacao", // mesmo prefixo do cliente, gerente e executor
    configure: options =>
    {
        options.Path = "/operations/jobs";
        options.StorageLabel = "Minha aplicação"; // rótulo público, sem credenciais
        options.MaxJobs = 10_000;
        // options.AuthorizationPolicy = "JobOperators"; // política definida pelo consumidor
    });
```

A rota é isolada das rotas da aplicação. A biblioteca principal continua utilizável sem ASP.NET Core ou dashboard. Autenticação e políticas pertencem à aplicação consumidora; o pacote não cria usuários nem um mecanismo próprio de login. Parâmetros e erros podem conter informações da aplicação, portanto suas políticas devem abranger leitura e ações.

## Integração com a aplicação

Um `IStartupFilter` monta um pipeline isolado sob `DashboardOptions.Path`, com padrão `/dashboard`. O pacote mantém seu roteamento e antiforgery dentro desse caminho, preservando os endpoints da aplicação. A base dos links e da conexão interativa acompanha o caminho do painel. O pacote carrega o manifesto de recursos estáticos do build; a publicação usa `wwwroot`. A dependência de assets do ASP.NET Core inclui o runtime Blazor por NuGet, inclusive em consumidores sem arquivos Razor próprios.

A conexão Redis pertence à aplicação e é reutilizada via `IConnectionMultiplexer`. O gerente concreto do dashboard usa o mesmo prefixo do monitor e não substitui um `IJobManager` registrado pelo consumidor. A extensão não registra catálogo, handlers, `JobServer`, scheduler ou recuperação. Consultar ou abrir a interface não altera tentativas abertas. Acesso ao armazenamento não comprova prontidão do executor; essa informação é apresentada como não informada. As ações usam as regras atômicas de gerenciamento da biblioteca.

## Consultas e consistência

`RedisJobMonitor.ReadAsync` percorre o conjunto de IDs com `SSCAN`, deduplica e consulta projeções atômicas por job em lotes de até 128. Cada projeção lê nove campos escalares e as últimas entradas de tentativa e estado, sem argumentos nem históricos completos. Não há índices adicionais ou migração de dados. O limite padrão é 10.000 jobs; `MaxJobs` permite de 1 a 100.000. Exceder o limite, encontrar metadados inválidos ou falhar no Redis invalida a leitura inteira. A projeção valida os campos consultados; a validação de argumentos e históricos completos pertence à consulta de detalhe e ao motor.

`JobObservation` contém início/fim, coleção imutável, contagens dos seis estados e quantidade de retries (`scheduled` com tentativas consumidas). Não é um snapshot global: cada job é consistente, mas pode ter sido observado em momentos diferentes no intervalo. As contagens abrangem a coleção observada, independentemente dos filtros. `Query(JobMonitorQuery)` aplica estado, busca parcial por ID/nome sem distinção de caixa, período de criação inclusivo em `From` e exclusivo em `Until`, ordem e paginação de 1 a 100 itens. Datas são comparadas como instantes UTC. A ordenação usa ID ordinal como desempate; é estável dentro da observação e pode mudar entre leituras. A consulta tem custo O(N) limitado, indicado para operação local e volumes moderados; escalas maiores exigem outra estratégia de consulta, não truncamento silencioso.

## Atualização e navegação

O cache singleton por host compartilha observações por até 1 segundo e serializa leituras concorrentes. A invalidação usa uma versão para impedir reutilização de leitura iniciada antes de uma mutação. A UI consulta a cada 1 segundo, com seleção de 1/5/10/30/60 segundos, pausa e atualização manual no cabeçalho. As contagens e os atalhos por estado ficam na sidebar; a área principal apresenta a listagem ou o detalhe do job. Um timer por sessão atualiza os tempos locais sem sobrepor consultas; é cancelado no descarte do componente. Um `CircuitHandler` suspende o polling durante desconexões, inclusive enquanto Blazor retém o circuito para reconexão. A atualização manual invalida o cache. A última observação permanece visível e identificada como desatualizada em falhas. Navegação e filtros ficam na query string, incluindo o ID opaco codificado, e o retorno do detalhe preserva contexto.

Os horários são exibidos em UTC. O relógio de duração pode avançar localmente sem nova confirmação de estado, e as páginas podem mudar entre atualizações.

## Durações e ciclos

Espera atual na fila é o intervalo desde `UpdatedAt` em `enqueued`; execução em andamento usa o início da tentativa aberta; duração concluída usa fim menos início. A tabela identifica a última execução, inclusive de ciclo anterior quando o contador atual é zero. O tempo do ciclo atual inclui espera e execução, da criação ou do último reenfileiramento até o estado final ou o instante local em andamento. O acumulado concluído soma tentativas finalizadas não interrompidas. Interrupção mostra intervalo observado até recuperação, não duração exata da função. Elegibilidade usa `ScheduledAt`; alcançar o horário não afirma promoção ou início. Ciclos são identificados pela ordem do histórico: transições de estado final para `enqueued` iniciam outro ciclo, e entradas `processing` correspondem às tentativas em ordem. A numeração reiniciada e timestamps iguais não provocam colisão.

## Ações e apresentação

A UI usa HTML semântico, CSS responsivo e ícones SVG locais, sem dependências de frontend ou fontes externas. Argumentos e erros são texto escapado; JSON é formatado e copiável. Confirmação usa diálogo modal com foco inicial em cancelar e retorno ao acionador. Escritas bloqueiam envio duplicado enquanto pendentes, apresentam ausência/conflito e atualizam o detalhe. Falha de confirmação informa resultado incerto e nunca dispara outra chamada de escrita no painel; a reconciliação interna do gerente mantém suas garantias de domínio.

## Autorização

O endereço e a porta pertencem à aplicação consumidora. Autenticação e políticas são configuradas por ela; `DashboardOptions.AuthorizationPolicy` permite restringir o painel, e a política é conferida no servidor antes de leituras e ações. A extensão não inclui um subsistema de login. Credenciais Redis permanecem no servidor. A interatividade usa circuito Blazor e antiforgery no pipeline do painel, sem endpoints HTTP adicionais de gerenciamento.

## Limites

O dashboard é uma extensão além da entrega obrigatória: a seção 2 da spec exclui interface gráfica de seu escopo. Os contratos HTTP exigidos e os critérios de aceite da biblioteca continuam válidos independentemente da extensão.

O painel não oferece cancelamento em execução, edição de parâmetros, logs de aplicação, heartbeat, estimativa de término, gráficos históricos, exportação ou ações em lote.

## Documentação relacionada

- [Build e pacotes](build-e-pacotes.md): instalar a extensão em outro projeto.
- [Uso da biblioteca](uso-biblioteca.md#consultar-e-gerenciar): regras de deleção e reenfileiramento.
- [Testes e validação](testes-e-validacao.md#dashboard-e-consumidores): evidências da interface e dos pacotes.
