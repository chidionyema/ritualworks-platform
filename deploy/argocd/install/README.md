# ArgoCD Bootstrap

One-time install for a fresh cluster. After this, ArgoCD itself is the
source of truth for everything else (including its own configuration via
the App-of-Apps pattern).

## On a kind cluster

```bash
# 1. Create the cluster (delegated to Makefile / kind config)
make k8s-up

# 2. Install ArgoCD into the argocd namespace
kubectl create namespace argocd
kubectl apply -n argocd -f https://raw.githubusercontent.com/argoproj/argo-cd/v2.13.0/manifests/install.yaml

# 3. Wait for the argocd-server Deployment to roll out
kubectl rollout status -n argocd deployment/argocd-server --timeout=5m

# 4. Apply the App-of-Apps — ArgoCD takes over from here
kubectl apply -f deploy/argocd/applications/app-of-apps.yaml
```

The App-of-Apps reconciles every Application manifest under
`deploy/argocd/applications/`. Each Application points at one Helm chart
under `deploy/helm/`.

## Accessing the dashboard

```bash
kubectl port-forward -n argocd svc/argocd-server 8080:443
# then open https://localhost:8080
# admin password:
kubectl -n argocd get secret argocd-initial-admin-secret \
  -o jsonpath="{.data.password}" | base64 -d
```
