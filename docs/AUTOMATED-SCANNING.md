# Automated Security, Quality & Exploratory Scanning

This platform runs **6 automated workflows** that continuously scan for security flaws, bugs, edge cases, and regressions — from code to deployed site.

---

## Overview

| Workflow | Trigger | What it does |
|----------|---------|-------------|
| [CodeQL](#1-codeql) | Every PR, push to main, nightly | Static analysis for security vulnerabilities |
| [Dependabot](#2-dependabot) | Weekly (Monday) | Dependency vulnerability + update PRs |
| [Nightly Scan](#3-nightly-scan) | 2 AM UTC daily, manual | Full audit: deps, architecture, tests, secrets, Claude deep review |
| [Claude PR Review](#4-claude-pr-review) | Every PR open/update | AI code review on changed files |
| [Post-Deploy Scan](#5-post-deploy-exploratory-scan) | After every deploy, manual | Live-site pentesting: ZAP, Nuclei, Claude attack scenarios |
| [CI](#6-ci) | Every PR, push to main | Build, unit, architecture, contract, integration tests |

---

## 1. CodeQL

**File:** `.github/workflows/codeql.yml`
**Trigger:** PR, push to main, nightly (3 AM UTC)

GitHub's static analysis engine for C#. Finds:
- SQL injection
- XSS (cross-site scripting)
- SSRF (server-side request forgery)
- Path traversal
- Insecure deserialization
- Code quality issues

Results appear in the **Security → Code scanning alerts** tab on GitHub.

No configuration needed — runs automatically.

---

## 2. Dependabot

**File:** `.github/dependabot.yml`

Automatically opens PRs when:
- A NuGet package has a **known vulnerability** (CVE)
- A NuGet package has a **newer version** available
- A GitHub Actions action has an update

Updates are grouped to reduce PR noise:

| Group | Packages |
|-------|----------|
| `aspire` | All `Aspire.*` packages |
| `testing` | xunit, FluentAssertions, Moq, Testcontainers, Test SDK |
| `ef-core` | All EF Core + Npgsql EF packages |

Schedule: **weekly on Monday**, max 10 open PRs.

---

## 3. Nightly Scan

**File:** `.github/workflows/nightly.yml`
**Trigger:** 2 AM UTC daily + manual dispatch from Actions tab

### Jobs

#### 3.1 Dependency Audit
Runs `dotnet list package` with `--vulnerable`, `--deprecated`, and `--outdated` flags. Results appear in the **job summary**.

#### 3.2 Architecture & Analyzers
- Full Release build with `TreatWarningsAsErrors=true`
- `scripts/check-architecture.sh` — cross-service coupling, raw Testcontainers violations
- All `*.Architecture` test projects

#### 3.3 Full Integration Test Suite
Runs **every** integration test project in the repo. Catches flaky tests and regressions that per-service CI might miss.

#### 3.4 Gitleaks Secret Scan
Scans the full git history for leaked credentials, API keys, tokens, and passwords.

#### 3.5 Claude Deep Code Review
**The AI audit.** Claude reads the entire `src/` directory and performs two phases:

**Phase 1 — Rules-based scan** (checks CLAUDE.md rules):
- Missing `[Authorize]` on controllers
- IDOR: userId taken from request body instead of JWT
- Wrong auth middleware order
- Invalid state transitions, missing idempotency
- Financial bugs ($0 amounts, refund > payment)
- Validator anti-patterns

**Phase 2 — Exploratory analysis** (thinks beyond the rules):
- **Data flow tracing**: input → controller → service → DB, finding validation gaps
- **Failure mode analysis**: what happens when Vault/Kafka/Elasticsearch/Redis is down?
- **Concurrency**: double purchase, double refund, saga timeout races
- **Business logic gaps**: empty cart checkout, sub-penny prices, cross-user access
- **Missing test coverage**: lists specific test cases that should exist but don't

Findings are posted as a **GitHub Issue** (label: `nightly-audit`). Previous night's issue is auto-closed.

#### 3.6 Summary
Dashboard table in the Actions summary tab showing pass/fail for all jobs.

---

## 4. Claude PR Review

**File:** `.github/workflows/claude-pr-review.yml`
**Trigger:** Every PR opened, updated, or reopened (skips Dependabot + drafts)

Claude reviews the PR diff with two lenses:

| Lens | What it checks |
|------|---------------|
| Rules | Auth, IDOR, injection, state transitions, financial math, validators |
| Exploratory | Data flow gaps, failure modes, race conditions, business logic abuse, missing tests |

Posts **inline review comments** directly on the PR. Can also be triggered manually with a `/review` comment.

---

## 5. Post-Deploy Exploratory Scan

**File:** `.github/workflows/post-deploy-scan.yml`
**Trigger:** After every deploy to Fly + manual dispatch (enter any URL)

Runs **5 parallel jobs** against the live deployed site:

#### 5.1 Smoke Check
Health endpoint with 10 retries. All other jobs skip if the site is down.

#### 5.2 OWASP ZAP
Automated penetration test:
- **API scan**: tests API endpoints for injection, auth bypass, CORS issues
- **Baseline scan**: checks headers, cookies, transport security

HTML report uploaded as artifact. Findings can auto-create GitHub Issues.

#### 5.3 Nuclei
Scans for:
- Known CVEs matching the tech stack
- Misconfigurations (exposed debug endpoints, default settings)
- Exposed admin panels (`/swagger`, `/hangfire`, `/elmah`)
- Default credentials

Fails the job on critical/high findings.

#### 5.4 Auth & IDOR Probe
Runs the E2E test suite's security-tagged tests against the live site using `E2E_TARGET_URL`.

#### 5.5 Claude Exploratory Testing
**The live attack simulation.** Claude generates and executes test scenarios using `curl` against the deployed URL:

| Category | Example scenarios |
|----------|-----------------|
| Auth bypass | No token, expired token, wrong role on every endpoint |
| IDOR | User A creates resource, User B tries to access/modify/delete it |
| Input validation | SQL injection, XSS, path traversal payloads in every field |
| Business logic | Empty cart checkout, $0.001 prices, double refund, price manipulation |
| Concurrency | Simultaneous last-stock purchase, parallel refund requests |
| State machine | Ship unpaid order, refund pending payment, re-open closed request |
| Infrastructure | Security headers, CORS origin reflection, exposed debug endpoints |

Each test reports: what was done, expected result, actual result, PASS/FAIL/WARNING, and evidence (response code + body). Posted as a GitHub Issue (label: `exploratory-scan`).

---

## Setup

### Required secret
```bash
gh secret set ANTHROPIC_API_KEY
# Paste your Anthropic API key when prompted
```

This enables all Claude-powered workflows (nightly review, PR review, post-deploy exploratory).

### Manual triggers
All scanning workflows can be triggered manually from the **Actions tab**:
- **Nightly Scan**: Actions → Nightly Scan → Run workflow
- **Post-Deploy Scan**: Actions → Post-Deploy Exploratory Scan → Run workflow (enter target URL)
- **CodeQL**: Actions → CodeQL → Run workflow

### Viewing results
| Result type | Where to find it |
|------------|-----------------|
| CodeQL alerts | GitHub → Security → Code scanning alerts |
| Dependabot PRs | GitHub → Pull requests (author: dependabot) |
| Nightly findings | GitHub → Issues (label: `nightly-audit`) |
| Post-deploy findings | GitHub → Issues (label: `exploratory-scan`) |
| ZAP/Nuclei reports | GitHub → Actions → run → Artifacts |
| Job summaries | GitHub → Actions → run → Summary tab |

### ZAP rule tuning
Edit `.github/zap-rules.tsv` to suppress false positives. Format:
```
<rule-id>	IGNORE|WARN|FAIL	(description)
```

---

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    CODE CHANGES                          │
│                                                         │
│  PR opened ──► Claude PR Review (rules + exploratory)   │
│             ──► CodeQL (static analysis)                 │
│             ──► CI (build + unit + integration tests)    │
│                                                         │
├─────────────────────────────────────────────────────────┤
│                    MERGED TO MAIN                        │
│                                                         │
│  Deploy ──► Post-Deploy Scan                            │
│              ├── OWASP ZAP (automated pentest)          │
│              ├── Nuclei (CVE + misconfig scan)          │
│              ├── Auth & IDOR probes (Playwright)        │
│              └── Claude exploratory attack scenarios     │
│                                                         │
├─────────────────────────────────────────────────────────┤
│                    NIGHTLY (2 AM UTC)                    │
│                                                         │
│  ├── Dependency audit (vulnerable/deprecated/outdated)  │
│  ├── Architecture enforcement (analyzers + tests)       │
│  ├── Full integration test suite (all services)         │
│  ├── Gitleaks (secret scan)                             │
│  └── Claude deep review (rules + exploratory + tests)   │
│                                                         │
├─────────────────────────────────────────────────────────┤
│                    WEEKLY (Monday)                       │
│                                                         │
│  Dependabot ──► NuGet vulnerability + update PRs        │
│             ──► GitHub Actions update PRs                │
└─────────────────────────────────────────────────────────┘
```
