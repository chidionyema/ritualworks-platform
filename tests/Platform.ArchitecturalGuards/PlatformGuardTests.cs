using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Haworks.Platform.ArchitecturalGuards;

/// <summary>
/// Platform-wide architectural guard tests. These run in CI and catch
/// the systemic patterns found during the May 2026 war room audit.
/// Each test prevents a specific class of bug from being reintroduced.
/// </summary>
public sealed class PlatformGuardTests
{
    private static readonly string SrcRoot = FindSrcRoot();
    private static readonly string[] ServiceNames =
    [
        "Audit", "BffWeb", "Catalog", "CheckoutOrchestrator", "Content",
        "Identity", "Location", "Merchant", "Notifications", "Orders",
        "Payments", "Payouts", "Pricing", "Privacy", "Scheduler", "Search", "Webhooks"
    ];

    // ─── Auth & Middleware ────────────────────────────────────────────

    [Fact]
    public void Every_Program_cs_calls_UseAuthentication_before_UseAuthorization()
    {
        var violations = new List<string>();
        foreach (var programCs in FindProgramFiles())
        {
            var content = File.ReadAllText(programCs);
            if (!content.Contains("UseAuthorization")) continue; // no auth pipeline

            var authIdx = content.IndexOf("UseAuthentication()", StringComparison.Ordinal);
            var authzIdx = content.IndexOf("UseAuthorization()", StringComparison.Ordinal);

            if (authIdx < 0)
                violations.Add($"{Relative(programCs)}: UseAuthorization called but UseAuthentication is MISSING");
            else if (authIdx > authzIdx)
                violations.Add($"{Relative(programCs)}: UseAuthentication must come BEFORE UseAuthorization");
        }
        violations.Should().BeEmpty("every service must call UseAuthentication() before UseAuthorization()");
    }

    [Fact]
    public void Every_controller_has_Authorize_or_AllowAnonymous_attribute()
    {
        var violations = new List<string>();
        foreach (var file in FindControllerFiles())
        {
            var content = File.ReadAllText(file);
            // Skip if class-level [Authorize] or [AllowAnonymous] exists
            if (content.Contains("[Authorize") || content.Contains("[AllowAnonymous]")) continue;
            violations.Add($"{Relative(file)}: controller has no [Authorize] or [AllowAnonymous] attribute");
        }
        violations.Should().BeEmpty("every controller must explicitly declare its auth policy");
    }

    // ─── Domain Guards ───────────────────────────────────────────────

