# Token-efficient wave briefs

How to write briefs that get the same work done in 2–5× fewer agent tokens. **The wave protocol is built on the premise that humans plan and agents execute — every token an agent spends planning is a leak.**

## Where the tokens actually go

Hitting a quota wall almost never means "the work was too big." It means agents spent most of their context window:

1. **Exploring** the codebase to find what the brief should have already given them — directory listings, grep + read + grep + read cycles
2. **Re-deriving** decisions the brief should have made — "where should this type live? what's it called?"
3. **Reading whole files** when a 20-line snippet would have sufficed
4. **Building up internal context** ("let me first understand the existing patterns…") before doing anything
5. **Producing long reasoning prose** between tool calls
6. **Iterating on builds** that fail because of small errors that could have been spec'd away

A well-written brief eliminates 1–5 entirely. The agent reads the brief once, reads the one reference file the brief points at, writes the code, runs the Done check, commits. Token spend is a small multiple of the diff size — not the repo size.

## The 10 rules of token-efficient briefs

In rough priority order — top of the list saves the most tokens.

### 1. Inline what the agent would read anyway

Every brief says "Reference to mirror: `src/Audit/.../X.cs`". For a 700-line file, that's tens of thousands of tokens the agent reads, parses, summarises. **Most of the file is irrelevant to the track.**

Better:

> **Reference (inline excerpt, lines 40–80 of `src/Audit/.../X.cs`):**
> ```csharp
> public sealed class XConsumer : IConsumer<IDomainEvent>
> {
>     private readonly IXService _service;
>     public async Task Consume(ConsumeContext<IDomainEvent> ctx) { … }
> }
> ```

Now the agent has the pattern without the file read. Save: thousands of tokens per track.

### 2. List exact file paths to create / edit; forbid everything else

A track says **`Files you own (exclusive)`** — turn that list into a literal "you may only write to these paths" instruction. Combined with `Files you may NOT touch`, agent never explores. Agent goes directly to the listed paths.

> **You will write or modify EXACTLY these files. Do not read or grep elsewhere unless required for compilation:**
> - `src/X/X.Application/Foo.cs` (new)
> - `src/X/X.Application/DependencyInjection.T1.cs` (existing; only modify the empty stub body)

Save: hundreds of grep/find/ls tool calls.

### 3. State the exact symbol names + signatures, don't make the agent design

Instead of "implement a service that handles refund completion," say:

> Create class `RefundCompletedHandler` in `src/X/Foo.cs` with signature:
>
> ```csharp
> internal sealed class RefundCompletedHandler(IRefundRepository repo) : INotificationHandler<RefundCompletedEvent>
> {
>     public async Task Handle(RefundCompletedEvent e, CancellationToken ct) { … }
> }
> ```
>
> The body should: 1) load refund by id, 2) mark as `Status=Completed`, 3) persist via `repo.SaveAsync(refund, ct)`.

The agent fills in 4 lines of body. No design decisions, no naming debates, no "let me see how other handlers look."

Save: 50–80% of the design-thinking tokens.

### 4. Pre-commit boilerplate via deterministic tooling

Anything templatable should be done by the wave TOOL (bash, scaffolds), not by the agent. The current scaffold step (`apply_scaffold` + `generate_di`) does this for L0; extend the same principle wherever possible:

- New EF migration boilerplate? Generate the file skeleton; agent fills in only the `Up()` / `Down()` SQL.
- Standard test class shape? Template it; agent fills in the test method bodies only.
- Per-track DI stub? Template it (already done).

If the wave tool can produce it deterministically, **the agent should never see that work in its brief**.

Save: 30–50% of code-writing tokens per track.

### 5. Keep tracks small. 2–4 hours, not 6+.

A 6-hour track means the agent accumulates 6 hours of context — files read, errors hit, retries, intermediate state. Each tool call carries the growing context along.

Split big tracks into 2–3 smaller tracks with tighter scope. With 8 agents running in parallel, 3 small tracks finish at the same wall-clock as 1 big track but with much less per-agent context.

Save: linear in track size. A 2h track uses ~⅓ the tokens of a 6h equivalent.

### 6. Forbid scope creep explicitly

A brief that doesn't say "don't refactor, don't rename, don't optimise" invites the agent to spend tokens on improvements you didn't ask for. Add to Universal rules:

> **No tangential edits.** If you notice an obvious improvement outside the listed file paths, write it as a `// TODO(track-Tn)` comment in YOUR file and continue. Do NOT change unrelated code. The PR review can decide whether to act on the TODO.

Save: variable, often substantial. One agent's "while I'm here" cleanup can blow a whole quota.

### 7. Provide the Done command verbatim; no discovery

A Done check like "run the tests" forces the agent to figure out which tests, where, how to filter. Provide the exact command:

