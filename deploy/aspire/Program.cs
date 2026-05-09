using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

// =============================================================================
// PER-SERVICE VAULT CREDENTIAL PATHS
// =============================================================================
var credsHostDir = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "vault-creds"));
Directory.CreateDirectory(credsHostDir);

string RoleIdPath(string svc)   => Path.Combine(credsHostDir, svc, "role_id");
string SecretIdPath(string svc) => Path.Combine(credsHostDir, svc, "secret_id");

var vaultManifestsHostDir = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "infra", "vault"));

// =============================================================================
// INFRASTRUCTURE RESOURCES
// =============================================================================
// ContainerLifetime.Persistent on every long-lived infra container —
// without this, every `dotnet run` tears down + recreates Postgres,
// RabbitMQ, Vault etc. and pays their full warmup cost (~30s of
// healthcheck waiting). Persistent keeps them alive across runs;
// Aspire reattaches on the next boot and skips startup entirely.
var postgres = builder.AddPostgres("postgres")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("ritualworks-platform-postgres-data")
    .WithBindMount("./init-postgres.sql", "/docker-entrypoint-initdb.d/init.sql")
    .WithPgAdmin();

var identityDb = postgres.AddDatabase("identity");
var catalogDb  = postgres.AddDatabase("catalog");
var ordersDb   = postgres.AddDatabase("orders");
var paymentsDb = postgres.AddDatabase("payments");
var contentDb  = postgres.AddDatabase("content");
var checkoutDb = postgres.AddDatabase("checkout");
var notificationsDb = postgres.AddDatabase("notifications");

var redis = builder.AddRedis("redis")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("ritualworks-platform-redis-data")
    .WithRedisCommander();

var rabbitmq = builder.AddRabbitMQ("rabbitmq", port: 5672)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithManagementPlugin();

var pactDb = builder.AddPostgres("pact-db", port: null, password: null)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("ritualworks-platform-pact-db-data");
var pactBroker = builder.AddContainer("pact-broker", "pactfoundation/pact-broker", "latest")
    .WithLifetime(ContainerLifetime.Persistent)
    .WaitFor(pactDb)
    .WithEnvironment(ctx =>
    {
        var pactDbEndpoint = pactDb.Resource.PrimaryEndpoint;
        ctx.EnvironmentVariables["PACT_BROKER_DATABASE_HOST"]     = pactDbEndpoint.ContainerHost;
        ctx.EnvironmentVariables["PACT_BROKER_DATABASE_PORT"]     = pactDbEndpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ctx.EnvironmentVariables["PACT_BROKER_DATABASE_NAME"]     = "postgres";
        ctx.EnvironmentVariables["PACT_BROKER_DATABASE_USERNAME"] = "postgres";
        // Pass the parameter directly to avoid obsolete .Value call.
        ctx.EnvironmentVariables["PACT_BROKER_DATABASE_PASSWORD"] = pactDb.Resource.PasswordParameter;
        ctx.EnvironmentVariables["PACT_BROKER_BASE_URL"]          = "http://localhost:9292";
    })
    .WithHttpEndpoint(targetPort: 9292, name: "ui");

// LocalStack S3 emulator for hermetic local-dev. Production uses Fly Tigris;
// the AWS-SDK-based StorageOptions (feat/content/s3-presigned-storage) targets
// both transparently — only ServiceUrl + ForcePathStyle differ.
var localstack = builder.AddContainer("localstack", "localstack/localstack", "3")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithEnvironment("SERVICES", "s3")
    .WithEnvironment("AWS_DEFAULT_REGION", "us-east-1")
    .WithVolume("ritualworks-platform-localstack-data", "/var/lib/localstack")
    .WithHttpEndpoint(targetPort: 4566, name: "edge");

var clamav = builder.AddContainer("clamav", "clamav/clamav", "latest")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithEndpoint(port: 3310, targetPort: 3310, name: "clamd");

var meilisearch = builder.AddContainer("meilisearch", "getmeili/meilisearch", "v1.10")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithEnvironment("MEILI_MASTER_KEY", "dev_master_key_at_least_16_chars")
    .WithEnvironment("MEILI_NO_ANALYTICS", "true")
    .WithEnvironment("MEILI_ENV", "development")
    .WithVolume("ritualworks-platform-meilisearch-data", "/meili_data")
    .WithHttpEndpoint(targetPort: 7700, name: "http");

