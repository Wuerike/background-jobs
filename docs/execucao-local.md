# Execução local

[Início](../README.md) · [Arquitetura](arquitetura.md)

Este guia cobre a preparação do ambiente e a execução, parada e reinício da demo com os mesmos dados.

## Pré-requisitos

Use SDK .NET 10, Docker com Docker Compose e, para `make run`, GNU Make. Os exemplos de shell usam Bash/Linux/WSL e partem da raiz do repositório. Python 3 é necessário somente para os exemplos HTTP que calculam datas.

Os pacotes das bibliotecas são gerados localmente; consulte [build e pacotes](build-e-pacotes.md).

## SDK no WSL

O projeto requer o SDK .NET 10. Para usar o SDK instalado no sistema Ubuntu 24.04
do WSL, configure o repositório de pacotes da Microsoft e instale o SDK:

```bash
sudo apt-get update
sudo apt-get install -y ca-certificates wget
wget https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
sudo dpkg -i /tmp/packages-microsoft-prod.deb
rm /tmp/packages-microsoft-prod.deb
sudo apt-get update
sudo apt-get install -y dotnet-sdk-10.0
dotnet --version
```

O último comando deve mostrar uma versão `10.0.x`. A instalação fica no WSL e
não requer manter o SDK dentro do repositório. Para conferir todos os SDKs
instalados, use `dotnet --list-sdks`.

## Redis local

Para o Redis fornecido pelo repositório:

```bash
mkdir -p ./data
docker compose up -d --wait redis
docker compose exec redis redis-cli ping
```

