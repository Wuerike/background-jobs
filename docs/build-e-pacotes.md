# Build e pacotes

[Início](../README.md) · [Arquitetura](arquitetura.md)

Este guia descreve o empacotamento das bibliotecas, o consumo dos pacotes e a publicação da aplicação demonstrativa. Execute os comandos na raiz do repositório.

## Gerar pacotes e compilar

Os pacotes não estão publicados em um feed externo. O ponto de entrada recorrente do repositório empacota as duas bibliotecas, registra a versão local usada pela Demo e compila a solução:

```bash
dotnet msbuild build.proj -t:Build
```

Os artefatos ficam em `build/unix` no Linux/WSL e `build/windows` no Windows, com os `.nupkg` na subpasta `packages`. A separação evita colisões de restore e compilação entre as plataformas no mesmo checkout. A versão de desenvolvimento é única por empacotamento para evitar reutilização de uma cópia antiga no cache NuGet. `BackgroundJobs.Packages.props` informa a versão exata à Demo; esse arquivo e os pacotes são gerados e ignorados pelo Git. Ao alterar as bibliotecas, use esse build para atualizar os pacotes consumidos pela Demo.

`-t:Test` compila e executa as suítes; `-t:PublishDemo -p:Configuration=Release` publica a Demo em `build/unix/publish/demo` ou `build/windows/publish/demo`. `-p:ArtifactsPath=/tmp/backgroundjobs-build` permite um diretório de artefatos próprio para Linux/WSL. Use o mesmo caminho em comandos posteriores de `dotnet run` e `dotnet test`.

## Distribuir e consumir pacotes

Para distribuir uma versão estável, os projetos continuam empacotáveis com `dotnet pack src/BackgroundJobs` e `dotnet pack src/BackgroundJobs.Dashboard`; o pacote da interface depende do pacote do núcleo e inclui seus recursos e requisitos de build. Para instalar em outro projeto, configure a subpasta `packages` gerada como source NuGet adicional, mantendo `nuget.org` habilitado para as dependências. Então use `dotnet add package BackgroundJobs.Dashboard --prerelease` com o feed local configurado. O consumidor recebe o núcleo por dependência, sem referência a projetos ou arquivos deste checkout; a opção `--prerelease` seleciona as versões locais de desenvolvimento.

## Diretório de artefatos

Os diretórios de saída são centralizados por `Directory.Build.props`. Os caminhos padrão abaixo ficam sob `build/unix` no Linux/WSL e `build/windows` no Windows:

- `build/<plataforma>/bin/<projeto>/<configuração>/`: binários compilados.
- `build/<plataforma>/obj/<projeto>/<configuração>/`: arquivos intermediários; os arquivos de restore ficam em `build/<plataforma>/obj/<projeto>/`.

A pasta `build/` é ignorada pelo Git.

## Documentação relacionada

- [Execução local](execucao-local.md): ambiente e inicialização da demo.
- [Uso da biblioteca](uso-biblioteca.md): integração em outro processo.
- [Testes e validação](testes-e-validacao.md): pré-requisitos e suítes.
