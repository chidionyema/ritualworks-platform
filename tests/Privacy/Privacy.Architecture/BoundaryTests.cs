using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace Haworks.Privacy.Architecture;

public sealed class BoundaryTests
{
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "Haworks.Identity", "Haworks.Orders", "Haworks.Payments", "Haworks.Content",
        "Haworks.CheckoutOrchestrator", "Haworks.BffWeb", "Haworks.Catalog", "Haworks.Search", "Haworks.Payouts", "Haworks.Scheduler"
    ];

    private static readonly Assembly[] PrivacyAssemblies =
    [
        typeof(Haworks.Privacy.Domain.Aggregates.PrivacyRequest).Assembly,
        typeof(Haworks.Privacy.Application.Requests.Sagas.PrivacyRequestStateMachine).Assembly,
        typeof(Haworks.Privacy.Infrastructure.Persistence.PrivacyDbContext).Assembly,
    ];

    [Fact]
    public void Privacy_assemblies_must_not_reference_sibling_services()
    {
        foreach (var assembly in PrivacyAssemblies)
        {
            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(ForbiddenNamespacePrefixes).GetResult();
            result.IsSuccessful.Should().BeTrue($"Assembly '{assembly.GetName().Name}' has a forbidden cross-service dependency.");
        }
    }

    [Fact]
    public void Privacy_Application_must_not_reference_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Haworks.Privacy.Application.Requests.Sagas.PrivacyRequestStateMachine).Assembly).ShouldNot().HaveDependencyOn("Haworks.Privacy.Infrastructure").GetResult();
        result.IsSuccessful.Should().BeTrue();
    }
}
