using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace Haworks.Identity.Architecture;

/// <summary>
/// Enforces the monorepo boundary rules from ADR-0001 for the identity service.
///
/// Each Identity.* assembly may only reference:
///   • System.* / Microsoft.* / third-party NuGet packages
///   • Haworks.Contracts.*    (cross-service event records)
///   • Haworks.BuildingBlocks.* (cross-cutting infrastructure)
///   • Sibling Identity.* assemblies in the layered direction
///
/// It MUST NOT reference any other service's namespace
/// (Haworks.Catalog, Haworks.Orders, Haworks.Payments, Haworks.Content,
///  Haworks.CheckoutOrchestrator, Haworks.BffWeb).
///
/// The Directory.Build.props in src/Identity/ already blocks
/// cross-service ProjectReference items at MSBuild time. NetArchTest
/// here catches namespace-only references that ProjectReference blocking
/// could miss (e.g., a fully-qualified type used without a using).
/// </summary>
public sealed class BoundaryTests
{
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "Haworks.Catalog",
        "Haworks.Orders",
        "Haworks.Payments",
        "Haworks.Content",
        "Haworks.CheckoutOrchestrator",
        "Haworks.BffWeb",
    ];

    private static readonly Assembly[] IdentityAssemblies =
    [
        typeof(Haworks.Identity.Domain.User).Assembly,
        typeof(Haworks.Identity.Application.LoginCommand).Assembly,
        typeof(Haworks.Identity.Infrastructure.AppIdentityDbContext).Assembly,
    ];

    [Fact]
    public void Identity_assemblies_must_not_reference_sibling_services()
    {
        foreach (var assembly in IdentityAssemblies)
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
    public void Identity_Domain_must_not_reference_Application_or_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Haworks.Identity.Domain.User).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Haworks.Identity.Application",
                "Haworks.Identity.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"Identity.Domain depends on a higher layer. Violations: " +
            $"{string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void Identity_Application_must_not_reference_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Haworks.Identity.Application.LoginCommand).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Haworks.Identity.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"Identity.Application depends on Infrastructure (wrong direction). Violations: " +
            $"{string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }
}
