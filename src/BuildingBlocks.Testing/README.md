# BuildingBlocks.Testing

Shared test infrastructure library. Provides reusable container singletons, authentication fakes, and test utilities for all integration test suites.

## Shared Container Singletons

All integration tests **must** use these singletons. Raw `PostgreSqlBuilder` / `ContainerBuilder` usage is banned and enforced by CI architecture guards.

| Singleton | Container | Usage |
|-----------|-----------|-------|
| `SharedTestPostgres` | PostgreSQL 16 | `await SharedTestPostgres.CreateDatabaseAsync("catalog")` |
| `SharedTestPostGIS` | PostgreSQL + PostGIS | `await SharedTestPostGIS.CreateDatabaseAsync("location")` |
| `SharedTestElasticsearch` | Elasticsearch 8 | `await SharedTestElasticsearch.GetConnectionAsync("search")` |
| `SharedTestRabbitMq` | RabbitMQ 3 | `await SharedTestRabbitMq.GetConnectionAsync()` |
| `SharedTestKafka` | Confluent Kafka | `await SharedTestKafka.GetConnectionAsync()` |
| `SharedTestLocalStack` | LocalStack (S3) | `await SharedTestLocalStack.GetConnectionAsync()` |

## Design Principles
- **`WithReuse(true)`** — one Docker container per type shared across all `dotnet test` runs on a machine
- **Fresh DB per test** — `CreateDatabaseAsync()` creates an isolated database; `DropDatabaseAsync()` cleans up
- **Orphan cleanup** — automatic deletion of stale test databases (keeps last 3 per service prefix)
- **No raw containers** — CI `scripts/check-architecture.sh` fails on direct Testcontainer instantiation

## Authentication Utilities
| Class | Purpose |
|-------|---------|
| `TestAuthenticationHandler` | In-process auth handler for `WebApplicationFactory` |
| `JwtTestDefaults` | Standard test JWT claims (userId, roles) |
| `TestAuthMiddleware` | Request-scoped principal injection |

## Test Utilities
| Class | Purpose |
|-------|---------|
| `TestBase` | Abstract base with `MockRepository`, `TestConfig`, `LoggerFactory` |
| `TestWait` | Polling/retry helpers for eventual consistency assertions |
| `TestModuleInitializer` | One-time setup per test assembly |