    [Fact]
    public void No_Guid_NewGuid_as_placeholder_in_production_consumer_code()
    {
        var violations = new List<string>();
        foreach (var file in FindConsumerFiles())
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("Guid.NewGuid()") &&
                    (lines[i].Contains("Placeholder") || lines[i].Contains("placeholder") ||
                     lines[i].Contains("// TODO") || lines[i].Contains("//TODO")))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: placeholder Guid.NewGuid() in production path");
                }
            }
        }
        violations.Should().BeEmpty("placeholder Guid.NewGuid() must not exist in production consumer code");
    }

    [Fact]
    public void No_NotImplementedException_in_production_code()
    {
        var violations = new List<string>();
        foreach (var file in FindProductionCsFiles())
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("throw new NotImplementedException") &&
                    !file.Contains("/Test") && !file.Contains("/test"))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: NotImplementedException in production code");
                }
            }
        }
        violations.Should().BeEmpty("NotImplementedException must not exist in production code paths");
    }

    // ─── Validators ──────────────────────────────────────────────────

    [Fact]
    public void No_GreaterThanOrEqualTo_zero_on_amount_validators()
    {
        var violations = new List<string>();
        foreach (var file in FindValidatorFiles())
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("GreaterThanOrEqualTo(0)") &&
                    (lines[i].Contains("Amount") || lines[i].Contains("Total") || lines[i].Contains("Price")))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: use GreaterThan(0), not GreaterThanOrEqualTo(0) for financial amounts");
                }
            }
        }
        violations.Should().BeEmpty("financial amount validators must use GreaterThan(0) to prevent $0 transactions");
    }

    [Fact]
    public void No_GreaterThan_DateTimeOffset_UtcNow_in_validators()
    {
        var violations = new List<string>();
        foreach (var file in FindValidatorFiles())
        {
            var content = File.ReadAllText(file);
            if (Regex.IsMatch(content, @"\.GreaterThan\(DateTimeOffset\.UtcNow\)"))
            {
                violations.Add($"{Relative(file)}: use .Must(t => t > DateTimeOffset.UtcNow) instead — .GreaterThan captures startup time");
            }
        }
        violations.Should().BeEmpty("date validators must use .Must() lambda, not .GreaterThan(UtcNow) which freezes at startup");
    }

    // ─── Financial ───────────────────────────────────────────────────

    [Fact]
    public void No_long_cast_truncation_for_Stripe_amounts()
    {
        var violations = new List<string>();
        foreach (var file in FindProductionCsFiles())
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                // Match (long)(amount * 100) but not (long)Math.Round(...)
                if (Regex.IsMatch(lines[i], @"\(long\)\s*\(.*\*\s*100") &&
                    !lines[i].Contains("Math.Round"))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: use (long)Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero) instead of (long)(amount * 100)");
                }
            }
        }
        violations.Should().BeEmpty("Stripe amount conversions must use Math.Round to avoid truncation");
    }

    [Fact]
    public void No_hardcoded_USD_currency()
    {
        var violations = new List<string>();
        foreach (var file in FindSagaFiles())
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], @"Currency\s*=\s*""USD""") &&
                    !lines[i].TrimStart().StartsWith("//"))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: currency must not be hardcoded to USD — carry from source event");
                }
            }
        }
        violations.Should().BeEmpty("currency must be configurable, not hardcoded");
    }

    // ─── Consumers ───────────────────────────────────────────────────

    [Fact]
    public void No_GetByIdAsync_followed_by_mutation_in_consumers()
    {
        var violations = new List<string>();
        foreach (var file in FindConsumerFiles())
        {
            var content = File.ReadAllText(file);
            if (content.Contains("GetByIdAsync") &&
                (content.Contains("SaveChangesAsync") || content.Contains("MarkRefunded") ||
                 content.Contains("MarkPaid") || content.Contains("MarkCompleted")))
            {
                // Check it's not GetByIdTrackedAsync
                if (!content.Contains("GetByIdTrackedAsync") ||
                    content.IndexOf("GetByIdAsync") != content.IndexOf("GetByIdTrackedAsync"))
                {
                    violations.Add($"{Relative(file)}: uses GetByIdAsync with mutation — use GetByIdTrackedAsync for entities you modify");
                }
            }
        }
        // Note: this is a heuristic, may have false positives
        violations.Should().BeEmpty("consumers that modify entities must use GetByIdTrackedAsync");
    }

    // ─── EF Core ─────────────────────────────────────────────────────

    [Fact]
    public void No_ExecuteUpdateAsync_without_subsequent_SaveChangesAsync_in_consumers()
    {
        var violations = new List<string>();
        foreach (var file in FindConsumerFiles())
        {
            var content = File.ReadAllText(file);
            if (content.Contains("ExecuteUpdateAsync") &&
                content.Contains("PublishAsync") &&
                !content.Contains("SaveChangesAsync"))
            {
                violations.Add($"{Relative(file)}: ExecuteUpdateAsync + PublishAsync without SaveChangesAsync — outbox not flushed");
            }
        }
        violations.Should().BeEmpty("after ExecuteUpdateAsync, SaveChangesAsync must be called to flush the outbox");
    }

    // ─── Deployment ──────────────────────────────────────────────────

    [Fact]
    public void Every_Dockerfile_sets_non_root_user()
    {
        var violations = new List<string>();
        foreach (var dockerfile in Directory.GetFiles(SrcRoot, "Dockerfile", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(dockerfile);
            if (!content.Contains("USER") && !content.Contains("user "))
            {
                violations.Add($"{Relative(dockerfile)}: Dockerfile runs as root — add USER $APP_UID");
            }
        }
        violations.Should().BeEmpty("all Dockerfiles must run as non-root user");
    }

    // ─── Config ──────────────────────────────────────────────────────

    [Fact]
    public void No_passwords_in_appsettings_json()
    {
        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(SrcRoot, "appsettings.json", SearchOption.AllDirectories))
        {
            if (file.Contains("obj") || file.Contains("bin")) continue;
            var content = File.ReadAllText(file);
            if (Regex.IsMatch(content, @"Password\s*=\s*[^;""]+", RegexOptions.IgnoreCase) &&
                !file.Contains("Development") && !file.Contains("Test"))
            {
                violations.Add($"{Relative(file)}: contains password in connection string — use environment variables");
            }
        }
        violations.Should().BeEmpty("passwords must not be committed in appsettings.json");
    }

    [Fact]
    public void No_guest_credential_fallbacks_in_production_code()
    {
        var violations = new List<string>();
        foreach (var file in FindProductionCsFiles())
        {
            if (file.Contains("/Test") || file.Contains(".Testing")) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("?? \"guest\"") || lines[i].Contains("?? \"amqp://guest"))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: guest credential fallback — fail loudly on missing config");
                }
            }
        }
        violations.Should().BeEmpty("services must not fall back to guest credentials");
    }

    // ─── Shared Infrastructure ───────────────────────────────────────

    [Fact]
    public void HybridCache_does_not_call_factory_when_lock_not_acquired()
    {
        var cacheFile = Path.Combine(SrcRoot, "BuildingBlocks", "Caching", "HybridCache.cs");
        if (!File.Exists(cacheFile)) return;

        var content = File.ReadAllText(cacheFile);
        // After WaitAsync returns false, the factory should NOT be called
        content.Should().Contain("if (!lockAcquired)",
            "HybridCache must check lockAcquired and skip factory on timeout");
    }

    [Fact]
    public void JWKS_requires_HTTPS_in_non_development()
    {
        var jwksFile = Path.Combine(SrcRoot, "BuildingBlocks", "Authentication", "JwksAuthenticationExtensions.cs");
        if (!File.Exists(jwksFile)) return;

        var content = File.ReadAllText(jwksFile);
        content.Should().NotContain("RequireHttps = false",
            "JWKS must require HTTPS in non-Development environments");
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static string FindSrcRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null && !File.Exists(Path.Combine(dir, "RitualworksPlatform.sln")))
            dir = Directory.GetParent(dir)?.FullName;
        return Path.Combine(dir ?? ".", "src");
    }

    private static string Relative(string path) =>
        path.Replace(Directory.GetParent(SrcRoot)!.FullName + Path.DirectorySeparatorChar, "");

    private static IEnumerable<string> FindProgramFiles() =>
        Directory.GetFiles(SrcRoot, "Program.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin") && f.Contains(".Api"));

    private static IEnumerable<string> FindControllerFiles() =>
        Directory.GetFiles(SrcRoot, "*Controller.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin") && !f.Contains("Test") && f.Contains("Controllers"));

    private static IEnumerable<string> FindConsumerFiles() =>
        Directory.GetFiles(SrcRoot, "*Consumer*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(SrcRoot, "*Worker*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains("obj") && !f.Contains("bin") && !f.Contains("Test"));

    private static IEnumerable<string> FindValidatorFiles() =>
        Directory.GetFiles(SrcRoot, "*Validator*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin") && !f.Contains("Test"));

    private static IEnumerable<string> FindSagaFiles() =>
        Directory.GetFiles(SrcRoot, "*Saga*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin") && !f.Contains("Test") && !f.Contains("State") && !f.Contains("Migration"));

    private static IEnumerable<string> FindProductionCsFiles() =>
        Directory.GetFiles(SrcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin") && !f.Contains("/Test") && !f.Contains(".Testing"));
}
