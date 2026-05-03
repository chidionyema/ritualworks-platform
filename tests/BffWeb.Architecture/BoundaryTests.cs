using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace Haworks.BffWeb.Architecture;

/// <summary>
/// Boundary rules for bff-web. The composition layer is allowed to know
/// about every backend service in the abstract — but ONLY via Contracts
/// (events) and HttpClient (REST URLs identified by string keys). It MUST
/// NOT pull in another service's namespace; that's what proves the
/// boundaries are physically enforced.
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
        "Haworks.CheckoutOrchestrator",
    ];

    private static readonly Assembly[] BffAssemblies =
    [
        typeof(Haworks.BffWeb.Domain.BffWebDomainMarker).Assembly,
        typeof(Haworks.BffWeb.Application.BffWebApplicationMarker).Assembly,
    ];

    [Fact]
    public void BffWeb_assemblies_must_not_reference_sibling_services()
    {
        foreach (var assembly in BffAssemblies)
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
    public void BffWeb_Domain_must_not_reference_Application_or_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Haworks.BffWeb.Domain.BffWebDomainMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Haworks.BffWeb.Application",
                "Haworks.BffWeb.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void BffWeb_Application_must_not_reference_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Haworks.BffWeb.Application.BffWebApplicationMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Haworks.BffWeb.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
    }
}
