using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Haworks.BuildingBlocks.Testing.Containers;

/// <summary>
/// Lazy-singleton HashiCorp Vault dev-mode container shared across every
/// integration assembly. <c>WithReuse(true)</c> keeps the same container
/// across <c>dotnet test</c> invocations as long as the builder config
/// hash is unchanged — keep this builder static and identical, do not
/// parameterize.
///
/// The container runs in dev mode with a fixed root token. Helper methods
/// seed AppRoles, KV secrets, and static database roles for tests.
/// </summary>
public static class SharedTestVault
{
    private const string Image     = "hashicorp/vault:1.18";
    private const string RootToken = "test-root-token";
    private const int    Port      = 8200;

    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static IContainer? _container;
    private static int _mappedPort;

    private static async Task<IContainer> GetAsync()
    {
        if (_container is { State: TestcontainersStates.Running })
            return _container;
        if (!await _gate.WaitAsync(TimeSpan.FromMinutes(5)))
            throw new TimeoutException("SharedTestVault container gate timed out after 5 minutes");
        try
        {
            if (_container is null)
            {
                _container = new ContainerBuilder()
                    .WithImage(Image)
                    .WithEnvironment("VAULT_DEV_ROOT_TOKEN_ID",  RootToken)
                    .WithEnvironment("VAULT_DEV_LISTEN_ADDRESS", "0.0.0.0:8200")
                    .WithPortBinding(Port, assignRandomHostPort: true)
                    .WithWaitStrategy(Wait.ForUnixContainer()
                        .UntilHttpRequestIsSucceeded(r => r
                            .ForPort(Port)
                            .ForPath("/v1/sys/health")))
                    .WithReuse(true)
                    .Build();
            }
            if (_container.State != TestcontainersStates.Running)
            {
                await _container.StartAsync();
                _mappedPort = _container.GetMappedPublicPort(Port);
            }
            return _container;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Returns the HTTP address of the Vault dev server (e.g. http://localhost:32781).
    /// </summary>
    public static async Task<string> GetAddressAsync()
    {
        var container = await GetAsync();
        return $"http://{container.Hostname}:{_mappedPort}";
    }

    /// <summary>
    /// Returns the dev-mode root token.
    /// </summary>
    public static Task<string> GetRootTokenAsync()
        => Task.FromResult(RootToken);

    /// <summary>
    /// Creates an AppRole with a minimal KV-read policy for the given service,
    /// enables AppRole auth if not already enabled, and returns (roleId, secretId).
    /// </summary>
    public static async Task<(string RoleId, string SecretId)> SeedAppRoleAsync(string serviceName)
    {
        var address = await GetAddressAsync();
        using var http = CreateRootHttpClient(address);

        // Enable AppRole auth (idempotent — ignore 400 "already enabled").
        var enableResp = await http.PostAsJsonAsync("/v1/sys/auth/approle", new { type = "approle" });
        if (!enableResp.IsSuccessStatusCode)
        {
            var body = await enableResp.Content.ReadAsStringAsync();
            if (!body.Contains("already in use", StringComparison.OrdinalIgnoreCase))
                enableResp.EnsureSuccessStatusCode();
        }

        // Policy: read access to this service's KV paths.
        var policyName = $"{serviceName}-policy";
        var policyHcl = $"path \"secret/data/{serviceName}/*\" {{ capabilities = [\"read\"] }}\n" +
                        $"path \"secret/data/{serviceName}\" {{ capabilities = [\"read\"] }}";
        (await http.PostAsJsonAsync($"/v1/sys/policies/acl/{policyName}",
            new { policy = policyHcl })).EnsureSuccessStatusCode();

        // AppRole bound to the policy.
        var roleName = $"{serviceName}-role";
        (await http.PostAsJsonAsync($"/v1/auth/approle/role/{roleName}", new
        {
            token_policies = policyName,
            token_ttl      = "1h",
            token_max_ttl  = "24h",
            secret_id_ttl  = "0",
            bind_secret_id = true,
        })).EnsureSuccessStatusCode();

        // Fetch role_id.
        var roleIdResp = await http.GetFromJsonAsync<JsonElement>(
            $"/v1/auth/approle/role/{roleName}/role-id");
        var roleId = roleIdResp.GetProperty("data").GetProperty("role_id").GetString()!;

        // Generate secret_id.
        var secretIdResp = await http.PostAsync(
            $"/v1/auth/approle/role/{roleName}/secret-id",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        secretIdResp.EnsureSuccessStatusCode();
        var secretIdBody = await secretIdResp.Content.ReadFromJsonAsync<JsonElement>();
        var secretId = secretIdBody.GetProperty("data").GetProperty("secret_id").GetString()!;

        return (roleId, secretId);
    }

    /// <summary>
    /// Writes a KV v2 secret at the given path under the default "secret" mount.
    /// </summary>
    public static async Task SeedKvSecretAsync(string path, Dictionary<string, object> data)
    {
        var address = await GetAddressAsync();
        using var http = CreateRootHttpClient(address);

        (await http.PostAsJsonAsync($"/v1/secret/data/{path}", new { data }))
            .EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Creates a static database role in Vault's database secrets engine.
    /// Requires a running Postgres instance; call after SharedTestPostgres setup.
    /// </summary>
    public static async Task SeedStaticRoleAsync(
        string roleName,
        string username,
        string postgresConnectionString)
    {
        var address = await GetAddressAsync();
        using var http = CreateRootHttpClient(address);

        // Enable database secrets engine (idempotent).
        var enableResp = await http.PostAsJsonAsync("/v1/sys/mounts/database", new { type = "database" });
        if (!enableResp.IsSuccessStatusCode)
        {
            var body = await enableResp.Content.ReadAsStringAsync();
            if (!body.Contains("already in use", StringComparison.OrdinalIgnoreCase) &&
                !body.Contains("existing mount", StringComparison.OrdinalIgnoreCase))
                enableResp.EnsureSuccessStatusCode();
        }

        // Parse Postgres connection string into parts Vault expects.
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(postgresConnectionString);

        // Configure Postgres connection in Vault.
        (await http.PostAsJsonAsync("/v1/database/config/test-postgres", new
        {
            plugin_name           = "postgresql-database-plugin",
            allowed_roles         = roleName,
            connection_url        = $"postgresql://{{{{username}}}}:{{{{password}}}}@{builder.Host}:{builder.Port}/{builder.Database}?sslmode=disable",
            username              = builder.Username,
            password              = builder.Password,
        })).EnsureSuccessStatusCode();

        // Create the static role.
        (await http.PostAsJsonAsync($"/v1/database/static-roles/{roleName}", new
        {
            db_name         = "test-postgres",
            username        = username,
            rotation_period = "86400",
        })).EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Issues a response-wrapped secret_id for an existing AppRole.
    /// Returns the wrapping token (single-use).
    /// </summary>
    public static async Task<string> IssueWrappedSecretIdAsync(string serviceName, int wrapTtlSeconds = 300)
    {
        var address = await GetAddressAsync();
        using var http = CreateRootHttpClient(address);

        var roleName = $"{serviceName}-role";
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/auth/approle/role/{roleName}/secret-id");
        req.Headers.Add("X-Vault-Wrap-TTL", wrapTtlSeconds.ToString());
        req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("wrap_info").GetProperty("token").GetString()!;
    }

    // Test-only helper — IHttpClientFactory is unavailable in static test singletons.
#pragma warning disable HWK022
    private static HttpClient CreateRootHttpClient(string address)
    {
        var http = new HttpClient { BaseAddress = new Uri(address) };
        http.DefaultRequestHeaders.Add("X-Vault-Token", RootToken);
        return http;
    }
#pragma warning restore HWK022
}
