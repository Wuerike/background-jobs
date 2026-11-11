# BackgroundJobs

Biblioteca .NET 10 para executar jobs em background dentro do processo da aplicação, com persistência Redis e motor próprio de processamento, agendamento e retry.

Oferece registro tipado de funções, workers configuráveis (padrão 4), agendamento, retry com atraso fixo, históricos de estados e tentativas, recuperação após queda, consulta, deleção lógica e reenfileiramento. A demo ASP.NET Core apresenta a API HTTP e o dashboard opcional.

Use **um único processo executor por armazenamento**. A execução tem garantia **at-least-once sujeita ao limite de tentativas**; handlers devem considerar idempotência de efeitos externos.

<a id="sdk-no-wsl"></a>
<a id="redis-local"></a>

## Pré-requisitos

- SDK .NET 10.
- Docker com Docker Compose para o Redis local, ou Redis externo configurado conforme o [guia de persistência](docs/persistencia-redis.md).
- GNU Make para `make run`; os exemplos de shell usam Bash/Linux/WSL.

Consulte a [instalação do SDK no WSL](docs/execucao-local.md#sdk-no-wsl) e a [configuração do Redis local](docs/persistencia-redis.md#redis-local).

## Compilar e executar

Na raiz do repositório:

```bash
make run
```

O comando prepara o Redis local, gera os pacotes e inicia a demo com recompilação automática. Ao alterar as bibliotecas, pare e execute `make run` novamente para atualizar os pacotes. Os pacotes ainda não estão publicados em feed externo.

Para executar sem Make ou watch:

```bash
mkdir -p ./data
docker compose up -d --wait redis
dotnet msbuild build.proj -t:Build
dotnet run --project src/BackgroundJobs.Demo --no-build --no-launch-profile
```

A demo usa `PORT=8080`, `WORKER_COUNT=4` e `REDIS_CONNECTION=127.0.0.1:6379` por padrão. O Compose persiste em `DATA_DIR=./data`. Veja [execução local](docs/execucao-local.md) para configuração completa e outras portas.

<a id="registro-de-contratos"></a>
<a id="criar-executar-e-consultar-sem-http"></a>

## Demonstrar execução HTTP

Com a demo iniciada, em outro terminal:

```bash
curl --fail-with-body -sS http://localhost:8080/health
curl --fail-with-body -sS http://localhost:8080/demo/jobs \
  -H 'Content-Type: application/json' \
  -d '{"job":"log-message","params":{"message":"Olá"}}'
# Substitua SEU_JOB_ID pelo jobId retornado:
curl --fail-with-body -sS http://localhost:8080/api/jobs/SEU_JOB_ID
```

A criação retorna `202 { "jobId": "..." }` após persistência. Consulte [API HTTP](docs/api-http.md) para endpoints, exemplos de agendamento/retry e coleções Bruno; o guia da biblioteca explica o [registro de contratos](docs/uso-biblioteca.md#registro-de-contratos) e traz um [exemplo completo sem HTTP](docs/uso-biblioteca.md#criar-executar-e-consultar-sem-http).

## Dashboard opcional

Na demo, abra [http://localhost:8080/dashboard/](http://localhost:8080/dashboard/). O painel mostra estados, tentativas, erros, históricos e ações de gerenciamento. Integração em outra aplicação, configuração e limites estão em [dashboard](docs/dashboard.md).

<a id="preservar-um-volume-nomeado-existente"></a>

## Parar e reiniciar com os mesmos dados

Pressione Ctrl+C e aguarde o término das funções. Para parar o Redis, use `docker compose down` com o mesmo executável Docker usado na inicialização; os arquivos do diretório persistido são preservados. Reinicie usando o mesmo `DATA_DIR`, porta e conexão. O [guia de execução local](docs/execucao-local.md#parar-e-reiniciar-com-os-mesmos-dados) detalha sinais, recuperação e conferência dos dados.

Para dados em volume nomeado, siga o [procedimento de preservação](docs/persistencia-redis.md#preservar-um-volume-nomeado-existente).

## Validar

Com o build disponível, os testes unitários não precisam de Redis:

```bash
dotnet test tests/BackgroundJobs.UnitTests --no-build
```

Para a suíte completa, prepare Redis acessível e um executável Redis nativo em Linux/WSL conforme [testes e validação](docs/testes-e-validacao.md):

```bash
dotnet msbuild build.proj -t:Test
```

O documento reúne pré-requisitos, comandos por suíte, cobertura dos critérios de aceite, resultados, medições e limitações das validações disponíveis.

<a id="pacotes-locais-e-build"></a>

## Documentação

| Documento | Consulte para |
| --- | --- |
| [Execução local](docs/execucao-local.md) | Preparar o ambiente, configurar, executar e reiniciar a demo. |
| [Build e pacotes](docs/build-e-pacotes.md) | Gerar NuGet, instalar em outro projeto e publicar a demo. |
| [Uso da biblioteca](docs/uso-biblioteca.md) | Registrar, criar, executar, consultar e gerenciar sem HTTP. |
| [API HTTP](docs/api-http.md) | Usar endpoints, jobs da demo, curl e Bruno. |
| [Dashboard](docs/dashboard.md) | Instalar e usar a extensão de monitoramento. |
| [Arquitetura](docs/arquitetura.md) | Entender componentes, responsabilidades e decisões gerais. |
| [Contratos e versionamento](docs/contratos-e-versionamento.md) | Evoluir argumentos e handlers preservando jobs existentes. |
| [Processamento](docs/processamento.md) | Entender estados, FIFO, retry, prontidão, recuperação e parada. |
| [Persistência Redis](docs/persistencia-redis.md) | Consultar modelo, índices, atomicidade, AOF e preservação de dados. |
| [Testes e validação](docs/testes-e-validacao.md) | Reproduzir testes e consultar evidências e limites. |
| [Especificação funcional](docs/spec-base.md) | Consultar requisitos, escopo e critérios de aceite. |
| [Plano de desenvolvimento](docs/plano-desenvolvimento.md) | Consultar metodologia e situação das entregas. |
