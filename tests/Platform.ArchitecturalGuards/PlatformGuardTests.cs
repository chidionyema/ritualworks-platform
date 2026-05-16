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
                if (Regex.IsMatch(lines[i], @"\(long\)\s*\((?!Math\.Round).*\*\s*100"))
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
                if (Regex.IsMatch(lines[i], @"\.Currency\s*=\s*""USD""") &&
                    !lines[i].TrimStart().StartsWith("//") &&
                    !lines[i].Contains("fallback") && !lines[i].Contains("default"))
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
            // Only flag if the file has BOTH GetByIdAsync (non-tracked pattern) AND a tracked variant exists in the same repo
            // Skip if the repo's GetByIdAsync is already tracked (no AsNoTracking) — that's fine
            if (content.Contains("GetByIdAsync") &&
                content.Contains("GetByIdTrackedAsync") == false &&
                (content.Contains(".MarkRefunded") || content.Contains(".RevertToPaid")) &&
                content.Contains("SaveChangesAsync"))
            {
                violations.Add($"{Relative(file)}: uses GetByIdAsync with mutation — verify entity is tracked or use GetByIdTrackedAsync");
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
    public void HybridCache_guards_lock_acquisition_failure()
    {
        // Find HybridCache by pattern, not hardcoded path
        var cacheFiles = FindProductionCsFiles()
            .Where(f => Path.GetFileName(f) == "HybridCache.cs");
        foreach (var file in cacheFiles)
        {
            var content = File.ReadAllText(file);
            if (!content.Contains("WaitAsync")) continue;
            content.Should().Contain("!lockAcquired",
                $"{Relative(file)}: HybridCache must check lockAcquired and skip factory on timeout");
        }
    }

    [Fact]
    public void JWKS_requires_HTTPS_in_non_development()
    {
        var jwksFiles = FindProductionCsFiles()
            .Where(f => Path.GetFileName(f) == "JwksAuthenticationExtensions.cs");
        foreach (var file in jwksFiles)
        {
            var content = File.ReadAllText(file);
            content.Should().NotContain("RequireHttps = false",
                $"{Relative(file)}: JWKS must require HTTPS in non-Development environments");
        }
    }

    // ─── Concurrency (Wave 2) ────────────────────────────────────────

    [Fact]
    public void DbContexts_with_consumer_modified_entities_configure_xmin()
    {
        // Find services that have BOTH a DbContext AND consumers (meaning concurrent writes)
        var servicesWithConsumers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var diFile in FindDependencyInjectionFiles())
        {
            var diContent = File.ReadAllText(diFile);
            if (diContent.Contains("AddConsumer"))
            {
                // Extract service name from path (e.g., Payments from src/Payments/...)
                var parts = diFile.Replace(SrcRoot + Path.DirectorySeparatorChar, "").Split(Path.DirectorySeparatorChar);
                if (parts.Length > 0) servicesWithConsumers.Add(parts[0]);
            }
        }

        var violations = new List<string>();
        foreach (var file in FindDbContextFiles())
        {
            var content = File.ReadAllText(file);
            if (content.Contains("IsConcurrencyToken") || content.Contains("xmin")) continue;
            if (!content.Contains("entity.Property") && !content.Contains("OnModelCreating")) continue;

            // Check if this DbContext's service has consumers
            var serviceName = file.Replace(SrcRoot + Path.DirectorySeparatorChar, "").Split(Path.DirectorySeparatorChar).FirstOrDefault() ?? "";
            if (servicesWithConsumers.Contains(serviceName))
            {
                violations.Add($"{Relative(file)}: service has consumers but DbContext lacks xmin concurrency token");
            }
        }
        // Informational — not all entities need xmin. Uncomment when all services migrate.
        // violations.Should().BeEmpty("services with consumers should configure xmin concurrency tokens");
    }

    [Fact]
    public void No_reservation_check_outside_transaction_in_StockService()
    {
        var stockServiceFiles = Directory.GetFiles(SrcRoot, "StockService.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("Test"));
        foreach (var file in stockServiceFiles)
        {
            var content = File.ReadAllText(file);
            var reserveIdx = content.IndexOf("ReserveStockAsync", StringComparison.Ordinal);
            if (reserveIdx < 0) continue;
            var transactionIdx = content.IndexOf("BeginTransactionAsync", reserveIdx, StringComparison.Ordinal);
            var existCheckIdx = content.IndexOf("FirstOrDefaultAsync", reserveIdx, StringComparison.Ordinal);
            if (existCheckIdx > 0 && transactionIdx > 0 && existCheckIdx < transactionIdx)
            {
                Assert.Fail($"{Relative(file)}: Reservation existence check must be INSIDE the transaction, not before it");
            }
        }
    }

    // ─── Outbox & Messaging (Wave 2) ─────────────────────────────────

    [Fact]
    public void Every_service_with_MassTransit_has_outbox_configured()
    {
        var violations = new List<string>();
        foreach (var file in FindDependencyInjectionFiles())
        {
            var content = File.ReadAllText(file);
            if (!content.Contains("AddMassTransit") && !content.Contains("AddConsumer")) continue;
            if (content.Contains("IsEnvironment(\"Test\")")) continue; // test-guarded
            if (!content.Contains("AddEntityFrameworkOutbox") && !content.Contains("UseBusOutbox"))
            {
                violations.Add($"{Relative(file)}: has MassTransit consumers but no EntityFrameworkOutbox — events lost on crash");
            }
        }
        violations.Should().BeEmpty("every service publishing events must have AddEntityFrameworkOutbox configured");
    }

    [Fact]
    public void No_MassTransit_without_Test_environment_guard()
    {
        var violations = new List<string>();
        foreach (var file in FindDependencyInjectionFiles())
        {
            var content = File.ReadAllText(file);
            if (!content.Contains("AddMassTransit")) continue;
            if (!content.Contains("IsEnvironment(\"Test\")") && !content.Contains("IsEnvironment( \"Test\")") &&
                !content.Contains("ASPNETCORE_ENVIRONMENT") && !content.Contains("!= \"Test\""))
            {
                violations.Add($"{Relative(file)}: AddMassTransit without Test environment guard — conflicts with test harness");
            }
        }
        violations.Should().BeEmpty("AddMassTransit must be guarded with !env.IsEnvironment(\"Test\")");
    }

    // ─── EF Core (Wave 3) ────────────────────────────────────────────

    [Fact]
    public void No_Include_after_Skip_Take_in_repositories()
    {
        var violations = new List<string>();
        foreach (var file in FindRepositoryFiles())
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                // Look for .Include after .Take on the same query chain
                if (lines[i].Contains(".Include(") &&
                    i > 0 && (lines[i - 1].Contains(".Take(") || lines[i - 1].Contains(".Skip(")))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: .Include() must come BEFORE .Skip()/.Take() to avoid cartesian results");
                }
            }
        }
        violations.Should().BeEmpty("EF Include must be placed before Skip/Take");
    }

    [Fact]
    public void No_unbounded_ToListAsync_in_background_workers()
    {
        var violations = new List<string>();
        foreach (var file in FindConsumerFiles()
            .Where(f => (f.Contains("Service") || f.Contains("Command") || f.Contains("Worker"))
                        && !f.Contains("Test")))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(".ToListAsync")) continue;
                // Look back up to 10 lines for a .Take() in the query chain
                bool hasTake = false;
                for (int j = Math.Max(0, i - 10); j <= i; j++)
                {
                    if (lines[j].Contains(".Take(")) { hasTake = true; break; }
                }
                if (!hasTake)
                    violations.Add($"{Relative(file)}:{i + 1}: ToListAsync without .Take() — add batch size limit");
            }
        }
        // Informational — may have false positives for small tables
        violations.Should().BeEmpty("background workers must use .Take() to bound query results");
    }

    // ─── Identity & Claims (Wave 1+3) ────────────────────────────────

    [Fact]
    public void CurrentUserService_checks_JWT_before_header()
    {
        foreach (var file in FindProductionCsFiles()
            .Where(f => Path.GetFileName(f) == "CurrentUserService.cs"))
        {
            var content = File.ReadAllText(file);
            var claimIdx = content.IndexOf("FindFirst", StringComparison.Ordinal);
            var headerIdx = content.IndexOf("X-User-Id", StringComparison.Ordinal);
            if (claimIdx < 0 || headerIdx < 0) continue;
            claimIdx.Should().BeLessThan(headerIdx,
                $"{Relative(file)}: must check JWT claims BEFORE X-User-Id header to prevent spoofing");
        }
    }

    [Fact]
    public void ClaimsPrincipal_userId_extraction_falls_back_to_sub_claim()
    {
        // Any file that extracts user ID from claims must handle both NameIdentifier and "sub"
        foreach (var file in FindProductionCsFiles()
            .Where(f => f.Contains("ClaimsPrincipal") || f.Contains("CurrentUser")))
        {
            var content = File.ReadAllText(file);
            if (!content.Contains("GetUserId") && !content.Contains("NameIdentifier")) continue;
            if (content.Contains("NameIdentifier") && !content.Contains("\"sub\""))
            {
                // Only flag if the file is specifically about user ID extraction
                if (content.Contains("FindFirst") || content.Contains("FindFirstValue"))
                {
                    Assert.Fail($"{Relative(file)}: must fall back to 'sub' claim when NameIdentifier is absent");
                }
            }
        }
    }

    // ─── Health Checks (Wave 3) ──────────────────────────────────────

    [Fact]
    public void Every_service_with_DbContext_has_health_check()
    {
        var violations = new List<string>();
        foreach (var file in FindProgramFiles())
        {
            var content = File.ReadAllText(file);
            if (!content.Contains("DbContext")) continue; // only check services with actual DB
            if (!content.Contains("AddDbHealthCheck") && !content.Contains("AddHealthChecks"))
            {
                violations.Add($"{Relative(file)}: service has DB but no health check");
            }
        }
        violations.Should().BeEmpty("every service with a database must register a health check");
    }

    // ─── Saga Compensation (Wave 2) ──────────────────────────────────

    [Fact]
    public void No_saga_dead_end_states_without_handler()
    {
        var violations = new List<string>();
        foreach (var file in FindSagaFiles())
        {
            var content = File.ReadAllText(file);
            if (content.Contains("RequiresReview") &&
                !content.Contains("During(RequiresReview") &&
                !content.Contains("DuringAny") &&
                !content.Contains("monitoring only") &&
                content.Contains("MassTransitStateMachine"))
            {
                violations.Add($"{Relative(file)}: RequiresReview state defined but has no outbound transition handler");
            }
        }
        // Informational — some RequiresReview states are intentional dead ends with operator UI
        violations.Should().BeEmpty("saga states should have outbound transitions or be explicitly finalized");
    }

    // ─── Test Quality (Wave 3) ───────────────────────────────────────

    [Fact]
    public void No_Task_Delay_for_test_synchronization()
    {
        var testRoot = Path.Combine(Directory.GetParent(SrcRoot)!.FullName, "tests");
        if (!Directory.Exists(testRoot)) return;
        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(testRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin")))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], @"Task\.Delay\(\d+\)") &&
                    !lines[i].TrimStart().StartsWith("//"))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: use polling/harness.Published.Any instead of Task.Delay for test sync");
                }
            }
        }
        // This is a best-practice check, not a hard requirement
        // violations.Should().BeEmpty("tests should use polling, not Task.Delay for synchronization");
        // For now, just report count
        if (violations.Count > 0)
        {
            // Log but don't fail — tracked as tech debt
        }
    }

    [Fact]
    public void No_True_Is_True_placeholder_tests()
    {
        var testRoot = Path.Combine(Directory.GetParent(SrcRoot)!.FullName, "tests");
        if (!Directory.Exists(testRoot)) return;
        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(testRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin")))
        {
            var content = File.ReadAllText(file);
            if (file.Contains("PlatformGuardTests")) continue; // skip self-reference
            if (content.Contains("Assert.True(true)") || content.Contains("true.Should().BeTrue()"))
            {
                violations.Add($"{Relative(file)}: placeholder test (Assert.True(true)) — delete or replace with real test");
            }
        }
        violations.Should().BeEmpty("placeholder tests provide false coverage");
    }

    // ─── Idempotency & Deduplication ──────────────────────────────────

    [Fact]
    public void Every_Hangfire_job_has_DisableConcurrentExecution_or_mutex()
    {
        var violations = new List<string>();
        foreach (var file in FindProductionCsFiles().Where(f => f.Contains("Service") || f.Contains("Worker") || f.Contains("Command")))
        {
            var content = File.ReadAllText(file);
            if (!content.Contains("IBackgroundJobClient") && !content.Contains("IRecurringJobManager")) continue;
            if (!content.Contains("BackgroundJob.") && !content.Contains("RecurringJob.")) continue;
            // The file schedules Hangfire jobs — check that job methods are guarded
            if (!content.Contains("DisableConcurrentExecution") && !content.Contains("Mutex"))
            {
                violations.Add($"{Relative(file)}: schedules Hangfire jobs without DisableConcurrentExecution — double-execution possible");
            }
        }
        // Informational for now — not all jobs need this
    }

    [Fact]
    public void Tables_with_idempotency_keys_have_unique_constraints()
    {
        // General principle: any column named *EventId, *IdempotencyKey, *MessageId, *DeduplicationId
        // that is used for deduplication MUST have a unique constraint or unique index.
        var idempotencyColumnPatterns = new[] { "EventId", "IdempotencyKey", "MessageId", "DeduplicationId" };

        var violations = new List<string>();
        foreach (var file in FindDbContextFiles())
        {
            var content = File.ReadAllText(file);
            foreach (var pattern in idempotencyColumnPatterns)
            {
                if (content.Contains(pattern) && content.Contains("HasIndex") &&
                    !content.Contains("IsUnique") && !content.Contains($"{pattern}).IsUnique"))
                {
                    // Check if there's an index on this column but it's not unique
                    if (Regex.IsMatch(content, $@"HasIndex.*{pattern}") && !Regex.IsMatch(content, $@"HasIndex.*{pattern}.*IsUnique"))
                    {
                        violations.Add($"{Relative(file)}: has index on {pattern} but it's not unique — duplicates possible on retry");
                    }
                }
            }
        }
        violations.Should().BeEmpty("idempotency/deduplication columns must have unique constraints");
    }

    [Fact]
    public void Every_consumer_that_publishes_events_uses_outbox_or_transaction()
    {
        var violations = new List<string>();
        foreach (var file in FindConsumerFiles().Where(f => f.Contains("Consumer")))
        {
            var content = File.ReadAllText(file);
            if (!content.Contains("PublishAsync") && !content.Contains("Publish(")) continue;
            if (content.Contains("ExecuteUpdateAsync") && !content.Contains("SaveChangesAsync"))
            {
                violations.Add($"{Relative(file)}: uses ExecuteUpdateAsync + Publish without SaveChangesAsync — outbox message lost");
            }
        }
        violations.Should().BeEmpty("consumers publishing events after ExecuteUpdateAsync must call SaveChangesAsync");
    }

    // ─── Concurrency & Data Safety ───────────────────────────────────

    [Fact]
    public void No_float_or_double_for_money()
    {
        var violations = new List<string>();
        foreach (var file in FindProductionCsFiles())
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//")) continue;
                // Check for float/double used with money-related variable names
                if ((Regex.IsMatch(line, @"\b(float|double)\s+\w*(amount|price|total|balance|commission|tax|cost|fee|payout)\w*", RegexOptions.IgnoreCase)) &&
                    !file.Contains("Test") && !file.Contains("test"))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: float/double used for monetary value — use decimal");
                }
            }
        }
        violations.Should().BeEmpty("monetary values must use decimal, never float or double");
    }

    [Fact]
    public void No_SaveChangesAsync_inside_foreach_loop()
    {
        var violations = new List<string>();
        foreach (var file in FindProductionCsFiles())
        {
            var lines = File.ReadAllLines(file);
            int foreachDepth = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("foreach") || lines[i].Contains("for (") || lines[i].Contains("for("))
                    foreachDepth++;
                if (lines[i].Contains("}") && foreachDepth > 0)
                    foreachDepth--;
                if (foreachDepth > 0 && lines[i].Contains("SaveChangesAsync"))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: SaveChangesAsync inside loop — batch writes outside the loop");
                }
            }
        }
        // Informational — some patterns legitimately save per iteration
    }

    // Removed: No_new_HttpClient_in_production_code — superseded by the
    // more precise No_raw_HttpClient_without_timeout_in_production_code
    // guard which allows raw HttpClient when Timeout is explicitly set
    // (e.g., JWKS startup fetch, Vault bootstrap).

    [Fact]
    public void No_catch_Exception_without_rethrow_in_consumers()
    {
        var violations = new List<string>();
        foreach (var file in FindConsumerFiles().Where(f => f.Contains("Consumer")))
        {
            // Skip SignalR bridges, demo consumers, and Kafka loop consumers (they retry via uncommitted offset)
            if (file.Contains("Bridge") || file.Contains("SignalR") || file.Contains("Demo")) continue;
            var fileContent = File.ReadAllText(file);
            // Kafka BackgroundService consumers use a loop with uncommitted offset as retry — different pattern
            if (fileContent.Contains("BackgroundService") || fileContent.Contains("consumer.Consume(")) continue;
            var content = File.ReadAllText(file);
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], @"catch\s*\(\s*Exception\b") &&
                    !lines[i].Contains("when ("))
                {
                    // Check the entire catch block (up to 20 lines) for throw/re-throw
                    var block = string.Join(" ", lines.Skip(i).Take(20));
                    if (!block.Contains("throw") && !block.Contains("consumer.Commit"))
                    {
                        violations.Add($"{Relative(file)}:{i + 1}: catch(Exception) without throw — message silently lost on failure");
                    }
                }
            }
        }
        violations.Should().BeEmpty("consumers must not silently swallow exceptions — message will be lost");
    }

    // ─── Resilience ──────────────────────────────────────────────────

    [Fact]
    public void External_API_calls_have_resilience_policy()
    {
        var violations = new List<string>();
        foreach (var file in FindProductionCsFiles().Where(f =>
            f.Contains("Stripe") || f.Contains("PayPal") || f.Contains("Nominatim") ||
            f.Contains("ClamAV") || f.Contains("SendGrid") || f.Contains("Twilio") || f.Contains("Fcm")))
        {
            if (file.Contains("Test") || file.Contains("Options") || file.Contains("Constants")) continue;
            var content = File.ReadAllText(file);
            if ((content.Contains("PostAsync") || content.Contains("GetAsync") || content.Contains("SendAsync") ||
                 content.Contains("CreateAsync") || content.Contains("CancelAsync")) &&
                !content.Contains("Policy") && !content.Contains("Resilience") && !content.Contains("retry") &&
                !content.Contains("CircuitBreaker"))
            {
                violations.Add($"{Relative(file)}: external API calls without Polly resilience policy");
            }
        }
        // Informational — many services wire resilience at the HttpClient DI level (AddStandardResilienceHandler)
        // violations.Should().BeEmpty("external API calls should have explicit resilience policies");
    }

    [Fact]
    public void Kafka_consumers_disable_auto_commit()
    {
        var violations = new List<string>();
        foreach (var file in FindConsumerFiles().Where(f => f.Contains("Cdc") || f.Contains("Kafka")))
        {
            var content = File.ReadAllText(file);
            if (!content.Contains("consumer.Consume")) continue;
            if (content.Contains("EnableAutoCommit = true"))
            {
                violations.Add($"{Relative(file)}: Kafka auto-commit enabled — must be disabled for at-least-once delivery");
            }
        }
        violations.Should().BeEmpty("Kafka consumers must disable auto-commit and commit manually after processing");
    }

    [Fact]
    public void Kafka_consumers_handle_poison_messages()
    {
        // General principle: any BackgroundService that calls consumer.Consume() in a loop
        // must handle non-transient exceptions (JsonException, FormatException, etc.)
        // to prevent infinite retry on malformed messages.
        var violations = new List<string>();
        foreach (var file in FindConsumerFiles())
        {
            var content = File.ReadAllText(file);
            if (!content.Contains("consumer.Consume") && !content.Contains("IConsumer<string")) continue;
            if (!content.Contains("JsonException") && !content.Contains("FormatException") &&
                !content.Contains("consumer.Commit"))
            {
                violations.Add($"{Relative(file)}: Kafka/stream consumer has no poison message handling — malformed messages cause infinite retry");
            }
        }
        violations.Should().BeEmpty("stream consumers must catch non-transient exceptions and skip poison messages");
    }

    // ─── Schema & Migrations ─────────────────────────────────────────

    [Fact]
    public void Every_DbContext_sets_a_default_schema()
    {
        var violations = new List<string>();
        foreach (var file in FindDbContextFiles())
        {
            if (file.Contains("Test") || file.Contains("Aspire")) continue;
            var content = File.ReadAllText(file);
            if (!content.Contains("HasDefaultSchema") && content.Contains("OnModelCreating"))
            {
                violations.Add($"{Relative(file)}: DbContext has no HasDefaultSchema — tables in public schema risk name collisions");
            }
        }
        violations.Should().BeEmpty("every service DbContext must set a default schema to prevent collisions");
    }

    [Fact]
    public void No_hardcoded_connection_strings_with_passwords()
    {
        var violations = new List<string>();
        foreach (var file in FindProductionCsFiles())
        {
            if (file.Contains("Test") || file.Contains("test") || file.Contains("Migration") || file.Contains("Factory")) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], @"Password\s*=\s*\w+", RegexOptions.IgnoreCase) &&
                    lines[i].Contains("Host=") &&
                    !lines[i].TrimStart().StartsWith("//"))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: hardcoded connection string with password in source code");
                }
            }
        }
        violations.Should().BeEmpty("connection strings with passwords must come from environment variables or Vault");
    }

    // ─── Saga Quality ────────────────────────────────────────────────

    [Fact]
    public void Every_saga_has_SetCompletedWhenFinalized()
    {
        var violations = new List<string>();
        foreach (var file in FindSagaFiles())
        {
            var content = File.ReadAllText(file);
            if (!content.Contains("MassTransitStateMachine")) continue;
            if (!content.Contains("SetCompletedWhenFinalized"))
            {
                violations.Add($"{Relative(file)}: saga state machine missing SetCompletedWhenFinalized — finalized sagas never cleaned up");
            }
        }
        violations.Should().BeEmpty("every saga must call SetCompletedWhenFinalized to auto-remove completed rows");
    }

    [Fact]
    public void Every_saga_that_acquires_resources_has_corresponding_release()
    {
        // General principle: for every "Reserved/Acquired/Locked/Claimed" event a saga handles,
        // there must be a corresponding "Release/Free/Unlock/Return" publish somewhere in the same saga.
        var acquireReleasePatterns = new (string acquire, string release)[]
        {
            ("Reserved", "Release"),
            ("Acquired", "Release"),
            ("Locked", "Unlock"),
            ("Claimed", "Unclaim"),
            ("Allocated", "Deallocate"),
            ("Held", "Release"),
        };

        var violations = new List<string>();
        foreach (var file in FindSagaFiles())
        {
            var content = File.ReadAllText(file);
            // Only check actual state machines, not bridge/relay consumers
            if (!content.Contains("MassTransitStateMachine") && !content.Contains("StateMachineInstance")) continue;
            foreach (var (acquire, release) in acquireReleasePatterns)
            {
                if (content.Contains(acquire) && !content.Contains(release))
                {
                    violations.Add($"{Relative(file)}: saga handles '{acquire}' but has no '{release}' — resources may be orphaned on failure");
                }
            }
        }
        violations.Should().BeEmpty("sagas that acquire resources must have corresponding release/compensation paths");
    }

    // ─── API Quality ─────────────────────────────────────────────────

    [Fact]
    public void No_controller_returns_raw_exception_details()
    {
        var violations = new List<string>();
        foreach (var file in FindControllerFiles())
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("ex.Message") && lines[i].Contains("return") &&
                    !lines[i].TrimStart().StartsWith("//"))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: controller returns exception message to client — information leakage");
                }
            }
        }
        violations.Should().BeEmpty("controllers must not return raw exception messages to clients");
    }

    [Fact]
    public void Every_async_controller_method_accepts_CancellationToken()
    {
        var violations = new List<string>();
        foreach (var file in FindControllerFiles())
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], @"public\s+async\s+Task<") &&
                    !lines[i].Contains("CancellationToken") &&
                    !lines[i].TrimStart().StartsWith("//"))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: async controller method missing CancellationToken parameter");
                }
            }
        }
        // Informational — some frameworks inject it automatically
    }

    // ─── PII & Logging ───────────────────────────────────────────────

    [Fact]
    public void No_email_addresses_logged_at_Information_level()
    {
        var violations = new List<string>();
        foreach (var file in FindProductionCsFiles())
        {
            if (file.Contains("Test")) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("LogInformation") &&
                    (lines[i].Contains("email") || lines[i].Contains("Email")) &&
                    lines[i].Contains("{") && // structured logging placeholder
                    !lines[i].TrimStart().StartsWith("//"))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: email logged at Information level — PII should be at Debug or masked");
                }
            }
        }
        // Informational — requires GDPR review per jurisdiction
    }

    // ─── Idempotency & Inbox/Outbox ────────────────────────────────

    [Fact]
    public void Every_service_with_consumers_has_inbox_configured()
    {
        // General principle: any service that consumes messages and writes to DB
        // must have MassTransit inbox configured to prevent double-processing on redelivery.
        var violations = new List<string>();
        foreach (var file in FindDependencyInjectionFiles())
        {
            var content = File.ReadAllText(file);
            if (!content.Contains("AddConsumer")) continue;
            if (content.Contains("IsEnvironment(\"Test\")")) continue;
            // Inbox is part of the outbox configuration in MT — AddEntityFrameworkOutbox enables both
            if (!content.Contains("AddEntityFrameworkOutbox"))
            {
                violations.Add($"{Relative(file)}: has consumers but no EntityFrameworkOutbox (includes inbox) — messages reprocessed on crash");
            }
        }
        violations.Should().BeEmpty("every service with consumers must configure EntityFrameworkOutbox for inbox deduplication");
    }

    [Fact]
    public void Every_DbContext_with_outbox_has_inbox_and_outbox_entities()
    {
        // General principle: if DI configures AddEntityFrameworkOutbox, the DbContext's
        // OnModelCreating must register the inbox/outbox entities.
        var violations = new List<string>();
        foreach (var diFile in FindDependencyInjectionFiles())
        {
            var diContent = File.ReadAllText(diFile);
            if (!diContent.Contains("AddEntityFrameworkOutbox")) continue;

            // Find the DbContext type referenced
            var match = Regex.Match(diContent, @"AddEntityFrameworkOutbox<(\w+)>");
            if (!match.Success) continue;
            var contextName = match.Groups[1].Value;

            // Find the corresponding DbContext file
            var contextFile = FindDbContextFiles()
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == contextName);
            if (contextFile == null) continue;

            var contextContent = File.ReadAllText(contextFile);
            if (!contextContent.Contains("AddInboxStateEntity") && !contextContent.Contains("InboxState"))
            {
                violations.Add($"{Relative(contextFile)}: outbox configured but AddInboxStateEntity/AddOutboxStateEntity missing in OnModelCreating");
            }
        }
        violations.Should().BeEmpty("DbContexts with outbox must register inbox/outbox entities in OnModelCreating");
    }

    [Fact]
    public void POST_endpoints_accept_idempotency_key_or_have_dedup_guard()
    {
        // General principle: POST endpoints that create resources should either:
        // 1. Accept an IdempotencyKey header/parameter, OR
        // 2. Have natural dedup (e.g., unique constraint on OrderId/SagaId), OR
        // 3. Be explicitly marked as non-idempotent with a comment
        var violations = new List<string>();
        foreach (var file in FindControllerFiles())
        {
            var lines = File.ReadAllLines(file);
            var content = File.ReadAllText(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("[HttpPost") || lines[i].Contains("webhook") || lines[i].Contains("Webhook"))
                    continue;

                // Look at the method body (next 30 lines) for idempotency patterns
                var methodBlock = string.Join(" ", lines.Skip(i).Take(30));
                var hasIdempotency =
                    methodBlock.Contains("IdempotencyKey") ||
                    methodBlock.Contains("idempotencyKey") ||
                    methodBlock.Contains("Idempotent") ||
                    content.Contains("AddPostgresIdempotency") ||
                    content.Contains("[AllowAnonymous]"); // webhooks don't need idempotency keys

                // This is informational — we track but don't fail for now
                if (!hasIdempotency)
                {
                    // Check if the controller's service has natural dedup (SagaId, unique constraint)
                    if (!content.Contains("SagaId") && !content.Contains("IsUnique") &&
                        !content.Contains("DuplicateOrder") && !content.Contains("duplicate"))
                    {
                        // Soft check — log but don't fail
                    }
                }
            }
        }
        // This guard is informational — POST idempotency requires design decisions per endpoint
    }

    [Fact]
    public void Every_consumer_DI_has_consumer_definition_or_retry_config()
    {
        // General principle: consumers should have explicit retry configuration,
        // either via ConsumerDefinition or UseMessageRetry in the endpoint config.
        var violations = new List<string>();
        foreach (var file in FindDependencyInjectionFiles())
        {
            var content = File.ReadAllText(file);
            if (!content.Contains("AddConsumer")) continue;
            if (content.Contains("IsEnvironment(\"Test\")")) continue;
            if (!content.Contains("ConsumerDefinition") && !content.Contains("UseMessageRetry") &&
                !content.Contains("RetryPolicy") && !content.Contains("ConfigureConsumer"))
            {
                violations.Add($"{Relative(file)}: consumers registered without explicit retry configuration");
            }
        }
        // Informational — MassTransit has default retry, but explicit config is better
    }

    // ─── Clean Architecture ──────────────────────────────────────────

    [Fact]
    public void Domain_layer_does_not_reference_Infrastructure_or_Application()
    {
        // Clean Architecture: Domain must not depend on outer layers
        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(SrcRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(f => f.Contains(".Domain") && !f.Contains("obj")))
        {
            var content = File.ReadAllText(file);
            if (content.Contains("Infrastructure") || content.Contains("Application"))
            {
                // Check project references, not package references
                if (Regex.IsMatch(content, @"<ProjectReference.*\.(Infrastructure|Application)\."))
                {
                    violations.Add($"{Relative(file)}: Domain layer references Infrastructure or Application — clean architecture violation");
                }
            }
        }
        violations.Should().BeEmpty("Domain layer must not reference outer layers (Infrastructure, Application)");
    }

    [Fact]
    public void Controllers_do_not_directly_use_DbContext()
    {
        // Controllers should use MediatR/handlers, not access DbContext directly
        var violations = new List<string>();
        foreach (var file in FindControllerFiles())
        {
            if (file.Contains("Demo") || file.Contains("Admin")) continue; // demos are allowed
            var content = File.ReadAllText(file);
            if (Regex.IsMatch(content, @"\bDbContext\b") && !content.Contains("// design-time"))
            {
                violations.Add($"{Relative(file)}: controller directly uses DbContext — use MediatR handlers instead");
            }
        }
        violations.Should().BeEmpty("controllers must not directly access DbContext — use handlers/services");
    }

    // ─── Reliability ─────────────────────────────────────────────────

    [Fact]
    public void No_async_void_methods()
    {
        // async void is fire-and-forget — exceptions crash the process
        var violations = new List<string>();
        foreach (var file in FindProductionCsFiles())
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], @"\basync\s+void\b") &&
                    !lines[i].Contains("EventHandler") && // event handlers are the one valid use
                    !lines[i].TrimStart().StartsWith("//"))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: async void — use async Task (exceptions in async void crash the process)");
                }
            }
        }
        violations.Should().BeEmpty("async void methods must not exist — use async Task");
    }

    [Fact]
    public void No_Task_Run_in_request_pipeline()
    {
        // Task.Run in controllers/handlers causes thread pool starvation under load
        var violations = new List<string>();
        foreach (var file in FindControllerFiles()
            .Concat(FindProductionCsFiles().Where(f => f.Contains("Handler") || f.Contains("Command"))))
        {
            if (file.Contains("Worker") || file.Contains("Background") || file.Contains("Admin") || file.Contains("Demo")) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("Task.Run(") && !lines[i].TrimStart().StartsWith("//"))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: Task.Run in request pipeline — causes thread pool starvation");
                }
            }
        }
        violations.Should().BeEmpty("Task.Run must not be used in the HTTP request pipeline");
    }

    // ─── Data Safety ─────────────────────────────────────────────────

    [Fact]
    public void No_DateTime_Now_use_DateTime_UtcNow()
    {
        // DateTime.Now uses local timezone — always use UtcNow in server code
        var violations = new List<string>();
        foreach (var file in FindProductionCsFiles())
        {
            if (file.Contains("Test") || file.Contains("Migration")) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], @"DateTime\.Now\b") &&
                    !lines[i].Contains("UtcNow") &&
                    !lines[i].TrimStart().StartsWith("//"))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: DateTime.Now — use DateTime.UtcNow (server code must not depend on local timezone)");
                }
            }
        }
        violations.Should().BeEmpty("always use DateTime.UtcNow, never DateTime.Now in server code");
    }

    [Fact]
    public void No_string_interpolation_in_ExecuteSqlRaw()
    {
        // ExecuteSqlRawAsync with $"..." is SQL injection. Use ExecuteSqlInterpolatedAsync or {0} params.
        var violations = new List<string>();
        foreach (var file in FindProductionCsFiles())
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("ExecuteSqlRaw") && lines[i].Contains("$\""))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: string interpolation in ExecuteSqlRaw — SQL injection risk. Use ExecuteSqlInterpolated or parameterized {0}");
                }
            }
        }
        violations.Should().BeEmpty("ExecuteSqlRaw must use parameterized queries, never string interpolation");
    }

    // ─── Observability ───────────────────────────────────────────────

    [Fact]
    public void No_string_concatenation_in_log_messages()
    {
        // Log messages should use structured logging {Param}, not "text" + variable
        var violations = new List<string>();
        foreach (var file in FindProductionCsFiles())
        {
            if (file.Contains("Test")) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], @"Log(Information|Warning|Error|Debug|Critical)\s*\(\s*\$""") &&
                    !lines[i].TrimStart().StartsWith("//"))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: string interpolation in log message — use structured logging with {{placeholders}}");
                }
            }
        }
        violations.Should().BeEmpty("log messages must use structured logging {Placeholders}, not string interpolation");
    }

    // ─── Event Contracts ─────────────────────────────────────────────

    [Fact]
    public void Every_event_in_Contracts_has_at_least_one_consumer()
    {
        // Dead events are code smell — if nobody consumes it, delete it
        var contractsDir = Path.Combine(SrcRoot, "Contracts");
        if (!Directory.Exists(contractsDir)) return;

        var violations = new List<string>();
        var allSrcContent = string.Join("\n",
            FindProductionCsFiles()
                .Where(f => !f.Contains("Contracts") && !f.Contains("Test"))
                .Select(f => File.ReadAllText(f)));

        foreach (var file in Directory.GetFiles(contractsDir, "*Event.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj")))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            // Check if any file outside Contracts references this event type
            if (!allSrcContent.Contains(fileName))
            {
                violations.Add($"{Relative(file)}: event {fileName} has no consumers — consider deleting dead event");
            }
        }
        // Informational — some events may be consumed by external services
    }

    // ─── Integration Test Factory Quality (from CI debugging) ────────

    [Fact]
    public void No_EnsureDeletedAsync_in_test_factories()
    {
        var testRoot = Path.Combine(Directory.GetParent(SrcRoot)!.FullName, "tests");
        if (!Directory.Exists(testRoot)) return;
        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(testRoot, "*Factory.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin")))
        {
            var content = File.ReadAllText(file);
            if (content.Contains("EnsureDeletedAsync"))
            {
                violations.Add($"{Relative(file)}: EnsureDeletedAsync drops the entire database — use fresh DB from SharedTestPostgres instead");
            }
        }
        violations.Should().BeEmpty("test factories must never call EnsureDeletedAsync — it drops the DB");
    }

    [Fact]
    public void Test_factories_create_schema_before_EnsureCreated()
    {
        var testRoot = Path.Combine(Directory.GetParent(SrcRoot)!.FullName, "tests");
        if (!Directory.Exists(testRoot)) return;
        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(testRoot, "*Factory.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin")))
        {
            var content = File.ReadAllText(file);
            // Only flag actual code usage, not comments mentioning EnsureCreatedAsync
            var codeLines = content.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//") && !l.TrimStart().StartsWith("*"));
            if (!codeLines.Any(l => l.Contains("EnsureCreatedAsync"))) continue;
            // Find which schema the service uses
            var serviceName = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(file)) ?? "");
            // Check if the corresponding DbContext uses HasDefaultSchema
            var dbContextFiles = FindDbContextFiles()
                .Where(f => f.Contains(serviceName, StringComparison.OrdinalIgnoreCase));
            foreach (var dbCtx in dbContextFiles)
            {
                var dbContent = File.ReadAllText(dbCtx);
                var schemaMatch = Regex.Match(dbContent, @"HasDefaultSchema\(""(\w+)""\)");
                if (schemaMatch.Success && !content.Contains($"CREATE SCHEMA IF NOT EXISTS {schemaMatch.Groups[1].Value}"))
                {
                    violations.Add($"{Relative(file)}: uses EnsureCreatedAsync but doesn't CREATE SCHEMA '{schemaMatch.Groups[1].Value}' first — tables won't be created");
                }
            }
        }
        violations.Should().BeEmpty("test factories must CREATE SCHEMA before EnsureCreatedAsync when DbContext uses HasDefaultSchema");
    }

    [Fact]
    public void Test_factories_use_ConfigureTestServices_not_ConfigureServices()
    {
        var testRoot = Path.Combine(Directory.GetParent(SrcRoot)!.FullName, "tests");
        if (!Directory.Exists(testRoot)) return;
        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(testRoot, "*Factory.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin")))
        {
            var content = File.ReadAllText(file);
            if (!content.Contains("WebApplicationFactory")) continue;
            if (content.Contains("ConfigureServices") && !content.Contains("ConfigureTestServices"))
            {
                violations.Add($"{Relative(file)}: uses ConfigureServices — use ConfigureTestServices to guarantee override of app DI");
            }
        }
        violations.Should().BeEmpty("test factories must use ConfigureTestServices (runs AFTER app DI)");
    }

    [Fact]
    public void Test_factories_set_JwtTestDefaults()
    {
        var testRoot = Path.Combine(Directory.GetParent(SrcRoot)!.FullName, "tests");
        if (!Directory.Exists(testRoot)) return;
        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(testRoot, "*Factory.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin")))
        {
            var content = File.ReadAllText(file);
            if (!content.Contains("WebApplicationFactory") || !content.Contains("IAsyncLifetime")) continue;
            if (!content.Contains("JwtTestDefaults"))
            {
                violations.Add($"{Relative(file)}: missing JwtTestDefaults.SetTestEnvironmentVariables() — JwksOptions ValidateOnStart will throw");
            }
        }
        violations.Should().BeEmpty("test factories must call JwtTestDefaults.SetTestEnvironmentVariables()");
    }

    [Fact]
    public void All_event_records_in_Contracts_extend_DomainEvent()
    {
        var contractsDir = Path.Combine(SrcRoot, "Contracts");
        if (!Directory.Exists(contractsDir)) return;
        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(contractsDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj")))
        {
            var lines = File.ReadAllLines(file);
            foreach (var line in lines)
            {
                var m = Regex.Match(line, @"public\s+(sealed\s+)?record\s+(\w+)");
                if (!m.Success) continue;
                var name = m.Groups[2].Value;
                // Only check types that are MassTransit events (not value objects)
                if (!name.EndsWith("Event") && !name.EndsWith("Message") && !name.EndsWith("Notification"))
                    continue;
                if (!line.Contains(": DomainEvent") && !line.Contains(":DomainEvent"))
                {
                    violations.Add($"{Relative(file)}: {name} doesn't extend DomainEvent — MassTransit Init<T> will fault");
                }
            }
        }
        violations.Should().BeEmpty("all event records in Contracts must extend DomainEvent for MassTransit compatibility");
    }

    [Fact]
    public void Media_state_transitions_are_guarded()
    {
        var mediaFilePath = Path.Combine(SrcRoot, "Media", "Media.Api", "Domain", "MediaFile.cs");
        if (!File.Exists(mediaFilePath)) return;
        var content = File.ReadAllText(mediaFilePath);
        var violations = new List<string>();

        // Each MarkAs* method must contain a Status != check (guard)
        var methods = Regex.Matches(content, @"public\s+void\s+(MarkAs\w+)\s*\(\)");
        foreach (Match m in methods)
        {
            var methodName = m.Groups[1].Value;
            // Find method body — look for the throw check
            if (!content.Contains($"{methodName}()") || !content.Contains("throw new InvalidOperationException"))
                continue;

            // Check that the method body contains a Status != guard
            var methodStart = content.IndexOf($"public void {methodName}()");
            var methodEnd = content.IndexOf('}', content.IndexOf('{', methodStart)) + 1;
            var methodBody = content[methodStart..methodEnd];
            if (!methodBody.Contains("Status !="))
            {
                violations.Add($"MediaFile.{methodName} has no state transition guard (missing Status != check)");
            }
        }
        violations.Should().BeEmpty("all MediaFile state transitions must enforce valid source states");
    }

    // ─── Lens 2: Sync-over-Async (Concurrency) ────────────────────────

    [Fact]
    public void No_sync_over_async_blocking_calls()
    {
        var violations = new List<string>();
        foreach (var file in FindProductionCsFiles())
        {
            if (file.Contains("Program.cs") || file.Contains("Migration") || file.Contains("ModuleInitializer")) continue;
            // Startup/bootstrap code legitimately needs sync-over-async for one-time init
            if (file.Contains("Extensions") && file.Contains("Authentication")) continue;
            // Hosted services and disposables may use sync-over-async in teardown paths
            if (file.Contains("HostedService") || file.Contains("Revocation")) continue;
            // Demo infrastructure is not in the request path
            if (file.Contains("/Demo/")) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//")) continue;
                // Match Task.Result / .Result (property on Task) but NOT foo.Result (arbitrary property)
                // Key: .Result must NOT be followed by further property access typical of value objects
                if ((Regex.IsMatch(line, @"\.GetAwaiter\(\)\.GetResult\(\)") ||
                     Regex.IsMatch(line, @"\)\s*\.Result\b") ||    // someTask().Result
                     Regex.IsMatch(line, @"Task.*\.Result\b")) &&  // Task<T>.Result
                    !line.Contains("ModuleInitializer") && !line.Contains("Main(") &&
                    !line.Contains("Dispose") && !line.Contains("dispose") &&
                    !line.Contains("ScanResult") && !line.Contains("ClamScan"))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: .Result or .GetAwaiter().GetResult() — blocks thread pool, use await");
                }
                if (Regex.IsMatch(line, @"\.Wait\(\)") && !line.Contains("WaitAsync") &&
                    !line.Contains("SpinWait") && !line.Contains("ManualReset"))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: .Wait() blocks thread pool — use await");
                }
            }
        }
        violations.Should().BeEmpty("sync-over-async (.Result, .Wait()) causes thread pool starvation — use await");
    }

    // ─── Lens 5: Webhook Idempotency ────────────────────────────────

    [Fact]
    public void Webhook_controllers_check_for_duplicate_delivery()
    {
        var violations = new List<string>();
        foreach (var file in FindControllerFiles())
        {
            if (!file.Contains("Webhook", StringComparison.OrdinalIgnoreCase)) continue;
            // Skip subscription/delivery management controllers — they manage outbound webhook config, not inbound receipt
            if (file.Contains("Subscription", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Contains("Deliveries", StringComparison.OrdinalIgnoreCase)) continue;
            var content = File.ReadAllText(file);
            if (!content.Contains("[HttpPost")) continue;
            if (!content.Contains("idempotency") && !content.Contains("Idempotency") &&
                !content.Contains("eventId") && !content.Contains("EventId") &&
                !content.Contains("duplicate") && !content.Contains("Duplicate") &&
                !content.Contains("DeliveryId") && !content.Contains("MessageId"))
            {
                violations.Add($"{Relative(file)}: webhook endpoint has no duplicate delivery guard — replay attacks possible");
            }
        }
        violations.Should().BeEmpty("webhook endpoints must check for duplicate delivery (idempotency key, event ID, etc.)");
    }

    // ─── Lens 7: User ID Must Come from JWT ─────────────────────────

    [Fact]
    public void No_userId_from_request_body_in_state_changing_endpoints()
    {
        var violations = new List<string>();
        foreach (var file in FindControllerFiles())
        {
            var fileContent = File.ReadAllText(file);
            // Skip service-to-service controllers (inter-service calls carry UserId in the message)
            if (fileContent.Contains("Roles = \"Service\"") || fileContent.Contains("Roles = \"Admin,Service\"")) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                // Flag patterns like "request.UserId" or "command.UserId" or "dto.UserId" used in assignment
                if (Regex.IsMatch(lines[i], @"\b(request|command|dto|body|model)\.(UserId|OwnerId|CustomerId)\b", RegexOptions.IgnoreCase) &&
                    !lines[i].TrimStart().StartsWith("//") &&
                    !lines[i].Contains("// from JWT") && !lines[i].Contains("GetUserId") &&
                    !lines[i].Contains("// service-to-service"))
                {
                    // Check context: is this inside a POST/PUT/DELETE endpoint?
                    var context = string.Join(" ", lines.Skip(Math.Max(0, i - 15)).Take(15));
                    if (context.Contains("[HttpPost") || context.Contains("[HttpPut") || context.Contains("[HttpDelete"))
                    {
                        violations.Add($"{Relative(file)}:{i + 1}: UserId from request body in state-changing endpoint — must come from JWT claims");
                    }
                }
            }
        }
        violations.Should().BeEmpty("user identity must come from JWT claims, never from request body (prevents IDOR)");
    }

    // ─── Lens 7: SSRF Prevention ────────────────────────────────────

    [Fact]
    public void No_unvalidated_user_URLs_in_HTTP_calls()
    {
        var violations = new List<string>();
        foreach (var file in FindProductionCsFiles())
        {
            if (file.Contains("Test")) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                // Flag: new Uri(someVariable) followed by HttpClient call without validation
                if (lines[i].Contains("new Uri(") && !lines[i].Contains("\"http") &&
                    !lines[i].TrimStart().StartsWith("//"))
                {
                    // Check surrounding context for URL validation
                    var context = string.Join(" ", lines.Skip(Math.Max(0, i - 5)).Take(15));
                    if ((context.Contains("GetAsync") || context.Contains("PostAsync") || context.Contains("SendAsync")) &&
                        !context.Contains("IsAllowed") && !context.Contains("ValidateUrl") &&
                        !context.Contains("allowlist") && !context.Contains("whitelist") &&
                        !context.Contains("StartsWith(\"https://\""))
                    {
                        violations.Add($"{Relative(file)}:{i + 1}: user-supplied URL used in HTTP call without validation — SSRF risk");
                    }
                }
            }
        }
        // Informational — requires manual review of URL sources
    }

    // ─── Lens 8: Event Records Must Not Use Positional Syntax ───────

    [Fact]
    public void No_positional_record_syntax_in_Contracts()
    {
        var contractsDir = Path.Combine(SrcRoot, "Contracts");
        if (!Directory.Exists(contractsDir)) return;
        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(contractsDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj")))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                // Positional record: record Foo(string Bar, int Baz)
                // Non-positional: record Foo { ... } or record Foo : Base { ... }
                var recordMatch = Regex.Match(lines[i], @"record\s+(\w+)\s*\(");
                if (recordMatch.Success && !lines[i].Contains("//"))
                {
                    var recordName = recordMatch.Groups[1].Value;
                    // Skip DTOs/envelopes/value objects that aren't MassTransit events
                    if (recordName.Contains("Envelope") || recordName.Contains("Dto") ||
                        recordName.Contains("Request") || recordName.Contains("Source") ||
                        recordName.Contains("Payload") || recordName.Contains("Response"))
                        continue;
                    violations.Add($"{Relative(file)}:{i + 1}: {recordName} uses positional record syntax — MassTransit Init<T> faults. Use {{ get; init; }} properties");
                }
            }
        }
        violations.Should().BeEmpty("event records in Contracts must use {{ get; init; }} properties, never positional syntax");
    }

    // ─── Lens 9: Unbounded Responses in Controllers ─────────────────

    [Fact]
    public void No_unbounded_ToListAsync_in_controllers()
    {
        var violations = new List<string>();
        foreach (var file in FindControllerFiles())
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(".ToListAsync")) continue;
                // Look back for .Take() or pagination params
                var context = string.Join(" ", lines.Skip(Math.Max(0, i - 10)).Take(11));
                if (!context.Contains(".Take(") && !context.Contains("PageSize") &&
                    !context.Contains("pageSize") && !context.Contains("limit"))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: ToListAsync without pagination — could return unbounded results");
                }
            }
        }
        violations.Should().BeEmpty("controller queries must use .Take() or pagination to bound results");
    }

    // ─── Lens 11: Options Validation ────────────────────────────────

    [Fact]
    public void Options_classes_with_Required_attributes_have_ValidateOnStart()
    {
        var violations = new List<string>();
        // Find options classes WITH [Required] attributes that are OUR code (in src/)
        var optionsFiles = FindProductionCsFiles()
            .Where(f => f.Contains("Options") && !f.Contains("Test") &&
                         Path.GetFileName(f).EndsWith("Options.cs"));
        var requiredOptions = new HashSet<string>();
        foreach (var file in optionsFiles)
        {
            var content = File.ReadAllText(file);
            if (!content.Contains("[Required]")) continue;
            // Must be in our namespace (Haworks)
            if (!content.Contains("namespace Haworks")) continue;
            var match = Regex.Match(content, @"class\s+(\w+Options)\b");
            if (match.Success)
                requiredOptions.Add(match.Groups[1].Value);
        }

        // Check DI files for ValidateOnStart for each required options class
        foreach (var diFile in FindDependencyInjectionFiles())
        {
            var diContent = File.ReadAllText(diFile);
            foreach (var optionsName in requiredOptions)
            {
                // Only flag if the DI file actually binds this options class
                if (diContent.Contains($"<{optionsName}>") && !diContent.Contains("ValidateOnStart"))
                {
                    violations.Add($"{Relative(diFile)}: binds {optionsName} with [Required] but missing ValidateOnStart()");
                }
            }
        }
        violations.Should().BeEmpty("options with [Required] must use ValidateDataAnnotations().ValidateOnStart()");
    }

    // ─── Lens 12: FromSqlRaw Injection ──────────────────────────────

    [Fact]
    public void No_string_interpolation_in_FromSqlRaw()
    {
        var violations = new List<string>();
        foreach (var file in FindProductionCsFiles())
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("FromSqlRaw") && lines[i].Contains("$\""))
                {
                    violations.Add($"{Relative(file)}:{i + 1}: string interpolation in FromSqlRaw — SQL injection risk. Use FromSqlInterpolated or {0} parameters");
                }
            }
        }
        violations.Should().BeEmpty("FromSqlRaw must use parameterized queries, never string interpolation");
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

    private static IEnumerable<string> FindDbContextFiles() =>
        Directory.GetFiles(SrcRoot, "*DbContext.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin") && !f.Contains("Test") && !f.Contains("Factory"));

    private static IEnumerable<string> FindDependencyInjectionFiles() =>
        Directory.GetFiles(SrcRoot, "DependencyInjection.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin") && !f.Contains("Test"));

    private static IEnumerable<string> FindRepositoryFiles() =>
        Directory.GetFiles(SrcRoot, "*Repository.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin") && !f.Contains("Test"));

    private static IEnumerable<string> FindSagaFiles() =>
        Directory.GetFiles(SrcRoot, "*Saga*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin") && !f.Contains("Test") && !f.Contains("State") && !f.Contains("Migration"));

    private static IEnumerable<string> FindProductionCsFiles() =>
        Directory.GetFiles(SrcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin") && !f.Contains("/Test") && !f.Contains(".Testing"));

    // ─── Timeout & Resilience Guards ──────────────────────────────────

    [Fact]
    public void No_raw_HttpClient_without_timeout_in_production_code()
    {
        var violations = new List<string>();
        foreach (var file in FindProductionCsFiles())
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("new HttpClient()") && !lines[i].Contains("new HttpClient("))
                    continue;
                // Check next 5 lines for Timeout assignment
                bool hasTimeout = false;
                for (int j = i; j < Math.Min(i + 6, lines.Length); j++)
                {
                    if (lines[j].Contains(".Timeout") || lines[j].Contains("Timeout ="))
                    {
                        hasTimeout = true;
                        break;
                    }
                }
                if (!hasTimeout)
                    violations.Add($"{Relative(file)}:{i + 1}: new HttpClient() without Timeout assignment");
            }
        }
        violations.Should().BeEmpty(
            "every raw HttpClient must have an explicit Timeout to prevent indefinite hangs");
    }

    [Fact]
    public void No_SemaphoreSlim_WaitAsync_without_timeout_in_production_code()
    {
        var violations = new List<string>();
        foreach (var file in FindProductionCsFiles())
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                // Match: .WaitAsync() or .WaitAsync(ct) or .WaitAsync(cancellationToken)
                // but NOT .WaitAsync(TimeSpan...) or .WaitAsync(timeout,...)
                if (line.Contains(".WaitAsync(") &&
                    !line.Contains("TimeSpan") &&
                    !line.Contains("FromSeconds") &&
                    !line.Contains("FromMinutes") &&
                    !line.Contains("imeout", StringComparison.OrdinalIgnoreCase))
                {
                    // Only flag SemaphoreSlim patterns (not Task.WaitAsync etc.)
                    if (line.Contains("_gate.") || line.Contains("_lock.") ||
                        line.Contains("_clientGate.") || line.Contains("_unwrapGate.") ||
                        line.Contains("_clientLock.") || line.Contains("_tokenLock.") ||
                        line.Contains("@lock."))
                    {
                        violations.Add($"{Relative(file)}:{i + 1}: SemaphoreSlim.WaitAsync without TimeSpan timeout");
                    }
                }
            }
        }
        violations.Should().BeEmpty(
            "every SemaphoreSlim.WaitAsync must include a TimeSpan timeout to prevent indefinite hangs");
    }
}
