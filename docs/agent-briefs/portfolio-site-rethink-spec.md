# Portfolio site — massive rethink

**Status:** spec — not yet implemented.

**Repo:** `/Users/chidionyema/Documents/code/portfolio-site/` (Astro 4 + React + Tailwind, Cloudflare Pages).

**Mode for wave run:** modify-existing (it's not a new microservice; it's the existing Astro site getting an editorial pass).

## 1. Goal & non-goals

### Goal

The backend is rigorous: race-safe sagas, transactional outbox, chaos-tested compensation, an architecture check, three rollup PRs of decoupling work, 36 test projects across 11 service groups, documented every step. **The portfolio site does not reflect that discipline.** Hiring managers look at the site for ~30 seconds before forming an impression. Today the site loads, but it's information-dense to the point of cognitive overload — 5 sections, 15 demo components, 17 architecture components, chaos timelines, scoreboards, sparklines, metrics panels — and the visual polish trails the architectural ambition.

This spec resets the site to the level of taste, restraint, and quality the backend deserves. Not a rewrite — an editor's pass + an engineering pass + a content pass.

### Non-goals

- Rebuilding from scratch in a different framework. Astro is the right substrate.
- Removing the live-cluster demo concept. That's the differentiator. Polish, don't kill it.
- Adding more features. The fix is fewer features done better.
- Changing the editorial direction (light cream + Fraunces display + Inter body). The direction is good; execution lags.

## 2. The current state — honest diagnosis

Captured 2026-05-11 from inspection.

| What | Today | Smell |
|---|---|---|
| **Pages** | One `index.astro` (recently collapsed from many) + `deep-dives/[slug]` | Single page is fine; **everything-on-one-page** loses focus |
| **Sections** | Hero + Cluster + Demos + Deep Dives + About + Contact | 5 sections on top of 15 demo cards is a lot for one scroll |
| **Components rendered on index** | 15 demo + 17 architecture + 7 system + 4 hero + 1 quality | **44 component types on one page.** Pure overload. |
| **Lighthouse perf** | threshold 0.85 (was 0.90; relaxed) | Bar lowered, not bugs fixed |
| **Lighthouse a11y** | threshold 0.85 (was 1.0, then 0.95, then 0.85) | **Three relaxations.** Real regressions. |
| **Tests** | 0 (in `src/`) | Total gap |
| **Tailwind classnames** | 1182 across 108 files | Significant ad-hoc styling; no extracted variant components |
| **Parallel worktrees** | 9 (`cache-inval`, `eventflow`, `circuit-breaker`, `vault-swap`, `concurrency`, `rate-limiter`, `copy-rewrite`, `claude`) | Fragmentation; no canonical "main" |
| **GEMINI.md** | Present | Indicates ad-hoc Gemini-driven work without the wave protocol's structure |
| **CSS variables tokens** | Real, proper Tailwind config with light/dark surface tokens | **One thing done well.** |
| **UI_AND_DEMO_PLAN.md** | 288 lines, dated 2026-05-04 | Plan exists; execution didn't keep up |

**The verdict:** the concept is sound, the planning artifact exists, the design tokens are right, but **execution + polish are inconsistent and the bar has been relaxed multiple times to ship.**

## 3. Principles for the rethink

In rough priority order:

1. **Less is more.** Cut the page down. Hero + ONE strong demo + clear deep-dive entry points + contact. Everything else moves to dedicated pages or gets deferred.
2. **Restraint > density.** Each section earns its space by being undeniably useful. White space is allowed. Information density is not the product.
3. **Hiring manager first, technical reviewer second.** First 5 seconds decide if you're hired-or-not. The live cluster demo is for the technical reviewer who's already convinced you might be the right person — it's at the bottom, not the top.
4. **Restore the bar.** Lighthouse a11y back to 1.0 + perf to ≥0.95. If something fails the bar, the change is reverted — not the threshold.
5. **The site IS a test.** Every claim about engineering discipline is verified by the site itself meeting its own standards.
6. **Mobile-equal.** Hiring managers open links on their phone. Mobile is the canonical view; desktop is the variant.
7. **Zero JavaScript where possible.** Astro's superpower. Demos can be islands; copy is static.

## 4. The new IA (information architecture)

