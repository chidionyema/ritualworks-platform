#!/usr/bin/env bash
# Architectural fitness check — runs in CI on every PR.
#
# Rules enforced:
#   1. No service may have a <ProjectReference> to another service's csproj.
#      Allowed peers: BuildingBlocks, BuildingBlocks.Testing, Contracts, the
#      service's own internal sub-projects.
#   2. Only listed cross-cutting services are scanned for the "no using
#      Haworks.Contracts.<Other>" rule (warning-only for now — domain services
#      legitimately consume cross-domain events; tighten as the platform
#      decouples).
#
# Exit codes:
#   0   no violations
#   1   at least one project-reference violation (hard fail)
#   2   only soft-warning violations (still passes; warning printed)

set -euo pipefail

REPO_ROOT=${1:-$(git rev-parse --show-toplevel)}
cd "$REPO_ROOT"

# Cross-cutting services subject to BOTH rules. Domain services subject only
# to rule 1 (the project-reference rule).
CROSS_CUTTING=(Audit Notifications Payments Search Content Identity)
DOMAIN=(Catalog Orders CheckoutOrchestrator BffWeb)
ALL_SERVICES=("${CROSS_CUTTING[@]}" "${DOMAIN[@]}")

# Allowed reference targets (substrings matched against the ProjectReference path)
ALLOWED_PEERS=(
  "src/BuildingBlocks/Haworks.BuildingBlocks.csproj"
  "src/BuildingBlocks.Testing/Haworks.BuildingBlocks.Testing.csproj"
  "src/Contracts/Haworks.Contracts.csproj"
)

errors=0
warnings=0

# ── RULE 1: project-reference graph ──────────────────────────────────────
# Approach: each reference is a relative path from a csproj. We don't try
# to resolve absolute paths (BSD realpath lacks --relative-to). Instead we
# match on the SUFFIX: a reference is allowed if it ends in one of the
# allowed peer paths or contains "/<this-svc>." (own internal subproject).
echo "[1/2] Project-reference rule (hard)"
for svc in "${ALL_SERVICES[@]}"; do
  [ -d "src/$svc" ] || continue
  while IFS= read -r csproj; do
    csproj_name=$(basename "$csproj")
    while IFS= read -r ref; do
      allowed=0
      # Normalize Windows-style backslashes (some csprojs use them)
      ref_unix=${ref//\\//}
      # Allowed: BuildingBlocks / BuildingBlocks.Testing / Contracts (by suffix)
      case "$ref_unix" in
        *BuildingBlocks/Haworks.BuildingBlocks.csproj)                 allowed=1 ;;
        *BuildingBlocks.Testing/Haworks.BuildingBlocks.Testing.csproj) allowed=1 ;;
        *Contracts/Haworks.Contracts.csproj)                           allowed=1 ;;
      esac
      # Allowed: internal sub-project of the same service.
      case "$ref_unix" in
        */${svc}.Domain/*)         allowed=1 ;;
        */${svc}.Application/*)    allowed=1 ;;
        */${svc}.Infrastructure/*) allowed=1 ;;
        */${svc}.Api/*)            allowed=1 ;;
      esac

      if [ "$allowed" -eq 0 ]; then
        echo "  ✗ FAIL: $csproj_name → $ref"
        errors=$((errors + 1))
      fi
    done < <(grep -oE 'ProjectReference Include="[^"]+"' "$csproj" | sed 's|ProjectReference Include="||;s|"$||')
  done < <(find "src/$svc" -name "*.csproj" -not -path "*/bin/*" -not -path "*/obj/*")
done
[ "$errors" -eq 0 ] && echo "  ✓ no cross-service project references"

# ── RULE 2: cross-cutting services should not pull other services' Contracts subdomains ──
echo ""
echo "[2/2] Cross-cutting service decoupling (soft — coupling tracked, not failed)"
for svc in "${CROSS_CUTTING[@]}"; do
  [ -d "src/$svc" ] || continue
  bad=$(find "src/$svc" -name "*.cs" -not -path "*/bin/*" -not -path "*/obj/*" \
         | xargs grep -hE "^using Haworks\.Contracts\.[A-Z]\w+;" 2>/dev/null \
         | sort -u | grep -v "Haworks\.Contracts\.${svc};" \
         | grep -v "^using Haworks\.Contracts;" || true)
  if [ -n "$bad" ]; then
    echo "  ⚠ $svc imports cross-domain Contracts namespaces:"
    echo "$bad" | sed 's|^|      |'
    warnings=$((warnings + 1))
  fi
done
[ "$warnings" -eq 0 ] && echo "  ✓ all cross-cutting services consume only abstract / own-namespace contracts"

# ── RULE 3: no raw Testcontainers in integration tests ──────────────────
# All integration tests must use SharedTestPostgres / SharedTestElasticsearch /
# SharedTestPostGIS from BuildingBlocks.Testing.Containers instead of spinning
# up their own containers. Raw container usage wastes CI time and resources.
echo ""
echo "[3/3] Shared Testcontainers enforcement (hard)"
# Exclude BuildingBlocks.Testing itself (it defines the shared containers)
# and Vault-specific tests (they test Vault container integration directly).
raw_containers=$(grep -rn "new PostgreSqlBuilder\|new ContainerBuilder\|new RabbitMqBuilder\|new ElasticsearchBuilder" \
  tests/ --include="*.cs" \
  --exclude-dir=bin --exclude-dir=obj \
  2>/dev/null \
  | grep -v "BuildingBlocks.Testing\|BuildingBlocks.Tests/Vault" \
  || true)
if [ -n "$raw_containers" ]; then
  echo "  ✗ FAIL: raw Testcontainers usage found — use SharedTest* from BuildingBlocks.Testing.Containers:"
  echo "$raw_containers" | sed 's|^|      |'
  errors=$((errors + $(echo "$raw_containers" | wc -l)))
else
  echo "  ✓ all integration tests use shared Testcontainers"
fi

echo ""
echo "summary: $errors hard violations, $warnings soft warnings"
# Hard violations fail CI. Warnings are tracked but don't block — they
# represent coupling that exists today; tighten the rule to fail-on-warning
# once the inventory in docs/architecture/cross-cutting-coupling-audit.md
# is cleared.
[ "$errors" -gt 0 ] && exit 1
exit 0
