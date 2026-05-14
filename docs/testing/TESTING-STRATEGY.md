# Testing Strategy

## Test pyramid

```
         [E2E]            Playwright + Aspire AppHost (manual CI, local dev)
        [Smoke]           HttpClient against Aspire AppHost (manual CI, local dev)
      [Contract]          Pact consumer/provider (fast path, no Docker)
    [Architecture]        NetArchTest + check-architecture.sh (fast path, no Docker)
  [Integration]           Testcontainers, one suite per service (parallel CI matrix)
[Unit]                    Pure in-process, no I/O (fast path, no Docker)
```

The fast path (unit + architecture + contract tests) runs in a single CI job with a 15-minute timeout. Integration tests run in parallel per-service after the fast path succeeds. E2E and Smoke tests run only on manual `workflow_dispatch`.

---

## Shared Testcontainers pattern

### Rule

Integration test projects must never instantiate raw Testcontainers builders (`PostgreSqlBuilder`, `ContainerBuilder`, `RabbitMqBuilder`, `ElasticsearchBuilder`, etc.) directly. This is enforced by `scripts/check-architecture.sh` Rule 3 and will fail CI.

All container access goes through the shared singleton classes in `src/BuildingBlocks.Testing/Containers/`:

| Class | Container | Usage |
|---|---|---|
| `SharedTestPostgres` | `postgres:16-alpine` | Standard relational databases |
| `SharedTestPostGIS` | PostGIS-enabled Postgres | Geospatial databases (location-svc) |
| `SharedTestElasticsearch` | `elasticsearch:8.17.0` | Search index tests |
| `SharedTestKafka` | `confluentinc/cp-kafka:7.6.1` | Kafka consumer/producer tests |
| `SharedTestRabbitMq` | `rabbitmq:3-management` | MassTransit integration tests |
| `SharedTestS3` | `localstack/localstack:3` | S3/content storage tests |

### How it works

Each class is a static singleton with a `SemaphoreSlim` gate to prevent concurrent startup races. All containers are built with `.WithReuse(true)`, which tells Testcontainers to hash the builder configuration and reuse the container from a previous run if one with the same hash is already running. This means:

- First `dotnet test` run starts the containers (slow).
- Subsequent runs on the same machine reattach to the running containers (fast).
- CI gets fresh containers per runner because Docker state is not preserved between runs, but the images are cached in `actions/cache`.

### Postgres: per-test database isolation

`SharedTestPostgres.CreateDatabaseAsync("catalog")` creates a new database named `catalog_<guid>` on the shared container and returns its connection string. This gives each test fixture complete isolation at the database level without starting a new container:

```csharp
// In WebApplicationFactory.InitializeAsync():
ConnectionString = await SharedTestPostgres.CreateDatabaseAsync("catalog");
```

The GUID suffix ensures no collision between parallel test runs or between different fixtures in the same run.

### Elasticsearch: per-test index isolation

`SharedTestElasticsearch.GetConnectionAsync("search")` returns the shared container URL plus a unique index name `search_<guid>`:

```csharp
var (url, indexName) = await SharedTestElasticsearch.GetConnectionAsync("search");
```

### Kafka: shared bootstrap address

`SharedTestKafka.GetBootstrapAddressAsync()` returns the broker address of the shared Kafka container. All tests that need Kafka use the same container and create topics with unique names to avoid collision.

---

## Integration test conventions

### WebApplicationFactory

Each service's integration test project defines a `<Svc>WebAppFactory : WebApplicationFactory<Program>` that:

