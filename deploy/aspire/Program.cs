var builder = DistributedApplication.CreateBuilder(args);

// =============================================================================
// PER-SERVICE VAULT CREDENTIAL PATHS
// =============================================================================
// vault-init.sh writes per-service AppRole creds under vault-creds/<svc>/.
// Each service binds to its own subdirectory; no service can read another's.
// =============================================================================
var credsHostDir = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "vault-creds"));
Directory.CreateDirectory(credsHostDir);

string RoleIdPath(string svc)   => Path.Combine(credsHostDir, svc, "role_id");
string SecretIdPath(string svc) => Path.Combine(credsHostDir, svc, "secret_id");

// Shared Vault config manifests (per-service policy template + AppRole template,
// KV layout, dynamic DB roles). Same files prod IaC consumes.
var vaultManifestsHostDir = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "infra", "vault"));

// =============================================================================
// INFRASTRUCTURE RESOURCES
// =============================================================================

// Postgres + per-service databases. init-postgres.sql also creates per-DB
// "_owner" group roles that Vault dynamic credentials join.
//
// Volume name is namespaced (`ritualworks-platform-*`) so the ritualworks
// platform stack can co-exist with the legacy modular-monolith stack on the
// same Docker daemon without colliding on Aspire-generated postgres
// passwords baked into pg_hba on first init.
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("ritualworks-platform-postgres-data")
    .WithBindMount("./init-postgres.sql", "/docker-entrypoint-initdb.d/init.sql")
    .WithPgAdmin();

var identityDb = postgres.AddDatabase("identity");
var catalogDb  = postgres.AddDatabase("catalog");
var ordersDb   = postgres.AddDatabase("orders");
var paymentsDb = postgres.AddDatabase("payments");
var contentDb  = postgres.AddDatabase("content");
var checkoutDb = postgres.AddDatabase("checkout");

var redis = builder.AddRedis("redis")
    .WithDataVolume("ritualworks-platform-redis-data")
    .WithRedisCommander();

// Pinned host port — Aspire DCP otherwise drifts the rabbit Service port,
// breaking ConnectionStrings:rabbitmq for clients.
// Note: must use the canonical rabbitmq image (not masstransit/rabbitmq) for
// WithManagementPlugin() to recognize the registry/tag.
var rabbitmq = builder.AddRabbitMQ("rabbitmq", port: 5672)
    .WithManagementPlugin();

// Pact broker — self-hosted (ADR-0006). Used by every service's CI for
// publishing pacts and by every PR for can-i-deploy. Has its own Postgres
// sidecar (kept separate from the application Postgres cluster).
var pactDb = builder.AddPostgres("pact-db", port: null, password: null)
    .WithDataVolume("ritualworks-platform-pact-db-data");
var pactBroker = builder.AddContainer("pact-broker", "pactfoundation/pact-broker", "latest")
    .WaitFor(pactDb)
    .WithEnvironment(ctx =>
    {
        var pactDbEndpoint = pactDb.Resource.PrimaryEndpoint;
        ctx.EnvironmentVariables["PACT_BROKER_DATABASE_HOST"]     = pactDbEndpoint.ContainerHost;
        ctx.EnvironmentVariables["PACT_BROKER_DATABASE_PORT"]     = pactDbEndpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ctx.EnvironmentVariables["PACT_BROKER_DATABASE_NAME"]     = "postgres";
        ctx.EnvironmentVariables["PACT_BROKER_DATABASE_USERNAME"] = "postgres";
        // Aspire-generated random password from the pact-db resource:
        ctx.EnvironmentVariables["PACT_BROKER_DATABASE_PASSWORD"] = pactDb.Resource.PasswordParameter.Value;
        ctx.EnvironmentVariables["PACT_BROKER_BASE_URL"]          = "http://localhost:9292";
    })
    .WithHttpEndpoint(targetPort: 9292, name: "ui");

// MinIO — wired up but only content-svc references it (when extracted).
var minio = builder.AddContainer("minio", "minio/minio", "latest")
    .WithVolume("ritualworks-platform-minio-data", "/data")
    .WithEnvironment("MINIO_ROOT_USER", "minioadmin")
    .WithEnvironment("MINIO_ROOT_PASSWORD", "minioadmin")
    .WithArgs("server", "/data", "--console-address", ":9001")
    .WithHttpEndpoint(targetPort: 9000, name: "s3")
    .WithHttpEndpoint(targetPort: 9001, name: "console");

// ClamAV — same: wired up but only content-svc uses it.
var clamav = builder.AddContainer("clamav", "clamav/clamav", "latest")
    .WithEndpoint(port: 3310, targetPort: 3310, name: "clamd");

// =============================================================================
// VAULT
// =============================================================================
// Dev server (auto-unsealed, in-memory). Bootstrap-token only — services
// authenticate via per-service AppRoles set up by vault-init.sh below.

