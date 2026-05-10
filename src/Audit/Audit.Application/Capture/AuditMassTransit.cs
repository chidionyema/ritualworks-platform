using Haworks.Contracts;
using MassTransit;

namespace Haworks.Audit.Application.Capture;

/// <summary>
/// Static seam between L0's <c>Program.cs</c> and L1.B.
///
/// Registers <see cref="AuditConsumer{T}"/> for every <see cref="IDomainEvent"/>
/// via reflection over <c>Haworks.Contracts</c>.
/// </summary>
public static class AuditMassTransit
{
    public static void RegisterConsumers(IBusRegistrationConfigurator cfg)
    {
        var eventTypes = typeof(IDomainEvent).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IDomainEvent).IsAssignableFrom(t));

        foreach (var eventType in eventTypes)
        {
            var consumerType = typeof(AuditConsumer<>).MakeGenericType(eventType);
            cfg.AddConsumer(consumerType);
        }
    }
}
