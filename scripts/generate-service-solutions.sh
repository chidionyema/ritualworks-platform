#!/usr/bin/env bash
set -uo pipefail

# Generates per-service .sln files in the repo root.
# Each includes: the service's 4 layers + BuildingBlocks + Contracts + test projects.
# Master solution (RitualworksPlatform.sln) remains untouched for cross-cutting work.

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

SHARED_PROJECTS=(
    "src/BuildingBlocks/Haworks.BuildingBlocks.csproj"
    "src/Contracts/Haworks.Contracts.csproj"
    "src/BuildingBlocks.Testing/Haworks.BuildingBlocks.Testing.csproj"
)

SERVICES=(Audit BffWeb Catalog CheckoutOrchestrator Content Identity Location Merchant Notifications Orders Payments Payouts Pricing Privacy Scheduler Search Webhooks)

for svc in "${SERVICES[@]}"; do
    sln_file="${svc}.sln"
    
    # Create fresh solution
    dotnet new sln -n "$svc" --force -o . > /dev/null 2>&1
    
    # Add shared projects
    for proj in "${SHARED_PROJECTS[@]}"; do
        [ -f "$proj" ] && dotnet sln "$sln_file" add "$proj" > /dev/null 2>&1 || true
    done
    
    # Add service projects (all layers)
    find "src/$svc" -name "*.csproj" -not -path "*/obj/*" 2>/dev/null | while read -r proj; do
        dotnet sln "$sln_file" add "$proj" > /dev/null 2>&1 || true
    done
    
    # Add test projects
    for test_dir in "tests/$svc" "tests/${svc}.Unit" "tests/${svc}.Integration" "tests/${svc}.Architecture" "tests/${svc}.Contract"; do
        find "$test_dir" -name "*.csproj" -not -path "*/obj/*" 2>/dev/null | while read -r proj; do
            dotnet sln "$sln_file" add "$proj" > /dev/null 2>&1 || true
        done
    done
    
    # Add platform guard tests (always)
    dotnet sln "$sln_file" add "tests/Platform.ArchitecturalGuards/Platform.ArchitecturalGuards.csproj" > /dev/null 2>&1 || true
    
    proj_count=$(dotnet sln "$sln_file" list 2>/dev/null | grep -c ".csproj" || echo 0)
    echo "  $sln_file — $proj_count projects"
done

echo ""
echo "Done. Per-service solutions generated."
echo "Usage: dotnet build Payments.sln    # ~15s vs ~90s for full solution"
echo "       dotnet test Payments.sln     # only Payments tests"