// Tempo needs a config file to start — without /etc/tempo.yaml it exits
// with "failed to create store: unknown backend """. Reuse the same
// config docker-compose bind-mounts (../../infra/tempo.yaml).
var tempoConfigPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "infra", "tempo.yaml"));
var tempo = builder.AddContainer("tempo", "grafana/tempo", "latest")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithBindMount(tempoConfigPath, "/etc/tempo.yaml", isReadOnly: true)
    .WithArgs("-config.file=/etc/tempo.yaml")
    .WithEndpoint(targetPort: 4317, name: "grpc", scheme: "http")
    .WithEndpoint(targetPort: 3200, name: "http", scheme: "http");

var vaultBootstrapToken = "dev-root-token";
var vault = builder.AddContainer("vault", "hashicorp/vault", "1.15")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithEnvironment("VAULT_DEV_ROOT_TOKEN_ID", vaultBootstrapToken)
    .WithEnvironment("VAULT_DEV_LISTEN_ADDRESS", "0.0.0.0:8200")
    .WithEnvironment("VAULT_LOG_LEVEL", "info")
    .WithHttpEndpoint(targetPort: 8200, name: "http")
    .WithArgs("server", "-dev");

var vaultInit = builder.AddContainer("vault-init", "hashicorp/vault", "1.15")
    .WithReference(postgres)
    .WithBindMount("./vault-init.sh", "/init.sh")
    .WithBindMount(credsHostDir, "/creds")
    .WithBindMount(vaultManifestsHostDir, "/manifests", isReadOnly: true)
    .WithEnvironment("VAULT_ADDR",  "http://vault:8200")
    .WithEnvironment("VAULT_TOKEN", vaultBootstrapToken)
    .WithEnvironment("ALLOW_DEV_SEED", "yes")
    .WithEntrypoint("/bin/sh")
    .WithArgs("/init.sh");

var seedScriptHostPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "scripts", "seed-vault-dev.sh"));
var vaultSeed = builder.AddContainer("vault-seed", "hashicorp/vault", "1.15")
    .WaitForCompletion(vaultInit)
    .WithBindMount(seedScriptHostPath, "/seed.sh")
    .WithEnvironment("VAULT_ADDR",  "http://vault:8200")
    .WithEnvironment("VAULT_TOKEN", vaultBootstrapToken)
    .WithEntrypoint("/bin/sh")
    .WithArgs("/seed.sh");

// --- identity-svc -----------------------------------------------------------
var identity = builder.AddProject<Projects.Identity_Api>("identity-svc")
    .WaitForCompletion(vaultSeed)
    .WaitFor(identityDb)
    .WithReference(identityDb)
    .WaitFor(rabbitmq)
    .WithReference(rabbitmq)
    .WithEnvironment("Vault__Enabled",      "true")
    .WithEnvironment("Vault__Address",      vault.GetEndpoint("http"))
    .WithEnvironment("Vault__RoleIdPath",   RoleIdPath("identity"))
    .WithEnvironment("Vault__SecretIdPath", SecretIdPath("identity"))
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development");

// JWKS validator config — every backend that uses AddJwksAuthentication
// needs JwksUri/Issuer/Audience or its host refuses to boot
// (JwksOptions has [Required] + ValidateOnStart). The URI is interpolated
// from identity-svc's runtime endpoint via a callback so it picks up
// whatever port Aspire's reverse-proxy assigns.
static IResourceBuilder<T> AddJwksConfig<T>(
    IResourceBuilder<T> svc,
    IResourceBuilder<ProjectResource> identitySvc) where T : IResourceWithEnvironment
    => svc.WithEnvironment(ctx =>
    {
        var url = identitySvc.GetEndpoint("http").Url;
        ctx.EnvironmentVariables["Authentication__Jwks__JwksUri"] = $"{url}/.well-known/jwks.json";
        ctx.EnvironmentVariables["Authentication__Jwks__Issuer"]   = "http://localhost";
        ctx.EnvironmentVariables["Authentication__Jwks__Audience"] = "ritualworks-dev";
    });

