#!/usr/bin/env bash
set -euo pipefail

# Automated parallel security fix execution via Gemini CLI
# Usage: ./scripts/run-security-fixes.sh
# Requires: gemini CLI installed, git worktrees supported

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SPEC="$REPO_ROOT/docs/EDGE-CASE-AUDIT-SPEC.md"
PLAN="$REPO_ROOT/docs/PARALLEL-AGENT-PLAN.md"
RULES="$REPO_ROOT/.claude/projects/-Users-chidionyema-Documents-code-ritualworks-platform/memory/security-rules.md"
BASE_BRANCH="$(git -C "$REPO_ROOT" rev-parse --abbrev-ref HEAD)"

AGENTS=(
  "1:payments:src/Payments,tests/Payments"
  "2:orders-checkout:src/Orders,tests/Orders,src/CheckoutOrchestrator,tests/CheckoutOrchestrator"
  "3:catalog:src/Catalog,tests/Catalog"
  "4:identity:src/Identity,tests/Identity"
  "5:bffweb-content:src/BffWeb,tests/BffWeb,src/Content,tests/Content"
  "6:location-scheduler-notifications:src/Location,tests/Location,src/Scheduler,tests/Scheduler,src/Notifications,tests/Notifications"
  "7:payouts-privacy-webhooks:src/Payouts,tests/Payouts,src/Privacy,tests/Privacy,src/Webhooks,tests/Webhooks"
)

WORKTREE_DIR="$REPO_ROOT/../security-fix-worktrees"
LOG_DIR="$REPO_ROOT/logs/security-fixes"
mkdir -p "$LOG_DIR"

echo "=== Security Fix Automation ==="
echo "Base branch: $BASE_BRANCH"
echo "Worktree dir: $WORKTREE_DIR"
echo ""

# Phase 1: Create worktrees
echo "--- Phase 1: Creating worktrees ---"
mkdir -p "$WORKTREE_DIR"
for agent_def in "${AGENTS[@]}"; do
  IFS=: read -r num name scope <<< "$agent_def"
  branch="fix/security-agent-$num-$name"
  wt="$WORKTREE_DIR/agent-$num"

  if [ -d "$wt" ]; then
    echo "  Worktree agent-$num already exists, skipping"
  else
    git -C "$REPO_ROOT" worktree add "$wt" -b "$branch" "$BASE_BRANCH" 2>/dev/null || \
    git -C "$REPO_ROOT" worktree add "$wt" "$branch" 2>/dev/null || \
    { echo "  WARN: Could not create worktree for agent-$num"; continue; }
    echo "  Created worktree: agent-$num ($branch)"
  fi
done
echo ""

# Phase 2: Generate per-agent prompts
echo "--- Phase 2: Generating agent prompts ---"
PROMPT_DIR="$LOG_DIR/prompts"
mkdir -p "$PROMPT_DIR"

RULES_CONTENT="$(cat "$RULES" 2>/dev/null || echo 'See docs/EDGE-CASE-AUDIT-SPEC.md')"
SPEC_CONTENT="$(cat "$SPEC")"
PLAN_CONTENT="$(cat "$PLAN")"

for agent_def in "${AGENTS[@]}"; do
  IFS=: read -r num name scope <<< "$agent_def"

  cat > "$PROMPT_DIR/agent-$num.md" << PROMPT_EOF
# Security Fix Agent $num: $name

## Mandatory Coding Rules
$RULES_CONTENT

## Your Assignment (Agent $num)
You MUST only modify files within these paths:
$(echo "$scope" | tr ',' '\n' | sed 's/^/- /')

Extract the "Agent $num" section from the plan below and execute every task listed.

## Parallel Agent Plan
$PLAN_CONTENT

## Full Issue Specifications
$SPEC_CONTENT

## Instructions
1. Read each issue assigned to your agent from the spec
2. Fix the production code at the exact file:line specified
3. Write the test specified for each fix
4. After ALL fixes, run: dotnet build && dotnet test for your service test projects
5. Do NOT touch files outside your assigned scope — other agents own those
6. Commit with message: fix(security): agent-$num {service} — {brief summary}
7. If a fix requires a shared file (e.g., Contracts), document what's needed but do NOT modify it
PROMPT_EOF

  echo "  Generated prompt: agent-$num.md"
done
echo ""

# Phase 3: Launch agents in parallel
echo "--- Phase 3: Launching agents ---"
PIDS=()

for agent_def in "${AGENTS[@]}"; do
  IFS=: read -r num name scope <<< "$agent_def"
  wt="$WORKTREE_DIR/agent-$num"
  prompt="$PROMPT_DIR/agent-$num.md"
  log="$LOG_DIR/agent-$num.log"

  if [ ! -d "$wt" ]; then
    echo "  SKIP agent-$num: worktree missing"
    continue
  fi

  echo "  Launching agent-$num ($name) ..."

  # Try gemini CLI first, fall back to claude
  if command -v gemini &>/dev/null; then
    (cd "$wt" && gemini -p "$(cat "$prompt")" > "$log" 2>&1) &
  elif command -v claude &>/dev/null; then
    (cd "$wt" && claude -p "$(cat "$prompt")" --allowedTools Edit,Read,Glob,Grep,Bash,Write > "$log" 2>&1) &
  else
    echo "  ERROR: Neither 'gemini' nor 'claude' CLI found in PATH"
    exit 1
  fi

  PIDS+=($!)
done

echo ""
echo "All ${#PIDS[@]} agents launched. PIDs: ${PIDS[*]}"
echo "Logs: $LOG_DIR/agent-*.log"
echo ""

# Phase 4: Wait for completion
echo "--- Phase 4: Waiting for agents ---"
FAILED=0
for i in "${!PIDS[@]}"; do
  pid=${PIDS[$i]}
  num=$((i + 1))
  if wait "$pid"; then
    echo "  Agent $num: DONE"
  else
    echo "  Agent $num: FAILED (exit $?)"
    FAILED=$((FAILED + 1))
  fi
done
echo ""

# Phase 5: Collect results
echo "--- Phase 5: Results ---"
for agent_def in "${AGENTS[@]}"; do
  IFS=: read -r num name scope <<< "$agent_def"
  wt="$WORKTREE_DIR/agent-$num"
  if [ -d "$wt" ]; then
    commits=$(git -C "$wt" log "$BASE_BRANCH..HEAD" --oneline 2>/dev/null | wc -l | tr -d ' ')
    changed=$(git -C "$wt" diff "$BASE_BRANCH" --stat 2>/dev/null | tail -1)
    echo "  Agent $num ($name): $commits commits — $changed"
  fi
done
echo ""

if [ "$FAILED" -gt 0 ]; then
  echo "WARNING: $FAILED agent(s) failed. Check logs in $LOG_DIR/"
  exit 1
fi

echo "=== All agents completed successfully ==="
echo ""
echo "Next steps:"
echo "  1. Review each branch: git log fix/security-agent-{1..7}-*"
echo "  2. Run full test suite: dotnet test RitualworksPlatform.sln"
echo "  3. Merge branches:"
echo "     for i in 1 2 3 4 5 6 7; do"
echo "       git merge fix/security-agent-\$i-* --no-ff"
echo "     done"
echo "  4. Clean up: git worktree prune"