```
/                                  Hero + value prop + ONE proof + CTA + footer
├── /work                          Case studies — 3-5 substantial write-ups
├── /demos                         The interactive demos, now on their own page
│   ├── /demos/checkout            (was the headline demo, now standalone)
│   ├── /demos/idempotency
│   ├── /demos/circuit-breaker
│   ├── /demos/vault-rotation
│   └── /demos/event-flow
├── /deep-dives                    Existing structure, retain
│   └── /deep-dives/[slug]
├── /about                         Bio + experience + clients (lightly editorial)
└── /contact                       Form OR mailto + LinkedIn + GitHub
```

**Net effect:** the homepage is no longer overloaded. The cluster demo / chaos engine / live receipts move to `/demos` where the technical reviewer can spend time with them deliberately, not get bombarded with them on first scroll.

## 5. The new homepage — proposed structure (NOT 5 dense sections; THREE focused ones)

### Section 1 — Hero (the elevator pitch, 5-second read)
- Name, role, location, availability pill — restrained
- ONE sentence positioning: "Senior .NET engineer. Distributed microservices, sagas, vault-rotation, zero-downtime deploys, all production-grade."
- TWO CTAs: "See the work" → `/demos`, "Contact" → `/contact`
- A subtle live status pill ("cluster: healthy") that's evidence without dominating

### Section 2 — Proof (ONE proof, not a wall)
- A single embedded demo: the checkout saga running with chaos enabled, ~150 lines high. Live, real backend.
- Caption: "This page is talking to a real .NET 9 cluster. Click 'inject fault' to see the saga compensate."
- A small "See all demos →" link to `/demos` for the rest

### Section 3 — Deep-dive entry (the architecture)
- 3 deep-dive cards in a clean grid: "Saga vs 2PC", "Transactional Outbox", "Vault Rotation"
- Each with a one-line description + reading time
- Link to full archive

### Footer
- Quiet build receipt (git sha, built-at, link to source) — current footer is good, keep
- Contact links
- That's it

## 6. Quality bar — what the site must meet (no relaxations)

| Metric | Target | Failure policy |
|---|---|---|
| Lighthouse performance | ≥ 0.95 mobile, ≥ 0.98 desktop | Revert the PR. Don't lower threshold. |
| Lighthouse a11y | 1.0 (full) | Same |
| Lighthouse SEO | ≥ 0.95 | Same |
| Lighthouse best-practices | ≥ 0.95 | Same |
| JS bundle per route | ≤ 100KB gzipped | Same |
| LCP | ≤ 1.2s | Same |
| CLS | ≤ 0.05 | Same |
| INP | ≤ 100ms | Same |
| Page weight (total) | ≤ 500KB on first load | Same |
| Visual regression tests | per-route Playwright snapshots; deltas surface in PR | Reviewer decides intentional vs regression |

## 7. The test pyramid — what gets tested

| Layer | Tool | Owns |
|---|---|---|
| **Unit** | Vitest | hooks (`useClusterState`, `useLatestTraceId`), utility libs (`lib/api`, `lib/trace-store`, `lib/build-info`), pure component logic |
| **Component** | Vitest + React Testing Library, or Storybook play tests | interactive components (Hero pill states, demo card states, command palette keyboard nav). Each component has ≥ 1 happy-path + 1 failure-path test |
| **Visual regression** | Playwright snapshots | one screenshot per route × `{mobile,tablet,desktop}`. PR review compares deltas |
| **E2E** | Playwright | each demo page has a smoke that hits the live backend (or a recorded fixture if offline) |
| **a11y** | `@axe-core/playwright` in the E2E suite | full WCAG 2.1 AA scan on every route, fails on any violation |
| **Lighthouse CI** | `lhci` in `.github/workflows/site-quality.yml` | runs on every PR; thresholds per § 6 |

Target coverage: **70% line coverage on units, 100% component-test coverage on interactive components, 100% route coverage on a11y + visual regression.**

## 8. Design-system extraction — the 1182 className problem

Today ~1182 `className=` uses across 108 source files, almost certainly with significant Tailwind duplication. The fix is to extract recurring patterns into a small variant catalog using `cva` (class-variance-authority) or a similar primitive.

**Components to extract (target: 12-15 max):**
- `<Button variant="primary|secondary|ghost" size="sm|md|lg">`
- `<Card variant="default|panel|panel-dark" />` (already have surface tokens; codify the wrappers)
- `<Section id="..." padding="dense|relaxed|spacious">`
- `<Heading level={1|2|3} variant="display|hero|section|caption">`
- `<Prose>` for body copy with proper rhythm
- `<Code inline language="..." />` and `<CodeBlock>` (with copy-to-clipboard)
- `<Pill variant="status|tag|caption">`
- `<Link variant="default|cta|subtle" external?>`
- `<Container size="prose|wide|full">`
- `<Stack direction="row|column" gap={N} align align>` (replaces ad-hoc flex)
- `<Glass>` for the translucent panels currently using `bg-white/70 backdrop-blur`
- `<Reveal>` for the `data-reveal` animation wrappers

