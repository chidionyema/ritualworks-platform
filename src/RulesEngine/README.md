# RulesEngine Service

Dynamic business rule evaluation engine. Stores rules as LINQ Dynamic expressions in Postgres and evaluates them at runtime against arbitrary input payloads.

## Responsibilities
- CRUD management of named rules with dynamic expressions
- Evaluate rules against input payloads at runtime
- Return evaluation outcome with expression trace for debugging

## API Endpoints
| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| POST | `/api/rules/evaluate` | User | Evaluate a rule by ID against inputs |
| GET | `/api/rules` | User | List rules (optional `?activeOnly=true`) |
| GET | `/api/rules/{id}` | User | Get a single rule |
| POST | `/api/rules` | Admin | Create a rule |
| PUT | `/api/rules/{id}` | Admin | Update a rule |
| DELETE | `/api/rules/{id}` | Admin | Delete a rule |

## Domain Entities
- **Rule** — Id, Name (unique), Expression (LINQ Dynamic string), IsActive, CreatedAt, UpdatedAt
- **RuleEvaluationResult** — Outcome (bool), Expression, Trace

## Events Published
None.

## Events Consumed
None.

## Infrastructure Dependencies
- PostgreSQL (`RulesDbContext`, schema: `rules`)
- System.Linq.Dynamic.Core — expression evaluation

## Configuration
```
ConnectionStrings:rules    Postgres connection string
```

## Health Checks
- DB: `AddDbHealthCheck<RulesDbContext>()`
