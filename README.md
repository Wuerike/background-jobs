# BackgroundJobs

Estrutura inicial em .NET 10, baseada em [docs/spec-base.md](docs/spec-base.md).

- `BackgroundJobs.slnx`: solução com os dois projetos.
- `src/BackgroundJobs`: biblioteca de classes, ainda vazia.
- `src/BackgroundJobs.Demo`: aplicação ASP.NET Core com estrutura para Minimal APIs e referência à biblioteca.

Não há implementação de jobs, processamento, persistência ou endpoints nesta etapa.

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
