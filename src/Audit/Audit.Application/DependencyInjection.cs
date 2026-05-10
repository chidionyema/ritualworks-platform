using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Audit.Application;

/// <summary>
/// Top-level orchestrator. Calls the four phase-specific extension
/// methods in <c>DependencyInjection.{Extractors,Capture,Queries,Export}.cs</c>
/// — each one is owned by exactly one L1 phase, so the four phases can
/// run in parallel without ever touching this file.
///
/// THIS FILE IS WRITTEN ONCE BY L0. L1.A/B/C/D do NOT modify it.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddAuditApplication(this IServiceCollection services) => services
        .AddAuditExtractors()
        .AddAuditCapture()
        .AddAuditQueries()
        .AddAuditExport();
}
