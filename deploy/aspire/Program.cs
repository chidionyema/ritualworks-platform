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

var rabbitmq = builder.AddRabbitMQ("rabbitmq", port: 5672)
    .WithManagementPlugin();

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
        // Pass the parameter directly to avoid obsolete .Value call.
        ctx.EnvironmentVariables["PACT_BROKER_DATABASE_PASSWORD"] = pactDb.Resource.PasswordParameter;
        ctx.EnvironmentVariables["PACT_BROKER_BASE_URL"]          = "http://localhost:9292";
    })
    .WithHttpEndpoint(targetPort: 9292, name: "ui");

var minio = builder.AddContainer("minio", "minio/minio", "latest")
    .WithVolume("ritualworks-platform-minio-data", "/data")
    .WithEnvironment("MINIO_ROOT_USER", "minioadmin")
    .WithEnvironment("MINIO_ROOT_PASSWORD", "minioadmin")
    .WithArgs("server", "/data", "--console-address", ":9001")
    .WithHttpEndpoint(targetPort: 9000, name: "s3")
    .WithHttpEndpoint(targetPort: 9001, name: "console");

var clamav = builder.AddContainer("clamav", "clamav/clamav", "latest")
    .WithEndpoint(port: 3310, targetPort: 3310, name: "clamd");

var tempo = builder.AddContainer("tempo", "grafana/tempo", "latest")
    .WithEndpoint(targetPort: 4317, name: "grpc", scheme: "http")
    .WithEndpoint(targetPort: 3200, name: "http", scheme: "http");

var vaultBootstrapToken = "dev-root-token";
var vault = builder.AddContainer("vault", "hashicorp/vault", "1.15")
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

// --- catalog-svc -----------------------------------------------------------
// Replicated x2 so the BFF's HttpClient (which load-balances via Aspire's
// reverse proxy) round-robins requests across both. Each replica stamps
// X-Instance-Id on every response via UseInstanceIdHeader; the BFF
// captures it on the demo wire so the portfolio-site receipt strip shows
// the upstream replica that served the call. This is the visible proof
// that the platform actually distributes — visitor presses Run a few
// times and watches the catalog-svc-XXXX suffix rotate.
var catalog = builder.AddProject<Projects.Catalog_Api>("catalog-svc")
    .WithReplicas(2)
    .WaitFor(vault)
    .WaitFor(catalogDb)
    .WithReference(catalogDb)
    .WaitFor(rabbitmq)
    .WithReference(rabbitmq)
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", tempo.GetEndpoint("grpc"))
    .WithEnvironment("Vault__Enabled",      "false")
    .WithEnvironment("Vault__Address",      vault.GetEndpoint("http"))
    .WithEnvironment("Vault__RoleIdPath",   RoleIdPath("catalog"))
    .WithEnvironment("Vault__SecretIdPath", SecretIdPath("catalog"))
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development");

// --- checkout-orchestrator-svc --------------------------------------------
var checkout = builder.AddProject<Projects.CheckoutOrchestrator_Api>("checkout-svc")
    .WaitFor(vault)
    .WaitFor(checkoutDb)
    .WithReference(checkoutDb)
    .WaitFor(rabbitmq)
    .WithReference(rabbitmq)
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", tempo.GetEndpoint("grpc"))
    .WithEnvironment("Vault__Enabled",      "false")
    .WithEnvironment("Vault__Address",      vault.GetEndpoint("http"))
    .WithEnvironment("Vault__RoleIdPath",   RoleIdPath("checkout-orchestrator"))
    .WithEnvironment("Vault__SecretIdPath", SecretIdPath("checkout-orchestrator"))
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development");

// --- orders-svc ------------------------------------------------------------
var orders = builder.AddProject<Projects.Orders_Api>("orders-svc")
    .WaitFor(vault)
    .WaitFor(ordersDb)
    .WithReference(ordersDb)
    .WaitFor(rabbitmq)
    .WithReference(rabbitmq)
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", tempo.GetEndpoint("grpc"))
    .WithEnvironment("Vault__Enabled",      "false")
    .WithEnvironment("Vault__Address",      vault.GetEndpoint("http"))
    .WithEnvironment("Vault__RoleIdPath",   RoleIdPath("orders"))
    .WithEnvironment("Vault__SecretIdPath", SecretIdPath("orders"))
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development");

// --- payments-svc ----------------------------------------------------------
var payments = builder.AddProject<Projects.Payments_Api>("payments-svc")
    .WaitFor(vault)
    .WaitFor(paymentsDb)
    .WithReference(paymentsDb)
    .WaitFor(rabbitmq)
    .WithReference(rabbitmq)
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", tempo.GetEndpoint("grpc"))
    .WithEnvironment("Vault__Enabled",      "false")
    .WithEnvironment("Vault__Address",      vault.GetEndpoint("http"))
    .WithEnvironment("Vault__RoleIdPath",   RoleIdPath("payments"))
    .WithEnvironment("Vault__SecretIdPath", SecretIdPath("payments"))
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development");

// --- bff-web ---------------------------------------------------------------
var bffWeb = builder.AddProject<Projects.BffWeb_Api>("bff-web")
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
    .WithEndpoint("http",  e => e.Port = 5050)
    .WithEndpoint("https", e => e.Port = 5051)
    .WithExternalHttpEndpoints()
    .WithEnvironment("Vault__Enabled",      "false")
    .WithEnvironment("Vault__Address",      vault.GetEndpoint("http"))
    .WithEnvironment("Vault__RoleIdPath",   RoleIdPath("bff-web"))
    .WithEnvironment("Vault__SecretIdPath", SecretIdPath("bff-web"))
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development");

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