var vaultBootstrapToken = "dev-root-token";
var vault = builder.AddContainer("vault", "hashicorp/vault", "1.15")
    .WithEnvironment("VAULT_DEV_ROOT_TOKEN_ID", vaultBootstrapToken)
    .WithEnvironment("VAULT_DEV_LISTEN_ADDRESS", "0.0.0.0:8200")
    .WithEnvironment("VAULT_LOG_LEVEL", "info")
    .WithHttpEndpoint(targetPort: 8200, name: "http")
    .WithArgs("server", "-dev");

// One-shot init container — for each service in services.json:
//   * writes Vault policy svc-<name>
//   * configures AppRole haworks-<name>
//   * issues role_id + secret_id, writes them to vault-creds/<name>/
// Plus enables KV v2, seeds dev placeholder values, configures the database
// secrets engine + per-context dynamic roles.
//
// NOTE: no .WaitFor(vault) — Aspire 9's WaitFor blocks until the dependency
// reports Healthy, and AddContainer-defined resources have no health check
// by default. The script (`vault-init.sh`) has its own `until vault status`
// retry loop. Same reasoning for postgres — vault-init's bash script blocks
// on Aspire-injected ConnectionStrings__postgres before it does any DB work.
var vaultInit = builder.AddContainer("vault-init", "hashicorp/vault", "1.15")
    .WithReference(postgres)  // injects ConnectionStrings__postgres for the DB engine
    .WithBindMount("./vault-init.sh", "/init.sh")
    .WithBindMount(credsHostDir, "/creds")
    .WithBindMount(vaultManifestsHostDir, "/manifests", isReadOnly: true)
    .WithEnvironment("VAULT_ADDR",  "http://vault:8200")
    .WithEnvironment("VAULT_TOKEN", vaultBootstrapToken)
    // vault-init.sh refuses to run without this guard.
    .WithEnvironment("ALLOW_DEV_SEED", "yes")
    .WithEntrypoint("/bin/sh")
    .WithArgs("/init.sh");

// Second one-shot — currently a no-op safety net (vault-init handles KV
// values too). Kept as a separate resource so future flows can refresh
// just the secret values without re-creating the AppRoles.
var seedScriptHostPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "scripts", "seed-vault-dev.sh"));
var vaultSeed = builder.AddContainer("vault-seed", "hashicorp/vault", "1.15")
    .WaitForCompletion(vaultInit)
    .WithBindMount(seedScriptHostPath, "/seed.sh")
    .WithEnvironment("VAULT_ADDR",  "http://vault:8200")
    .WithEnvironment("VAULT_TOKEN", vaultBootstrapToken)
    .WithEntrypoint("/bin/sh")
    .WithArgs("/seed.sh");

// =============================================================================
// SERVICES
// =============================================================================
// Per docs/microservices-migration/03-build-plan.md.
// Each service is wired with:
//   • WaitForCompletion(vaultSeed) — KV secrets must exist before
//     VaultConfigBootstrap reads them (when wired)
//   • WithReference(<svc>Db)      — Aspire injects ConnectionStrings__<svc>
//   • Vault credential paths for the service's own AppRole

// --- identity-svc -----------------------------------------------------------
// Identity is the only service with Vault__Enabled=true at boot — its
// VaultConfigBootstrap loads JWT secrets via AppRole login during
// Program.cs Main(). That login requires vault-init to have already
// written the role_id + secret_id files under vault-creds/identity/,
// which only happens after vault-seed completes. Other services tolerate
// vault-not-ready (they use lazy access) so they can use the cheaper
// WaitFor(vault) — only identity needs WaitForCompletion(vaultSeed).
var identity = builder.AddProject<Projects.Identity_Api>("identity-svc")
    .WaitForCompletion(vaultSeed)
    .WithReference(identityDb)
    .WithEnvironment("Vault__Enabled",      "true")
    .WithEnvironment("Vault__Address",      vault.GetEndpoint("http"))
    .WithEnvironment("Vault__RoleIdPath",   RoleIdPath("identity"))
    .WithEnvironment("Vault__SecretIdPath", SecretIdPath("identity"))
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development");

// --- catalog-svc -----------------------------------------------------------
// Catalog publishes stock events to RabbitMQ (per-context outbox in
// catalog DB). Has its own per-service Vault AppRole (no Vault secrets
// needed yet — all stock state is in postgres, not Vault).
var catalog = builder.AddProject<Projects.Catalog_Api>("catalog-svc")
    .WaitFor(vault)
    .WithReference(catalogDb)
    .WithReference(rabbitmq)
    .WithEnvironment("Vault__Enabled",      "false")  // no Vault secrets in Phase 2; flip when needed
    .WithEnvironment("Vault__Address",      vault.GetEndpoint("http"))
    .WithEnvironment("Vault__RoleIdPath",   RoleIdPath("catalog"))
    .WithEnvironment("Vault__SecretIdPath", SecretIdPath("catalog"))
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development");