Adoption metric: `grep -c 'className=' src` should drop by **at least 40%** when the design system is in. The architecture-check workflow (see § 10) ratchets this.

## 9. Content + copywriting pass

The most-undervalued frontend work. Current copy is technically accurate but probably not **compelling**. The pass:

1. **Hero**: re-write as a value-proposition sentence, not a CV opener. Test 3 variants with whoever is brutal about copy.
2. **Section headings**: each should be a *claim*, not a label. "A live .NET 9 cluster, in your browser" is good (it's a claim). "Demos" is bad (it's a label).
3. **CTA labels**: "See the work" beats "View demos". "Hire me" beats "Contact". Action-oriented.
4. **Deep-dive titles + descriptions**: re-read each, ask "would I click this if I had 30 seconds?"
5. **About**: lose the "available for hire" pill if it's not also in the hero. Don't say things twice.
6. **Microcopy on demo controls**: "Inject fault" beats "Kill the service". "Show me a refund failing" beats "Trigger error scenario".

## 10. Architecture check — the front-end ratchet

`scripts/check-frontend-architecture.sh` runs on every PR (mirror `scripts/check-architecture.sh` for the backend):

| Rule | Why | Severity |
|---|---|---|
| No cross-feature imports (e.g., `components/demo/checkout/` → `components/demo/vault/` is forbidden) | Feature isolation; matches backend's no-cross-service rule | Hard fail |
| No `any` in committed TypeScript | Type safety | Hard fail |
| No `console.log` / `console.debug` / `debugger` | Production hygiene | Hard fail |
| No top-level await in `client:load` components | Performance | Hard fail |
| ≤ 100 `className=` uses per file | Forces design-system adoption | Soft warn (until 11.4 lands; then hard fail) |
| All images use `<Image>` from astro | LCP / performance | Hard fail |
| All routes have a `getStaticPaths` or are explicitly SSR | Predictable build | Hard fail |
| All interactive React components have `data-testid` | Testability | Soft warn |

This is the front-end mirror of the backend's coupling discipline. Quality flows from CI, not vigilance.

## 11. Parallel decomposition — wave-runnable

A single wave delivers this rethink. Modify-mode. Tracks ordered by dependency.

### Phase A — Foundation (must land first; sequential)

| Track | Owns | Hours |
|---|---|---|
| **T1** Design system extraction | `src/components/ui/**` (new) — Button, Card, Section, Heading, Prose, Code, Pill, Link, Container, Stack, Glass, Reveal. Plus their variant catalog (`cva` schemas). | 6 |

### Phase B — In parallel after T1 lands (7 tracks)

| Track | Owns | Hours |
|---|---|---|
| **T2** New homepage (3 sections, restrained) | `src/pages/index.astro` (rewrite), `src/components/hero/HeroLite.tsx` (revise to use ui/), `src/components/home/**` (new components for the 3 sections) | 4 |
| **T3** `/demos` page + per-demo routes | `src/pages/demos/index.astro` (new), `src/pages/demos/[demo].astro` (new). Each existing demo component moves into its own route. | 4 |
| **T4** Content + copywriting pass | Every `.mdx` in `src/content/deep-dives/`, all section copy in components. Editor pass: claims not labels, value not features. | 4 |
| **T5** Test pyramid scaffold | `vitest.config.ts`, `playwright.config.ts`, `tests/unit/**`, `tests/e2e/**`, `tests/visual/**`. CI workflow `site-quality.yml`. The TESTS get written per-component in T6+T7. | 3 |
| **T6** Component tests (Vitest + RTL) for all interactive components | one `*.test.tsx` next to each interactive component; full coverage of hooks; failure-path tests | 4 |
| **T7** E2E + visual regression + a11y | `tests/e2e/*.spec.ts` per route, Playwright snapshots, `@axe-core/playwright` runs on every page | 4 |
| **T8** Architecture check + quality ratchet | `scripts/check-frontend-architecture.sh`, `.github/workflows/site-quality.yml` (Lighthouse CI + a11y + visual + bundle-size budget) | 3 |

### Phase C — Demo polish (post-merge of A+B; can run later)