O ping deve retornar `PONG`. O serviço usa `127.0.0.1:6379` por padrão. Configuração de AOF, diretório persistido, portas alternativas, permissões e preservação de volumes estão em [persistência Redis](persistencia-redis.md#redis-local). No WSL, confira a integração do Docker com a distribuição; `make run` seleciona `docker.exe` quando disponível.

## Compilar e executar

Para desenvolver com recompilação automática, com GNU Make, SDK .NET 10 e Docker Compose disponível:

```bash
make run
```

O comando cria o diretório de dados, inicia o Redis local pelo Compose e aguarda sua prontidão, gera os pacotes locais das bibliotecas e inicia a demo com `dotnet watch`. Usa `docker.exe` quando disponível no WSL e `docker` nos demais ambientes; `DOCKER` permite escolher o executável. O Redis permanece disponível após encerrar o watch; use o mesmo executável Docker com `compose down` para pará-lo preservando os dados.

Alterações na demo são acompanhadas automaticamente. Ao alterar as bibliotecas, pare o watch e execute `make run` novamente para atualizar os pacotes. Para outras portas locais, use `PORT=8081 REDIS_PORT=6380 make run`. Para um Redis externo já disponível, informe `REDIS_CONNECTION`; quando diferente da conexão local configurada, o comando não inicia o Compose.

Para compilar a solução e executar sem watch:

```bash
dotnet msbuild build.proj -t:Build
dotnet run --project src/BackgroundJobs.Demo --no-build --no-launch-profile
```

A demo escuta na porta definida por `PORT` (padrão `8080`) e usa `REDIS_CONNECTION` (padrão `127.0.0.1:6379`). Para uma porta alternativa do Redis e da demo:

```bash
PORT=8081 REDIS_CONNECTION=127.0.0.1:6380 dotnet run --project src/BackgroundJobs.Demo --no-build --no-launch-profile
```

A demo usa `WORKER_COUNT`, com padrão **4**. Um valor fornecido precisa ser inteiro positivo de 32 bits; vazio, formato inválido, zero, negativo ou overflow impedem a inicialização com mensagem que identifica a configuração. Para FIFO, inicie com um worker; para observar concorrência, use dois:

```bash
WORKER_COUNT=1 dotnet run --project src/BackgroundJobs.Demo --no-build --no-launch-profile
# Após parar a aplicação, iniciar com dois workers:
WORKER_COUNT=2 dotnet run --project src/BackgroundJobs.Demo --no-build --no-launch-profile
```

Use apenas um executor por armazenamento. `DATA_DIR` configura o diretório do Redis no Compose; `REDIS_CONNECTION` aponta a biblioteca e a demo para esse serviço.

## Configuração

| Configuração | Padrão | Uso |
| --- | --- | --- |
| `PORT` | `8080` | Porta HTTP da demo. |
| `WORKER_COUNT` | `4` | Quantidade fixa de workers. Valor fornecido deve representar inteiro positivo de 32 bits; vazio, formato inválido, zero, negativo ou overflow impedem a inicialização com diagnóstico da configuração. |
| `DATA_DIR` | `./data` | Diretório do host montado em `/data` pelo Compose. |
| `REDIS_PORT` | `6379` | Porta do Redis exposta no host pelo Compose. |
| `REDIS_CONNECTION` | `127.0.0.1:6379` | Configuração adicional para a conexão da biblioteca. |

`DATA_DIR` configura o armazenamento no Compose; a biblioteca acessa esse armazenamento pelo Redis. Reinícios usam o mesmo diretório e conexão.

## Parar e reiniciar com os mesmos dados

Para uso interativo com `dotnet run`, pressione Ctrl+C e aguarde o término das funções. Para enviar sinais ao processo executor diretamente em Linux/WSL, use a DLL compilada. Na raiz do repositório, execute no mesmo terminal:

```bash
export PORT=8080 WORKER_COUNT=4 REDIS_CONNECTION=127.0.0.1:6379
dotnet msbuild build.proj -t:Build
dotnet build/unix/bin/BackgroundJobs.Demo/debug/BackgroundJobs.Demo.dll > /tmp/background-jobs-demo.log 2>&1 &
demo_pid=$!
curl --retry 15 --retry-delay 1 --retry-connrefused --fail-with-body -sS "http://localhost:$PORT/health"
# Após criar/consultar jobs em outro terminal, pare e aguarde:
kill -TERM "$demo_pid"
wait "$demo_pid"
# Preserve DATA_DIR e REDIS_PORT usados para iniciar o Redis:
docker compose down
docker compose up -d --wait redis
# Reinicie o executor no mesmo armazenamento:
dotnet build/unix/bin/BackgroundJobs.Demo/debug/BackgroundJobs.Demo.dll > /tmp/background-jobs-demo.log 2>&1 &
demo_pid=$!
```

Espere o health responder `200` antes de enviar pedidos; a consulta logo após iniciar pode falhar enquanto a escuta é preparada. Consulte os IDs guardados: argumentos, opções, estados finais, tentativas e históricos permanecem, e pendentes continuam processáveis. Os eventos da demo começam vazios a cada processo. Use novamente `kill -TERM` e `wait` para encerrar a instância iniciada; nunca mantenha dois executores no mesmo armazenamento.

Para observar recuperação após queda, substitua a parada por `kill -KILL "$demo_pid"` enquanto `succeed-after` estiver em `processing` e aguarde sua saída com `wait` (o shell informa encerramento por sinal, normalmente código 137). Reinicie com o mesmo Redis e consulte o ID: a tentativa aberta fica `interrupted`; outra só é iniciada se restar orçamento. Efeitos externos podem repetir, exigindo idempotência do consumidor. Os testes de processos automatizam esse cenário e a parada com pendentes; a garantia não depende do processo intermediário de `dotnet run`.

## Documentação relacionada

- [Persistência Redis](persistencia-redis.md): armazenamento e preservação de dados.
- [API HTTP](api-http.md): criação, consulta e gerenciamento de jobs.
- [Dashboard](dashboard.md): acompanhamento pela interface.
- [Processamento](processamento.md): garantias de recuperação e parada.
- [Testes e validação](testes-e-validacao.md): execução das suítes.
