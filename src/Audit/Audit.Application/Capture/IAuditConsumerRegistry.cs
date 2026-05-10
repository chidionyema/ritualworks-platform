using MassTransit;

namespace Haworks.Audit.Application.Capture;

/// <summary>
/// L1.B implements this — reflects over <c>Haworks.Contracts</c> and
/// registers a generic <c>AuditConsumer&lt;TEvent&gt;</c> for every
/// <see cref="Haworks.Contracts.IDomainEvent"/>. L0 ships the interface
/// so <c>Audit.Api/Program.cs</c> can wire MassTransit consumers via a
/// stable seam without depending on the L1.B impl.
/// </summary>
public interface IAuditConsumerRegistry
{
    void RegisterConsumers(IBusRegistrationConfigurator cfg);
}
