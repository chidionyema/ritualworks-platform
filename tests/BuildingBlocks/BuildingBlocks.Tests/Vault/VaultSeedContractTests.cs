using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Haworks.BuildingBlocks.Tests.Vault;

/// <summary>
/// Contract tests that parse the Vault infrastructure manifests
/// (infra/vault/*.json, *.hcl.tmpl) and verify they match C# runtime
/// expectations. These tests prevent the class of bug discovered in the
/// war room: seed.sh creating dynamic roles while C# calls
/// GetStaticCredentialsAsync.
///
/// All tests are purely file-based (no Vault container needed).
/// </summary>
[Trait("Category", "Unit")]
public sealed class VaultSeedContractTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException(
            "Cannot find repository root (.git directory) from " + AppContext.BaseDirectory);
    }

    private static string InfraPath(params string[] segments)
        => Path.Combine(new[] { RepoRoot, "infra", "vault" }.Concat(segments).ToArray());

    private static JsonElement LoadJson(params string[] segments)
    {
        var path = InfraPath(segments);
        var json = File.ReadAllText(path);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void ServicesJson_AllServicesWithDbFlag_HaveMatchingDatabaseRole()
    {
        var services = LoadJson("services.json");
        var roles = LoadJson("database", "roles.json");

        var roleNames = roles.GetProperty("roles")
            .EnumerateArray()
            .Select(r => r.GetProperty("role_name").GetString()!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var svc in services.GetProperty("services").EnumerateArray())
        {
            if (!svc.TryGetProperty("has_db", out var hasDb) || !hasDb.GetBoolean())
                continue;

            var dbRole = svc.GetProperty("db_role").GetString()!;

            roleNames.Should().Contain(dbRole,
                $"service '{svc.GetProperty("name").GetString()}' has has_db=true " +
                $"and db_role='{dbRole}' but no matching entry exists in database/roles.json");
        }
    }

    [Fact]
    public void DatabaseRolesJson_AllRoles_HaveUsernameField()
    {
        // Static roles require a pre-existing Postgres username; dynamic roles
        // do not have one. If someone removes the username field, seed.sh
        // would fall back to dynamic role creation — which breaks the C# code
        // that calls GetStaticCredentialsAsync.
        var roles = LoadJson("database", "roles.json");

        foreach (var role in roles.GetProperty("roles").EnumerateArray())
        {
            var roleName = role.GetProperty("role_name").GetString()!;

            role.TryGetProperty("username", out var username).Should().BeTrue(
                $"role '{roleName}' must have a 'username' field (static role requirement)");

            username.GetString().Should().NotBeNullOrWhiteSpace(
                $"role '{roleName}' has an empty username — seed.sh would create a dynamic role " +
                "but C# calls GetStaticCredentialsAsync");
        }
    }

    [Fact]
    public void ApproleTemplate_SecretIdTtl_IsNotUnlimited()
    {
        // A secret_id_ttl of "0" means unlimited lifetime, which is a security
        // finding: leaked secret IDs never expire. The template should enforce
        // a finite TTL.
        var template = LoadJson("auth", "approle-template.json");
        var ttl = template.GetProperty("secret_id_ttl").GetString()!;

        ttl.Should().NotBe("0",
            "secret_id_ttl must not be '0' (unlimited) — leaked secret IDs would never expire");
        ttl.Should().NotBeNullOrWhiteSpace(
            "secret_id_ttl must be explicitly set to a finite duration");
    }

    [Fact]
    public void PolicyTemplate_GrantsStaticCredsPath_NotDynamicCredsPath()
    {
        // The C# code calls GetStaticCredentialsAsync, so the policy must
        // grant access to database/static-creds/haworks-{{service}}.
        // If someone changes it to database/creds/ (dynamic), the runtime
        // would get a 403.
        var policyPath = InfraPath("policies", "service-template.hcl.tmpl");
        var policyContent = File.ReadAllText(policyPath);

        policyContent.Should().Contain("database/static-creds/haworks-{{service}}",
            "policy template must grant static-creds path to match C# GetStaticCredentialsAsync");
    }

    [Fact]
    public void ServicesJson_NoDeletedServices()
    {
        // Verify that every service listed in services.json has a corresponding
        // directory under src/. Catches dead entries like the Content service
        // that was deleted but could linger in the manifest.
        var services = LoadJson("services.json");

        // Map kebab-case service names to PascalCase directory names.
        // e.g. "checkout-orchestrator" -> "CheckoutOrchestrator", "bff-web" -> "BffWeb"
        static string ToPascalCase(string kebab)
        {
            return string.Concat(
                kebab.Split('-')
                     .Select(segment => CultureInfo.InvariantCulture.TextInfo
                         .ToTitleCase(segment)));
        }

        foreach (var svc in services.GetProperty("services").EnumerateArray())
        {
            var name = svc.GetProperty("name").GetString()!;
            var dirName = ToPascalCase(name);
            var srcDir = Path.Combine(RepoRoot, "src", dirName);

            Directory.Exists(srcDir).Should().BeTrue(
                $"service '{name}' is listed in services.json but src/{dirName}/ does not exist — " +
                "remove the dead entry from services.json");
        }
    }
}
