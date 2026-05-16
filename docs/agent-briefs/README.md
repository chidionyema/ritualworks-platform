# Agent Briefs

Self-contained instructions for autonomous coding agents (Gemini, Claude, etc.) to do focused work in this repo without needing the originating chat context.

Each brief includes:
- The mission and what "done" looks like
- Conventions and anti-patterns specific to this codebase
- A worked example
- A sequenced work queue with explicit source/target paths
- Acceptance criteria + escalation rules

## Parallel-agent waves (Mode B)

For features that decompose into independent parallel tracks (file-scope-disjoint work), use the **`wave`** tool to launch N agents that self-claim tracks via the stress-tested SUPERPROMPT.md Mode B protocol.

```bash
./docs/agent-briefs/wave init <feature>          # scaffold parallel-tracks.md from _TEMPLATE
# edit the brief: fill in Parameters block, file scopes, Done checks
./docs/agent-briefs/wave validate <feature>      # check well-formedness
./docs/agent-briefs/wave launch <feature>        # spawn N gemini agents in tmux
./docs/agent-briefs/wave status <feature>        # one-line-per-track dashboard
./docs/agent-briefs/wave fix-stalled <feature>   # open PRs for stuck branches
```

Worked example: `audit/parallel-tracks.md` (4 tracks, all merged via this protocol).

Prerequisites for a parallelizable feature:
1. **L0 scaffold merged.** Interfaces, project skeletons, and DI orchestrator must already be on the integration branch.
2. **Disjoint file scopes.** Each track must own files no other track touches. Shared files (DI orchestrator, csproj) get written ONCE by L0 and frozen.
3. **Reference implementation exists.** A canonical file in the repo whose patterns each track mirrors.
4. **Per-track Done check.** A shell command that exits 0 only when that track is correct.

If any of these fail, the feature is not yet ready for a wave — do an L0 scaffold first.

## Brief status

| Brief | Status |
|---|---|
| [`audit-service-spec.md`](./audit-service-spec.md) | Complete — v1 deployed |
| [`audit-decoupling-spec.md`](./audit-decoupling-spec.md) | Complete — event-shape-agnostic |
| [`platform-completion-spec.md`](./platform-completion-spec.md) | Complete — auth + reservations shipped |
| [`webhooks-service-spec.md`](./webhooks-service-spec.md) | Complete — deployed |
| [`test-port-from-monolith.md`](./test-port-from-monolith.md) | In progress |
| [`location-service-spec.md`](./location-service-spec.md) | Draft — pending review |
| [`cdc-service-spec.md`](./cdc-service-spec.md) | Deferred — see [BACKLOG.md](../BACKLOG.md) |
| [`k8s-platform-spec.md`](./k8s-platform-spec.md) | Vision — not yet implemented |
