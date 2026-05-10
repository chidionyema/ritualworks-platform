using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace Haworks.Catalog.Architecture;

/// <summary>
/// Enforces the monorepo boundary rules from ADR-0001 for the catalog service.
///
/// Each Catalog.* assembly may only reference:
///   • System.* / Microsoft.* / third-party NuGet packages
///   • Haworks.Contracts.*    (cross-service event records)
///   • Haworks.BuildingBlocks.* (cross-cutting infrastructure)
///   • Sibling Catalog.* assemblies in the layered direction
///
/// It MUST NOT reference any other service's namespace
/// (Haworks.Identity, Haworks.Orders, Haworks.Payments, Haworks.Content,
///  Haworks.CheckoutOrchestrator, Haworks.BffWeb).
///
/// Directory.Build.props in src/Catalog/ blocks cross-service ProjectReference
/// items at MSBuild time. NetArchTest catches namespace-only references that
/// ProjectReference blocking would miss (e.g., a fully-qualified type used
/// without a using statement).
/// </summary>
public sealed class BoundaryTests
{
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "Haworks.Identity",
        "Haworks.Orders",
        "Haworks.Payments",
        "Haworks.Content",
        "Haworks.CheckoutOrchestrator",
        "Haworks.BffWeb",
    ];

    private static readonly Assembly[] CatalogAssemblies =
    [
        typeof(Haworks.Catalog.Domain.Product).Assembly,
        typeof(Haworks.Catalog.Application.Commands.CreateProductCommand).Assembly,
        typeof(Haworks.Catalog.Infrastructure.CatalogDbContext).Assembly,
    ];

    [Fact]
    public void Catalog_assemblies_must_not_reference_sibling_services()
    {
        foreach (var assembly in CatalogAssemblies)
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
    public void Catalog_Domain_must_not_reference_Application_or_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Haworks.Catalog.Domain.Product).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Haworks.Catalog.Application",
                "Haworks.Catalog.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"Catalog.Domain depends on a higher layer. Violations: " +
            $"{string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void Catalog_Application_must_not_reference_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Haworks.Catalog.Application.Commands.CreateProductCommand).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Haworks.Catalog.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"Catalog.Application depends on Infrastructure (wrong direction). Violations: " +
            $"{string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }
}
