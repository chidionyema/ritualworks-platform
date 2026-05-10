using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace Haworks.CheckoutOrchestrator.Architecture;

/// <summary>
/// Boundary rules for checkout-orchestrator-svc. The saga is the one place
/// in the system that legitimately consumes events from EVERY upstream
/// service (Catalog, Payments) AND publishes orchestration triggers back
/// at them — but it does so via Haworks.Contracts only, never by
/// importing another service's namespace.
/// </summary>
public sealed class BoundaryTests
{
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "Haworks.Identity",
        "Haworks.Catalog",
        "Haworks.Orders",
        "Haworks.Payments",
        "Haworks.Content",
        "Haworks.BffWeb",
    ];

    private static readonly Assembly[] CheckoutAssemblies =
    [
        typeof(Haworks.CheckoutOrchestrator.Domain.CheckoutSagaState).Assembly,
        typeof(Haworks.CheckoutOrchestrator.Application.CheckoutApplicationMarker).Assembly,
        typeof(Haworks.CheckoutOrchestrator.Infrastructure.CheckoutDbContext).Assembly,
    ];

    [Fact]
    public void CheckoutOrchestrator_assemblies_must_not_reference_sibling_services()
    {
        foreach (var assembly in CheckoutAssemblies)
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
    public void CheckoutOrchestrator_Domain_must_not_reference_Application_or_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Haworks.CheckoutOrchestrator.Domain.CheckoutSagaState).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Haworks.CheckoutOrchestrator.Application",
                "Haworks.CheckoutOrchestrator.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void CheckoutOrchestrator_Application_must_not_reference_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Haworks.CheckoutOrchestrator.Application.CheckoutApplicationMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Haworks.CheckoutOrchestrator.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
    }
}
