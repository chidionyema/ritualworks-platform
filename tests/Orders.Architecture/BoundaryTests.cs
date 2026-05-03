using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace Haworks.Orders.Architecture;

/// <summary>
/// Enforces ADR-0001 monorepo boundary rules for orders-svc.
/// </summary>
public sealed class BoundaryTests
{
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "Haworks.Identity",
        "Haworks.Catalog",
        "Haworks.Payments",
        "Haworks.Content",
        "Haworks.CheckoutOrchestrator",
        "Haworks.BffWeb",
    ];

    private static readonly Assembly[] OrdersAssemblies =
    [
        typeof(Haworks.Orders.Domain.Order).Assembly,
        typeof(Haworks.Orders.Application.OrdersApplicationMarker).Assembly,
        typeof(Haworks.Orders.Infrastructure.OrderDbContext).Assembly,
    ];

    [Fact]
    public void Orders_assemblies_must_not_reference_sibling_services()
    {
        foreach (var assembly in OrdersAssemblies)
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOnAny(ForbiddenNamespacePrefixes)
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                $"Assembly '{assembly.GetName().Name}' has a forbidden cross-service dependency. " +
                $"Failing types: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
        }
    }

    [Fact]
    public void Orders_Domain_must_not_reference_Application_or_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Haworks.Orders.Domain.Order).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Haworks.Orders.Application",
                "Haworks.Orders.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"Orders.Domain depends on a higher layer. Violations: " +
            $"{string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void Orders_Application_must_not_reference_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Haworks.Orders.Application.OrdersApplicationMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Haworks.Orders.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"Orders.Application depends on Infrastructure (wrong direction). Violations: " +
            $"{string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }
}
