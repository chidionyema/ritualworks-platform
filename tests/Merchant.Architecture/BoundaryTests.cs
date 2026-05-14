using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace Haworks.Merchant.Architecture;

public sealed class BoundaryTests
{
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "Haworks.Identity", "Haworks.Orders", "Haworks.Payments", "Haworks.Content",
        "Haworks.CheckoutOrchestrator", "Haworks.BffWeb", "Haworks.Catalog", "Haworks.Search", "Haworks.Payouts", "Haworks.Scheduler", "Haworks.Privacy"
    ];

    private static readonly Assembly[] MerchantAssemblies =
    [
        typeof(Haworks.Merchant.Domain.Aggregates.MerchantProfile).Assembly,
        typeof(Haworks.Merchant.Application.Merchants.Commands.CreateMerchant.CreateMerchantCommand).Assembly,
        typeof(Haworks.Merchant.Infrastructure.Persistence.MerchantDbContext).Assembly,
    ];

    [Fact]
    public void Merchant_assemblies_must_not_reference_sibling_services()
    {
        foreach (var assembly in MerchantAssemblies)
        {
            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(ForbiddenNamespacePrefixes).GetResult();
            result.IsSuccessful.Should().BeTrue($"Assembly '{assembly.GetName().Name}' has a forbidden cross-service dependency.");
        }
    }

    [Fact]
    public void Merchant_Application_must_not_reference_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Haworks.Merchant.Application.Merchants.Commands.CreateMerchant.CreateMerchantCommand).Assembly).ShouldNot().HaveDependencyOn("Haworks.Merchant.Infrastructure").GetResult();
        result.IsSuccessful.Should().BeTrue();
    }
}
