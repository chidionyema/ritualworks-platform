.PHONY: help setup dev test build build-full lint k8s-up k8s-down k8s-smoke local-smoke refresh-images

help: ## Show this help
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-20s\033[0m %s\n", $$1, $$2}'

setup: ## First-time setup: install git hooks + restore packages
	./scripts/install-git-hooks.sh
	dotnet restore HaworksPlatform.sln

build: ## Fast build (no analyzers): make build or make build svc=identity
	$(if $(svc),dotnet build filters/$(shell echo $(svc) | sed 's/.*/\u&/').slnf,dotnet build HaworksPlatform.sln)

build-full: ## Full build with all analyzers (same as CI)
	HAWORKS_ANALYZERS=true dotnet build HaworksPlatform.sln

lint: ## Run analyzers only (without full rebuild)
	HAWORKS_ANALYZERS=true dotnet build HaworksPlatform.sln -m

test: ## Run tests for one service: make test svc=identity [mode=unit|integration]
	./scripts/test.sh $(svc) $(or $(mode),all)

dev: ## Start the full stack via Aspire (one process tree, live reload)
	dotnet run --project deploy/aspire

k8s-up: ## Provision kind cluster + ArgoCD + sync everything
	./scripts/k8s-up.sh

k8s-down: ## Tear down the kind cluster
	kind delete cluster --name haworks

k8s-smoke: ## Run cross-system smoke against kind cluster
	./scripts/k8s-smoke.sh

local-smoke: ## Run cross-system smoke against running Aspire stack
	./scripts/local-deploy-smoke.sh

refresh-images: ## Pull latest service images and update infra/image-digests.lock
	./scripts/refresh-images.sh

demo-saga-failure: ## Headline chaos demo — kill payments mid-checkout, watch saga compensate
	./scripts/demo-saga-failure.sh
