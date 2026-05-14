using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace Haworks.Payouts.Architecture;

public sealed class BoundaryTests
{
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "Haworks.Identity", "Haworks.Orders", "Haworks.Payments", "Haworks.Content",
        "Haworks.CheckoutOrchestrator", "Haworks.BffWeb", "Haworks.Catalog", "Haworks.Search",
    ];

    private static readonly Assembly[] PayoutsAssemblies =
    [
        typeof(Haworks.Payouts.Domain.Aggregates.Payout).Assembly,
        typeof(Haworks.Payouts.Application.Ledger.Services.LedgerService).Assembly,
        typeof(Haworks.Payouts.Infrastructure.Persistence.PayoutsDbContext).Assembly,
    ];

    [Fact]
    public void Payouts_assemblies_must_not_reference_sibling_services()
    {
        foreach (var assembly in PayoutsAssemblies)
        {
            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(ForbiddenNamespacePrefixes).GetResult();
            result.IsSuccessful.Should().BeTrue($"Assembly '{assembly.GetName().Name}' has a forbidden cross-service dependency.");
        }
    }

    [Fact]
    public void Payouts_Domain_must_not_reference_Application_or_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Haworks.Payouts.Domain.Aggregates.Payout).Assembly).ShouldNot().HaveDependencyOnAny("Haworks.Payouts.Application", "Haworks.Payouts.Infrastructure").GetResult();
        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void Payouts_Application_must_not_reference_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Haworks.Payouts.Application.Ledger.Services.LedgerService).Assembly).ShouldNot().HaveDependencyOn("Haworks.Payouts.Infrastructure").GetResult();
        result.IsSuccessful.Should().BeTrue();
    }
}
