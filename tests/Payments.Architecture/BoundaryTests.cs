using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace Haworks.Payments.Architecture;

/// <summary>
/// Enforces ADR-0001 monorepo boundary rules for payments-svc.
///
/// Each Payments.* assembly may only reference:
///   • System.* / Microsoft.* / third-party NuGet packages
///   • Haworks.Contracts.*    (cross-service event records)
///   • Haworks.BuildingBlocks.* (cross-cutting infrastructure)
///   • Sibling Payments.* assemblies in the layered direction
///
/// MUST NOT reference any other service's namespace.
/// </summary>
public sealed class BoundaryTests
{
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "Haworks.Identity",
        "Haworks.Catalog",
        "Haworks.Orders",
        "Haworks.Content",
        "Haworks.CheckoutOrchestrator",
        "Haworks.BffWeb",
    ];

    private static readonly Assembly[] PaymentsAssemblies =
    [
        typeof(Haworks.Payments.Domain.Payment).Assembly,
        typeof(Haworks.Payments.Application.PaymentsApplicationMarker).Assembly,
        typeof(Haworks.Payments.Infrastructure.PaymentDbContext).Assembly,
    ];

    [Fact]
    public void Payments_assemblies_must_not_reference_sibling_services()
    {
        foreach (var assembly in PaymentsAssemblies)
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
    public void Payments_Domain_must_not_reference_Application_or_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Haworks.Payments.Domain.Payment).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Haworks.Payments.Application",
                "Haworks.Payments.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"Payments.Domain depends on a higher layer. Violations: " +
            $"{string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void Payments_Application_must_not_reference_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Haworks.Payments.Application.PaymentsApplicationMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Haworks.Payments.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"Payments.Application depends on Infrastructure (wrong direction). Violations: " +
            $"{string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }
}