> **Done:** `dotnet test "$WT/tests/Audit/Audit.Unit/Audit.Unit.csproj" -c Release --nologo --filter "FullyQualifiedName~OverrideHonored"`

No discovery. The agent runs that exact string. If it exits 0, the track is done.

Save: the cost of figuring out test layout / project names / filter syntax — usually 1–2k tokens per track.

### 8. Skip the "preamble" — instruct agents to act, not narrate

Agents default to producing reasoning prose between tool calls ("Let me first understand the structure…", "I'll need to check…"). At quota margins, this is pure waste.

Add to Universal rules:

> **Skip preamble.** Don't narrate your plan; execute it. Don't explain decisions the brief already made. Output text should be limited to: progress signals (one short line per file group), error reports, and the final Done-Report. Internal reasoning stays internal.

Save: 10–30% of total tokens depending on agent verbosity.

### 9. Pre-build / pre-warm shared state

The first `dotnet build` on a fresh worktree compiles every dependency. With 8 agent worktrees, that's 8× the build work. The wave tool's L0 scaffold step already runs `dotnet build` once — the `bin/obj` for shared dependencies is then on disk. But each agent worktree is its own filesystem location; bins don't transfer.

Workaround: use `--use-current-runtime` or shared `NUGET_PACKAGES` env var so NuGet cache is global, not per-worktree. Pre-restore at L0 time so agents start with a warm cache.

Save: each agent saves ~1–2 minutes (and the tokens for "waiting for the build" reasoning).

### 10. Don't make the agent read the brief twice

Long briefs (this one is one) force agents to re-read sections during execution. Keep brief structure tight enough that the whole brief fits in one read. As briefs grow past ~3000 tokens, structure them so the *track section* (what THIS agent does) is self-contained — universal rules + reference can be skim-only once at the start.

The current parallel-tracks template format is close to this — refine by pushing more concrete instruction into each track's section instead of into the universal rules.

## The brief lifecycle, with tokens annotated

```
                                              Tokens per agent
                                                (rough order)
operator writes spec → wave run                        ~0
  └─ wave script: deterministic scaffold +             ~0   (bash, not an LLM)
     DI gen + Aspire / fly / bootstrap wiring +
     dotnet build of L0
  └─ gemini design pass: 1 invocation, reads          ~30k  (one-time)
     spec + protocol + example, writes brief
  └─ wave run launches 8 agents
       └─ each agent reads brief                       ~5k  (depends on brief size)
       └─ each agent reads ONE reference file          ~3k  (inline excerpts cut this)
       └─ each agent writes code (the actual work)    ~10k  (proportional to diff)
       └─ each agent runs Done check                   ~1k
       └─ each agent commits + pushes                  ~1k
                                                     ─────
                                            per-agent  ~20k  (target — see below)

  TOTAL per wave (8 agents + 1 design pass)          ~190k

Today (without the optimisations in this doc):
  per-agent                                           ~70k  (lots of exploration)
  TOTAL                                              ~590k
```

**Target: 70k → 20k per agent.** That's the ~3× improvement that fits the same work into a tighter quota.

## What the brief author actually has to do differently

The work moves up the value chain. The operator (or design-pass LLM) does more upfront thinking; agents do less.

Per track in a brief, additions to today's structure:

- An inline 20–40-line **code snippet** the agent should write (signature + skeleton; agent fills bodies).
- An inline **excerpt** from the reference file, not a path to read.
- An exact **Done command**.
- A "**don't explore**" instruction reinforcing universal rules.
- A "**no scope creep**" reminder.

It's more upfront work for the operator. It pays back 4–5× in agent quota.

## Operationally — what to change today

The wave tool's design prompt already produces structured briefs. To make briefs token-efficient by default, update `cmd_run`'s design prompt to instruct the design-pass LLM:

1. Inline 20–40 lines of the reference file directly under "**Reference (inline excerpt):**", not just the path.
2. Include a code skeleton (signature + body comments) for each new file each track owns.
3. Specify the exact Done command, not a descriptive one.
4. Add a universal "no exploration, no preamble, no scope creep" rule to every brief.

Plus a `wave audit-brief <feature>` subcommand that scores a brief on these dimensions before launch, flagging "this brief will be expensive" early.

## When token efficiency matters most

| Scenario | Concern level |
|---|---|
| Daily development on a known stack | Low — quota usually fine |
| First wave on a new spec | Medium |
| Multi-track wave with 8+ agents simultaneously | **High** — quotas burn fast |
| Long tracks (4+ hours per agent) | **High** |
| Mode A (single big agent doing all the work) | **Very high** — no parallelism, one agent's bloat hits hard |
| Recovery wave after a chaos/incident | High — extra context required |

Make briefs efficient by default; the gain compounds across waves.

## The discipline phrase

> *Operators plan; agents execute. Every token an agent spends planning is a defect in the brief.*

When you hit a quota, the first question is "where did the brief leak tokens?" not "do I need a bigger quota?"