// --- catalog-svc -----------------------------------------------------------
// Replicated x2 so the BFF's HttpClient (which load-balances via Aspire's
// reverse proxy) round-robins requests across both. Each replica stamps
// X-Instance-Id on every response via UseInstanceIdHeader; the BFF
// captures it on the demo wire so the portfolio-site receipt strip shows
// the upstream replica that served the call. This is the visible proof
// that the platform actually distributes — visitor presses Run a few
// times and watches the catalog-svc-XXXX suffix rotate.
var catalog = AddJwksConfig(builder.AddProject<Projects.Catalog_Api>("catalog-svc")
    .WithReplicas(2)
    .WaitFor(vault)
    .WaitFor(catalogDb)
    .WithReference(catalogDb)
    .WaitFor(rabbitmq)
    .WithReference(rabbitmq)
    .WaitFor(identity)
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", tempo.GetEndpoint("grpc"))
    .WithEnvironment("Vault__Enabled",      "false")
    .WithEnvironment("Vault__Address",      vault.GetEndpoint("http"))
    .WithEnvironment("Vault__RoleIdPath",   RoleIdPath("catalog"))
    .WithEnvironment("Vault__SecretIdPath", SecretIdPath("catalog"))
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development"), identity);

// --- checkout-orchestrator-svc --------------------------------------------
var checkout = AddJwksConfig(builder.AddProject<Projects.CheckoutOrchestrator_Api>("checkout-svc")
    .WaitFor(vault)
    .WaitFor(checkoutDb)
    .WithReference(checkoutDb)
    .WaitFor(rabbitmq)
    .WithReference(rabbitmq)
    .WaitFor(identity)
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", tempo.GetEndpoint("grpc"))
    .WithEnvironment("Vault__Enabled",      "false")
    .WithEnvironment("Vault__Address",      vault.GetEndpoint("http"))
    .WithEnvironment("Vault__RoleIdPath",   RoleIdPath("checkout-orchestrator"))
    .WithEnvironment("Vault__SecretIdPath", SecretIdPath("checkout-orchestrator"))
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development"), identity);

// --- orders-svc ------------------------------------------------------------
var orders = AddJwksConfig(builder.AddProject<Projects.Orders_Api>("orders-svc")
    .WaitFor(vault)
    .WaitFor(ordersDb)
    .WithReference(ordersDb)
    .WaitFor(rabbitmq)
    .WithReference(rabbitmq)
    .WaitFor(identity)
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", tempo.GetEndpoint("grpc"))
    .WithEnvironment("Vault__Enabled",      "false")
    .WithEnvironment("Vault__Address",      vault.GetEndpoint("http"))
    .WithEnvironment("Vault__RoleIdPath",   RoleIdPath("orders"))
    .WithEnvironment("Vault__SecretIdPath", SecretIdPath("orders"))
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development"), identity);

// --- payments-svc ----------------------------------------------------------
var payments = AddJwksConfig(builder.AddProject<Projects.Payments_Api>("payments-svc")
    .WaitFor(vault)
    .WaitFor(paymentsDb)
    .WithReference(paymentsDb)
    .WaitFor(rabbitmq)
    .WithReference(rabbitmq)
    .WaitFor(identity)
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", tempo.GetEndpoint("grpc"))
    .WithEnvironment("Vault__Enabled",      "false")
    .WithEnvironment("Vault__Address",      vault.GetEndpoint("http"))
    .WithEnvironment("Vault__RoleIdPath",   RoleIdPath("payments"))
    .WithEnvironment("Vault__SecretIdPath", SecretIdPath("payments"))
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development"), identity);

// --- content-svc -----------------------------------------------------------
// content-svc reads/writes via the AWS S3 SDK against LocalStack here.
// Storage__* shape is set by StorageOptions on feat/content/s3-presigned-storage.
var content = AddJwksConfig(builder.AddProject<Projects.Content_Api>("content-svc")
    .WaitFor(vault)
    .WaitFor(contentDb)
    .WithReference(contentDb)
    .WaitFor(rabbitmq)
    .WithReference(rabbitmq)
    .WaitFor(identity)
    .WaitFor(localstack)
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", tempo.GetEndpoint("grpc"))
    .WithEnvironment("Vault__Enabled",      "false")
    .WithEnvironment("Vault__Address",      vault.GetEndpoint("http"))
    .WithEnvironment("Vault__RoleIdPath",   RoleIdPath("content"))
    .WithEnvironment("Vault__SecretIdPath", SecretIdPath("content"))
    .WithEnvironment("Storage__ServiceUrl",     localstack.GetEndpoint("edge"))
    .WithEnvironment("Storage__AccessKey",      "test")
    .WithEnvironment("Storage__SecretKey",      "test")
    .WithEnvironment("Storage__BucketName",     "content-dev")
    .WithEnvironment("Storage__Region",         "us-east-1")
    .WithEnvironment("Storage__ForcePathStyle", "true")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development"), identity);

// --- search-svc ------------------------------------------------------------
// Read-side projection of catalog. No DB, no Vault — just Meilisearch and
// HTTP back to catalog-svc for category lookups.
var search = AddJwksConfig(builder.AddProject<Projects.Search_Api>("search-svc")
    .WaitFor(meilisearch)
    .WaitFor(catalog)
    .WaitFor(identity)
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", tempo.GetEndpoint("grpc"))
    .WithEnvironment("Vault__Enabled",        "false")
    .WithEnvironment("Meilisearch__Url",      meilisearch.GetEndpoint("http"))
    .WithEnvironment("Meilisearch__MasterKey", "dev_master_key_at_least_16_chars")
    .WithEnvironment("Meilisearch__IndexName", "products")
    .WithEnvironment(ctx =>
    {
        ctx.EnvironmentVariables["Catalog__BaseAddress"] = catalog.GetEndpoint("http").Url;
    })
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development"), identity);

// --- notifications-svc -----------------------------------------------------
var notifications = AddJwksConfig(builder.AddProject<Projects.Notifications_Api>("notifications-svc")
    .WaitFor(vault)
    .WaitFor(notificationsDb)
    .WithReference(notificationsDb)
    .WaitFor(rabbitmq)
    .WithReference(rabbitmq)
    .WaitFor(identity)
    .WaitFor(redis)
    .WithReference(redis)
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", tempo.GetEndpoint("grpc"))
    .WithEnvironment("Vault__Enabled",      "false")
    .WithEnvironment("Vault__Address",      vault.GetEndpoint("http"))
    .WithEnvironment("Vault__RoleIdPath",   RoleIdPath("notifications"))
    .WithEnvironment("Vault__SecretIdPath", SecretIdPath("notifications"))
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development"), identity);

// --- bff-web ---------------------------------------------------------------
var bffWeb = AddJwksConfig(builder.AddProject<Projects.BffWeb_Api>("bff-web")
    .WaitFor(vault)
    .WaitFor(rabbitmq)
    .WithReference(rabbitmq)
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", tempo.GetEndpoint("grpc"))
    .WaitFor(identity)
    .WithReference(identity)
    .WaitFor(catalog)
    .WithReference(catalog)
    .WaitFor(orders)
    .WithReference(orders)
    .WaitFor(payments)
    .WithReference(payments)
    .WaitFor(checkout)
    .WithReference(checkout)
    .WaitFor(content)
    .WithReference(content)
    .WaitFor(search)
    .WithReference(search)
    .WaitFor(notifications)
    .WithReference(notifications)
    .WithEndpoint("http",  e => e.Port = 5050)
    .WithEndpoint("https", e => e.Port = 5051)
    .WithExternalHttpEndpoints()
    .WithEnvironment("Vault__Enabled",      "false")
    .WithEnvironment("Vault__Address",      vault.GetEndpoint("http"))
    .WithEnvironment("Vault__RoleIdPath",   RoleIdPath("bff-web"))
    .WithEnvironment("Vault__SecretIdPath", SecretIdPath("bff-web"))
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development"), identity);

builder.Services.AddHostedService<ResourceFileLogger>();

builder.Build().Run();

// =============================================================================
// RESOURCE FILE LOGGER
// =============================================================================
internal sealed class ResourceFileLogger(
    ResourceLoggerService loggerService,
    ResourceNotificationService notificationService,
    IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var resourceEvent in notificationService.WatchAsync(stoppingToken))
        {
            var state = resourceEvent.Snapshot.State;
            if (state == "Starting" || state == "Running")
            {
                _ = Task.Run(() => CaptureLogsAsync(resourceEvent.ResourceId, stoppingToken), stoppingToken);
            }
        }
    }

    private async Task CaptureLogsAsync(string resourceId, CancellationToken ct)
    {
        var logDir = configuration["ASPIRE_LOGS_DIR"] ?? Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, resourceId + ".log");

        using var writer = new StreamWriter(logPath, append: true) { AutoFlush = true };
        await foreach (var logs in loggerService.WatchAsync(resourceId).WithCancellation(ct))
        {
            foreach (var line in logs)
            {
                await writer.WriteLineAsync("[" + DateTime.Now.ToString("T") + "] " + (line.IsErrorMessage ? "ERR" : "INF") + " " + line.Content);
            }
        }
    }
}
