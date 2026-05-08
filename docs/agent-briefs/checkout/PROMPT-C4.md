You are a coding agent. Read carefully — these instructions are the contract.

================================================================
STEP 1 — SETUP (idempotent, safe to re-run, parallel-safe via git worktree)
================================================================
Run these shell commands in order. If any fail, STOP and report.

  set -euo pipefail
  REPO_ROOT=/Users/chidionyema/Documents/code/ritualworks-platform
  BRIEF_FILE=C4-checkout-session-expired-consumer.md
  BRIEF_ID=C4
  cd "$REPO_ROOT"

  WORKTREE="$REPO_ROOT/../rw-$BRIEF_ID"
  BRANCH="feat/checkout-fills/$BRIEF_ID"
  BASE_BRANCH=main

  git fetch origin --prune

  if [ ! -d "$WORKTREE" ]; then
      git worktree add "$WORKTREE" -b "$BRANCH" "origin/$BASE_BRANCH"
  fi
  cd "$WORKTREE"

  CURRENT=$(git rev-parse --abbrev-ref HEAD)
  [ "$CURRENT" = "$BRANCH" ] || { echo "ERROR: expected $BRANCH, on $CURRENT" >&2; exit 1; }

  # Precondition: orders-svc must exist with a consumer pattern to mirror.
  if [ ! -f "src/Orders/Orders.Application/Consumers/PaymentCompletedConsumer.cs" ]; then
      echo "BLOCKER: orders-svc PaymentCompletedConsumer not found — pattern template missing. STOP." >&2
      exit 1
  fi

  echo "Worktree ready: $WORKTREE on $BRANCH"

================================================================
STEP 2 — READ (in this order, in full, BEFORE WRITING ANYTHING)
================================================================
  1. docs/agent-briefs/checkout/README.md                                    (protocol)
  2. docs/agent-briefs/checkout/C4-checkout-session-expired-consumer.md      (your specific task)

Then read every file in the brief's "Inputs" section, in order listed.
Do not grep blindly. Do not skim.

================================================================
STEP 3 — EXECUTE
================================================================
  • Touch only files in the brief's "Deliverable" list.
  • If a file already exists with the correct content, SKIP.
  • The ONE existing-file modification is
    src/Orders/Orders.Infrastructure/DependencyInjection.cs — add a single
    mt.AddConsumer<CheckoutSessionExpiredConsumer>() line in the existing
    AddMassTransit block. Nothing else.
  • Atomic-mark-before-stock-release ordering is load-bearing — do not invert it.
  • Run every command in "Acceptance". ALL must pass.
  • Commit locally:
        git add <files-from-deliverable>
        git commit -m "feat(orders): C4 — <one-line summary>"

================================================================
STEP 4 — HARD STOPS
================================================================
  ✗ git push / git push --force
  ✗ git commit --amend / --no-verify / --no-gpg-sign
  ✗ git rebase / git reset --hard
  ✗ Modifying main, origin, or any branch other than feat/checkout-fills/C4
  ✗ Editing files outside src/Orders/, src/Contracts/Payments/CheckoutSessionExpiredEvent.cs (read-only verification only), and tests/Orders.*
  ✗ Editing the Order aggregate or OrderStatus enum
  ✗ Adding new methods to IOrderRepository — file a blocker if the atomic mark method is missing
  ✗ Editing payments-svc or Stripe processor (publish path is already wired)
  ✗ Inventing new contract events that don't exist in src/Contracts/
  ✗ Editing CI workflow files
  ✗ flyctl deploy / flyctl secrets / flyctl scale
  ✗ Opening PRs / auto-merging
  ✗ Continuing past 30 minutes — emit a blocker and stop

Allowed:
  ✓ Local file create/edit per the brief
  ✓ Running tests, builds, formatters, linters
  ✓ Local git add / git commit (no amend, no force)

================================================================
STEP 5 — OUTPUT
================================================================
Done-report or blocker per docs/agent-briefs/checkout/README.md.
No prose outside those formats.

BEGIN.
