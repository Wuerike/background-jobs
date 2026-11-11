DOCKER ?= $(shell if command -v docker.exe >/dev/null 2>&1; then echo docker.exe; else echo docker; fi)
export REDIS_CONNECTION ?= 127.0.0.1:$(or $(REDIS_PORT),6379)

.PHONY: run

run:
ifeq ($(REDIS_CONNECTION),127.0.0.1:$(or $(REDIS_PORT),6379))
	mkdir -p "$${DATA_DIR:-./data}"
	WSLENV="$${WSLENV:+$$WSLENV:}REDIS_PORT:DATA_DIR/p" $(DOCKER) compose up -d --wait redis
endif
	dotnet msbuild build.proj -t:Package
	dotnet watch --project src/BackgroundJobs.Demo run --no-launch-profile
