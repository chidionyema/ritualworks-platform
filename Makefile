.PHONY: help dev k8s-up k8s-down k8s-smoke local-smoke refresh-images

help: ## Show this help
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-20s\033[0m %s\n", $$1, $$2}'

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
