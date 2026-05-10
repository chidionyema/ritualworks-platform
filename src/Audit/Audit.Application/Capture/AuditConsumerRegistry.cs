using Haworks.Contracts;
using MassTransit;

namespace Haworks.Audit.Application.Capture;

/// <summary>
/// Implementation of <see cref="IAuditConsumerRegistry"/> that registers
/// <see cref="AuditConsumer{T}"/> for every <see cref="IDomainEvent"/>
/// found in the contracts assembly.
/// </summary>
public sealed class AuditConsumerRegistry : IAuditConsumerRegistry
{
    public void RegisterConsumers(IBusRegistrationConfigurator cfg)
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