| Track | Owns | Hours |
|---|---|---|
| **T9** Per-demo polish using the new design system | each `src/components/demo/<demo>/**` revised for visual consistency, restraint, and the test contract | 6 |
| **T10** Deep-dive content + visual restraint pass | every deep-dive .mdx; visual hierarchy; reading rhythm | 4 |

**Phase A first (T1), then Phase B parallel (T2-T8), then Phase C parallel (T9-T10).**

**Wall-clock with 8 agents in parallel after T1:** ~4 hours. T1 itself is ~6h sequential (foundation everyone else builds on). Total: ~10h wall-clock for the full rethink.

## 12. Token-discipline budget for this wave

This wave has 10 tracks. Per `token-efficient-briefs.md`:

- Inline reference excerpts for every "mirror this Tailwind component" reference. The existing 700-line Hero file shouldn't be a `Reference to mirror: <path>` — paste 30 lines of it.
- Exact component signatures + skeletons for every new file each track owns.
- Done commands verbatim per track: `npm run test -- src/components/ui` etc.
- Universal rules include no-exploration / no-preamble / no-scope-creep.
- Each track ≤ 80 lines of brief content.

Target: ≤ 25k tokens per agent. Run `wave audit-brief portfolio-site-rethink` before launch — expect 0 warnings.

## 13. Wave configuration

```
REPO=/Users/chidionyema/Documents/code/portfolio-site
GH_REPO=chidionyema/portfolio-site
WAVE_MODE=modify
BASE_BRANCH=feat/site-rethink
BRIEF_FILE=docs/agent-briefs/portfolio-site-rethink-spec.md
TRACK_PREFIX=feat/site-
TRACKS=(T1 T2 T3 T4 T5 T6 T7 T8 T9 T10)
WORKTREE_PARENT=/Users/chidionyema/.gemini/tmp/portfolio-site
```

Notes:
- The portfolio-site repo is **separate** from `haworks-platform`. The wave tool needs the brief file inside the portfolio-site repo (or referenced from this one via `REPO=`). Easiest: copy this spec into the portfolio site's `docs/agent-briefs/` when ready to run.
- The architecture-check workflow (T8) is the FE equivalent of the backend's, ratcheted from day 1.

## 14. Definition of done (whole feature)

- [ ] Homepage has 3 sections (Hero, Proof, Deep-dive entry) — not 5
- [ ] `/demos` page exists with all demos on their own routes
- [ ] Design system extracted; `grep -c 'className=' src` is ≥ 40% lower than today
- [ ] Lighthouse: a11y 1.0, perf ≥ 0.95, SEO ≥ 0.95, best-practices ≥ 0.95
- [ ] Test pyramid: 70% line coverage on units; 100% interactive components have tests; every route has E2E + visual + a11y
- [ ] `scripts/check-frontend-architecture.sh` runs in CI with 0 hard violations
- [ ] All 9 existing parallel worktrees consolidated or pruned
- [ ] `git log` shows zero "relax threshold" commits going forward — only bug fixes

## 15. What this fixes about the site that the user said "looks like a drunk schoolboy"

| User's pain | Fix |
|---|---|
| Cognitive overload on one page | Homepage 3 sections + `/demos` for everything else |
| Inconsistent visual polish | Design system extraction (12-15 ui/ components) |
| Lighthouse degradation | Hard quality bar in CI; reverts replace relaxations |
| Zero tests | Test pyramid (T5-T7) covering unit + component + visual + a11y + E2E |
| Information density past polish | The whole "less is more" principle (§ 3 rule 1) |
| Backend rigour not reflected | The site itself passes the same kind of quality check the backend does |
| Fragmented 9 worktrees | Single feat/site-rethink wave consolidates and prunes |
| Lacks compelling copy | T4 content pass: claims not labels, value not features |

## 16. Reference files (inline excerpts to follow when this becomes a real wave brief)

When the brief is finalised, paste inline excerpts from:

- **Hero shape** to mirror (refined): `portfolio-site/src/components/hero/HeroLite.tsx` lines 22-50
- **Astro page shape**: `portfolio-site/src/pages/deep-dives/[slug].astro` (the existing slug route is clean — mirror its head/layout setup)
- **Tailwind config tokens**: `portfolio-site/tailwind.config.mjs` lines 1-50 (the token system stays)
- **Existing demo component** to mirror for the new ui/ pattern: pick the smallest demo (`IdempotencyDemo.tsx`) as the canonical example

The wave's design pass should run with `wave audit-brief` and reach 0 warnings before launch.