// --- checkout-orchestrator-svc --------------------------------------------
// The saga. Consumes everything (CheckoutInitiated, StockReserved/Failed,
// PaymentSessionCreated/Failed, PaymentCompleted, PaymentAmountMismatch);
// publishes orchestration triggers (StockReservationRequested,
// PaymentSessionRequested, StockReleaseRequested). Per-context outbox in
// checkout DB.
var checkout = builder.AddProject<Projects.CheckoutOrchestrator_Api>("checkout-svc")
    .WaitFor(vault)
    .WithReference(checkoutDb)
    .WithReference(rabbitmq)
    .WithEnvironment("Vault__Enabled",      "false")
    .WithEnvironment("Vault__Address",      vault.GetEndpoint("http"))
    .WithEnvironment("Vault__RoleIdPath",   RoleIdPath("checkout-orchestrator"))
    .WithEnvironment("Vault__SecretIdPath", SecretIdPath("checkout-orchestrator"))
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development");

// --- orders-svc ------------------------------------------------------------
// Orders consumes PaymentCompleted / PaymentSessionFailed / StockReservationFailed
// from RabbitMQ; publishes OrderCreated / OrderCompleted / OrderAbandoned via
// per-context outbox in orders DB. Per-service Vault AppRole; no Vault secrets
// needed yet (no external API calls — pure event-driven state).
var orders = builder.AddProject<Projects.Orders_Api>("orders-svc")
    .WaitFor(vault)
    .WithReference(ordersDb)
    .WithReference(rabbitmq)
    .WithEnvironment("Vault__Enabled",      "false")
    .WithEnvironment("Vault__Address",      vault.GetEndpoint("http"))
    .WithEnvironment("Vault__RoleIdPath",   RoleIdPath("orders"))
    .WithEnvironment("Vault__SecretIdPath", SecretIdPath("orders"))
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development");

// --- payments-svc ----------------------------------------------------------
// Payments owns Stripe + PayPal webhook ingress and publishes
// PaymentCompleted / PaymentSessionFailed / PaymentAmountMismatch /
// PaymentVerified / PaymentWebhookValidated to RabbitMQ via the
// per-context outbox in payments DB. Per-service Vault AppRole holds the
// provider API keys + webhook secrets (wired in Phase 3b/3c).
var payments = builder.AddProject<Projects.Payments_Api>("payments-svc")
    .WaitFor(vault)
    .WithReference(paymentsDb)
    .WithReference(rabbitmq)
    .WithEnvironment("Vault__Enabled",      "false")  // flip when Phase 3b wires Stripe/PayPal secrets
    .WithEnvironment("Vault__Address",      vault.GetEndpoint("http"))
    .WithEnvironment("Vault__RoleIdPath",   RoleIdPath("payments"))
    .WithEnvironment("Vault__SecretIdPath", SecretIdPath("payments"))
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development");

// --- bff-web ---------------------------------------------------------------
// Public HTTP edge. Composes services via REST clients (Phase 7b),
// pushes checkout updates to browsers via SignalR (Phase 7c), and hosts
// the demo surface (DemoController + DemoHub) the portfolio site talks to
// (Phase 9 — see docs/agent-briefs/portfolio-integration.md when written).
// Owns no DB; consumes PaymentSessionCreatedEvent from RabbitMQ to bridge
// the saga -> SignalR -> browser flow.
//
// Pinned to :5050 / :5051 because the portfolio site's .env.local hardcodes
// PUBLIC_API_URL=http://localhost:5050. macOS Control Center owns :5000
// so we shifted off it; :5050 is conventionally free. WithEndpoint(name,
// mutator) is used instead of WithHttpEndpoint(name:) to avoid the
// "Endpoint with name 'http' already exists" collision with the auto-
// generated endpoint from launchSettings.
var bffWeb = builder.AddProject<Projects.BffWeb_Api>("bff-web")
    .WaitFor(vault)
    .WithReference(rabbitmq)
    .WithReference(identity)
    .WithReference(catalog)
    .WithReference(orders)
    .WithReference(payments)
    .WithReference(checkout)
    .WithEndpoint("http",  e => e.Port = 5050)
    .WithEndpoint("https", e => e.Port = 5051)
    .WithExternalHttpEndpoints()
    .WithEnvironment("Vault__Enabled",      "false")
    .WithEnvironment("Vault__Address",      vault.GetEndpoint("http"))
    .WithEnvironment("Vault__RoleIdPath",   RoleIdPath("bff-web"))
    .WithEnvironment("Vault__SecretIdPath", SecretIdPath("bff-web"))
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development");

builder.Build().Run();