1. Calls `SharedTestPostgres.CreateDatabaseAsync("<svc>")` in `InitializeAsync()` to get an isolated database.
2. Sets `ASPNETCORE_ENVIRONMENT=Test` before the host builds. The `Test` environment is used as a guard in `DependencyInjection.cs` to skip production-only registrations (e.g., MassTransit's RabbitMQ transport, Kafka consumer registration).
3. Overrides configuration via `ConfigureAppConfiguration` with the test connection string and `Vault__Enabled=false`.
4. Replaces the production MassTransit transport with `AddMassTransitTestHarness()` in `ConfigureServices`.
5. Registers `AddTestAuth()` to satisfy `[Authorize]`-decorated endpoints.

Example pattern from `CatalogWebAppFactory`:

```csharp
public sealed class CatalogWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        ConnectionString = await SharedTestPostgres.CreateDatabaseAsync("catalog");
        JwtTestDefaults.SetTestEnvironmentVariables();
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__catalog", ConnectionString);
        Environment.SetEnvironmentVariable("Vault__Enabled", "false");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.ConfigureServices(services =>
        {
            services.AddMassTransitTestHarness();
            services.AddDomainEventPublisher();
            services.AddAuthentication(TestAuthenticationHandler.SchemeName).AddTestAuth();
        });
    }

    public async Task EnsureSchemaAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.MigrateAsync();
    }
}
```

### MassTransit test harness

Integration tests use `ITestHarness` to assert message publishing and consumption:

```csharp
var harness = app.Services.GetRequiredService<ITestHarness>();
await harness.Start();

// ... perform HTTP call that should trigger a publish ...

var published = await harness.Published.SelectAsync<StockReservedEvent>().FirstOrDefault();
published.Should().NotBeNull();
```

The in-memory transport eliminates the need for a RabbitMQ container in most integration test scenarios. Tests that specifically exercise consumer behavior can register consumers on the test harness.

### xUnit collection fixtures

To share a single `WebApplicationFactory` instance across all tests in a project, use xUnit's `[Collection]` attribute:

```csharp
[CollectionDefinition(nameof(CatalogIntegrationCollection))]
public sealed class CatalogIntegrationCollection : ICollectionFixture<CatalogWebAppFactory> { }

[Collection(nameof(CatalogIntegrationCollection))]
public class CatalogFlowsTests(CatalogWebAppFactory app) { ... }
```

This ensures the factory is created once, `EnsureSchemaAsync()` runs once, and the container is not torn down between test classes.

---

## Contract tests (Pact)

Contract tests verify the schema of cross-service event messages. They run without any infrastructure (no Docker, no HTTP servers).

### Consumer-side tests

A consumer-side Pact test defines what a consumer expects from a message. The test writes a pact file to `tests/pacts/`. For example, `tests/Catalog/Catalog.Contract/StockReservedConsumerTests.cs` defines the expected shape of `StockReservedEvent` from the perspective of any service that consumes it.

```csharp
_messagePact = Pact.V4("ConsumerOfCatalog", "catalog-svc", config).WithMessageInteractions();
```

The pact config points `PactDir` at `tests/pacts/` (relative to the test binary). The generated pact files in that directory are committed or published to the Pact Broker.

### Provider-side verification

Provider-side tests verify that the actual producer matches the pact file. These are located in the provider service's own test project and run against the real event types.

### Pact Broker

The Pact Broker runs locally at `http://localhost:9292` (docker-compose) or via the Aspire `pact-broker` container. In CI, pact files are compared against the broker with `pact-broker can-i-deploy` as part of the release gate.

### What Pact tests cover

Current contract test coverage:

| Consumer name | Pact file | Event pinned |
|---|---|---|
| `ConsumerOfCatalog` | `pacts/ConsumerOfCatalog-catalog-svc.json` | `StockReservedEvent` |
| `ConsumerOfIdentity` | `pacts/ConsumerOfIdentity-identity-svc.json` | (see Identity.Contract) |
| `ConsumerOfOrders` | `pacts/ConsumerOfOrders-orders-svc.json` | (see Orders.Contract) |
| `ConsumerOfPayments` | `pacts/ConsumerOfPayments-payments-svc.json` | (see Payments.Contract) |

---

## Architecture tests

Architecture tests enforce the service boundary rules from ADR-0001. They run in-process using NetArchTest.Rules and do not require any infrastructure.

### Per-service boundary tests

Each service with an `*.Architecture` test project defines a `BoundaryTests` class that asserts no type in the service's assemblies references a type from a sibling service namespace.

For example, `tests/Catalog/Catalog.Architecture/BoundaryTests.cs` asserts that `Haworks.Catalog.*` types do not depend on `Haworks.Identity`, `Haworks.Orders`, `Haworks.Payments`, `Haworks.Content`, `Haworks.CheckoutOrchestrator`, or `Haworks.BffWeb`.

### CI architecture check script

`scripts/check-architecture.sh` runs three rules against the source tree:

**Rule 1 (hard fail): No cross-service project references**

Scans all `*.csproj` files under `src/<Svc>/` and fails if any `<ProjectReference>` points to a path that is not:
- `src/BuildingBlocks/Haworks.BuildingBlocks.csproj`
- `src/BuildingBlocks.Testing/Haworks.BuildingBlocks.Testing.csproj`
- `src/Contracts/Haworks.Contracts.csproj`
- An internal sub-project of the same service (e.g., `src/Catalog/Catalog.Application/`)

**Rule 2 (soft warning): Cross-cutting service decoupling**

For the cross-cutting services (Audit, Notifications, Payments, Search, Content, Identity), warns if any `.cs` file contains a `using Haworks.Contracts.<Other>` import for a different service's namespace. This is a warning today (cross-domain event consumption is legitimate); it will become a hard fail as the platform decouples further.

**Rule 3 (hard fail): No raw Testcontainers in integration tests**

Scans `tests/` for direct instantiation of `PostgreSqlBuilder`, `ContainerBuilder`, `RabbitMqBuilder`, or `ElasticsearchBuilder`. Fails if any match is found outside of `BuildingBlocks.Testing` itself.

The script exits with code 1 on any hard violation and code 0 (or 2 for soft-only warnings) otherwise.

---

## E2E tests

### Structure

E2E tests live in `tests/E2E/` and use Playwright for browser automation. They require the full Aspire AppHost to be running and Playwright browsers to be installed.

### Skip pattern

E2E tests skip by default when `E2E_ENABLED` is not set. This prevents them from running during `dotnet test` without the full stack:

```csharp
Skip.IfNot(Environment.GetEnvironmentVariable("E2E_ENABLED") == "1",
    "Set E2E_ENABLED=1 to run E2E tests");
```

### Running locally

```bash
# Install Playwright browsers (once)
dotnet build tests/E2E/E2E.csproj
pwsh tests/E2E/bin/Debug/net9.0/playwright.ps1 install --with-deps

# Start the Aspire AppHost (in a separate terminal)
cd deploy/aspire && dotnet run

# Run E2E tests
E2E_ENABLED=1 dotnet test tests/E2E/E2E.csproj
```

### Smoke tests

Smoke tests in `tests/Smoke/` use `HttpClient` rather than Playwright and exercise the BFF's public endpoints. They also require the Aspire stack but are faster than full Playwright tests.

### CI behavior

Both Smoke and E2E suites are excluded from automatic push/PR CI runs. The `smoke-and-e2e` CI job has `if: github.event_name == 'workflow_dispatch'`. The intent is to add a deployment-triggered run against `SMOKE_TARGET_URL` (a live Fly.io URL) once the deployment pipeline is stable.

---

## CI matrix

The integration test matrix in `.github/workflows/ci.yml`:

```yaml
matrix:
  include:
    - suite: Catalog        path: tests/Catalog/Catalog.Integration
    - suite: Orders         path: tests/Orders/Orders.Integration
    - suite: Payments       path: tests/Payments/Payments.Integration
    - suite: Identity       path: tests/Identity/Identity.Integration
    - suite: BffWeb         path: tests/BffWeb/BffWeb.Integration
    - suite: CheckoutOrchestrator  path: tests/CheckoutOrchestrator/CheckoutOrchestrator.Integration
    - suite: Content        path: tests/Content/Content.Integration
    - suite: Search         path: tests/Search/Search.Integration
    - suite: Audit          path: tests/Audit/Audit.Integration
    - suite: Notifications  path: tests/Notifications/Notifications.Integration
    - suite: Webhooks       path: tests/Webhooks/Webhooks.Integration
    - suite: Payouts        path: tests/Payouts.Integration
    - suite: Scheduler      path: tests/Scheduler.Integration
```

`fail-fast: false` ensures a failure in one suite does not cancel the others.

Docker images are cached per runner using `actions/cache` keyed on exact image tags. The cache contains `.tar` archives of `postgres:16-alpine`, `elasticsearch:8.17.0`, `cp-kafka:7.6.1`, and `rabbitmq:3-management`. On a cache hit, images are loaded with `docker load` before tests run.

---

## How to add tests for a new service

### 1. Create the test project structure

```
tests/<Svc>/
  <Svc>.Unit/
    <Svc>.Unit.csproj
  <Svc>.Integration/
    <Svc>WebAppFactory.cs
    <Svc>IntegrationCollection.cs
    <Svc>.Integration.csproj
  <Svc>.Architecture/
    BoundaryTests.cs
    <Svc>.Architecture.csproj
```

Contract tests are optional and follow the pattern in `tests/Catalog/Catalog.Contract/`.

### 2. Implement WebApplicationFactory

Follow the `CatalogWebAppFactory` pattern:

- Call `SharedTestPostgres.CreateDatabaseAsync("<svc>")` (or `SharedTestPostGIS` for geospatial services).
- Set `ASPNETCORE_ENVIRONMENT=Test` before the host builds.
- Set `Vault__Enabled=false`.
- Replace MassTransit with `AddMassTransitTestHarness()`.
- Register `AddTestAuth()`.
- Add a `EnsureSchemaAsync()` method that calls `db.Database.MigrateAsync()`.

If the service depends on Kafka, call `SharedTestKafka.GetBootstrapAddressAsync()` and inject the address.

If the service depends on Elasticsearch, call `SharedTestElasticsearch.GetConnectionAsync("<svc>")` and inject the URL and index name.

If the service depends on S3, call `SharedTestS3.GetEndpointAsync()`.

### 3. Implement boundary tests

Copy `tests/Catalog/Catalog.Architecture/BoundaryTests.cs` and update:

- `ForbiddenNamespacePrefixes`: list all sibling service namespaces that must not be referenced.
- `CatalogAssemblies`: list the Domain, Application, and Infrastructure assembly types for the new service.

### 4. Add to CI matrix

Add a `suite` / `path` entry to the `integration-tests` matrix in `.github/workflows/ci.yml`.

### 5. Add to deploy workflow

Add path filters for the new service in `.github/workflows/deploy.yml` under `changes.steps.filter.filters`. Add the service to the `plan` step's `add_if` list. Create a `fly.<svc>.toml` following the pattern of existing service toml files.

### 6. Verify architecture rules pass

```bash
bash scripts/check-architecture.sh
```

The script must exit with code 0 before the new service is merged.
