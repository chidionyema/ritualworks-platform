using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace Haworks.Scheduler.Architecture;

public sealed class BoundaryTests
{
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "Haworks.Identity", "Haworks.Orders", "Haworks.Payments", "Haworks.Content",
        "Haworks.CheckoutOrchestrator", "Haworks.BffWeb", "Haworks.Catalog", "Haworks.Search", "Haworks.Payouts"
    ];

    private static readonly Assembly[] SchedulerAssemblies =
    [
        typeof(Haworks.Scheduler.Application.Scheduling.Commands.ScheduleEvent.ScheduleEventCommand).Assembly,
        typeof(Haworks.Scheduler.Infrastructure.Persistence.HangfireEventScheduler).Assembly,
    ];

    [Fact]
    public void Scheduler_assemblies_must_not_reference_sibling_services()
    {
        foreach (var assembly in SchedulerAssemblies)
        {
            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(ForbiddenNamespacePrefixes).GetResult();
            result.IsSuccessful.Should().BeTrue($"Assembly '{assembly.GetName().Name}' has a forbidden cross-service dependency.");
        }
    }

    [Fact]
    public void Scheduler_Application_must_not_reference_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Haworks.Scheduler.Application.Scheduling.Commands.ScheduleEvent.ScheduleEventCommand).Assembly).ShouldNot().HaveDependencyOn("Haworks.Scheduler.Infrastructure").GetResult();
        result.IsSuccessful.Should().BeTrue();
    }
}
