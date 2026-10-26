# BackgroundJobs

Estrutura inicial em .NET 10, baseada em [docs/spec-base.md](docs/spec-base.md).

- `BackgroundJobs.slnx`: solução com os dois projetos.
- `src/BackgroundJobs`: biblioteca de classes, ainda vazia.
- `src/BackgroundJobs.Demo`: aplicação ASP.NET Core com estrutura para Minimal APIs e referência à biblioteca.

Não há implementação de jobs, processamento, persistência ou endpoints nesta etapa.

## Redis local

O storage escolhido para os jobs é Redis. O `compose.yaml` usa a imagem oficial
`redis:8.2.10-alpine`, com volume persistente em `/data`, AOF habilitado e
`appendfsync always` para sincronizar as escritas em disco antes de responder.
A política `noeviction` impede a remoção automática de chaves por pressão de memória.
Referência: [persistência do Redis](https://redis.io/docs/latest/operate/oss_and_stack/management/persistence/).

Na raiz do projeto, com Docker e Docker Compose disponíveis, inicie o serviço:

```bash
docker compose up -d --wait redis
docker compose exec redis redis-cli ping
```

O segundo comando deve retornar `PONG`. O serviço fica disponível apenas em
`127.0.0.1:6379`, sem senha, para desenvolvimento local. A aplicação .NET executada
no host deverá usar a connection string `127.0.0.1:6379`. A integração da aplicação
com Redis ainda será implementada.

Se a porta já estiver ocupada, escolha outra ao iniciar:

```bash
REDIS_PORT=6380 docker compose up -d --wait redis
```

Nesse caso, a connection string será `127.0.0.1:6380`.

Para consultar logs e parar o serviço preservando os dados:

```bash
docker compose logs -f redis
docker compose down
```

O volume `redis-data` é mantido ao parar ou recriar o container. O comando
`docker compose down -v` também remove o volume e apaga os dados locais.

## SDK local

O SDK .NET 10.0.403 para Linux x64 foi instalado em `.dotnet/` na raiz do projeto, usando os [binários oficiais da Microsoft](https://learn.microsoft.com/en-us/dotnet/core/install/linux-scripted-manual).

No Bash, a partir da raiz do projeto, configure a sessão:

```bash
export DOTNET_ROOT="$PWD/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_HOME="$DOTNET_ROOT"
export NUGET_PACKAGES="$DOTNET_ROOT/packages"
```

## Compilar e executar

```bash
dotnet build BackgroundJobs.slnx
dotnet run --project src/BackgroundJobs.Demo --no-launch-profile
```

A aplicação inicia o servidor HTTP, ainda sem rotas. Requisições retornam `404`.

Os diretórios de saída são centralizados na pasta `build/` da raiz por `Directory.Build.props`, usando o [layout de artefatos do SDK](https://learn.microsoft.com/pt-br/dotnet/core/sdk/artifacts-output):

- `build/bin/<projeto>/<configuração>/`: binários compilados.
- `build/obj/<projeto>/<configuração>/`: arquivos intermediários; os arquivos de restore ficam em `build/obj/<projeto>/`.

A pasta `build/` é ignorada pelo Git.

## Validação neste ambiente

O build foi concluído com zero avisos e zero erros. Para contornar as restrições de escrita e de processos deste ambiente, após configurar o SDK local, foi usado:

```bash
export XDG_RUNTIME_DIR=/tmp
export XDG_DATA_HOME="$DOTNET_ROOT/.local/share"
dotnet build BackgroundJobs.slnx --disable-build-servers -m:1
```
