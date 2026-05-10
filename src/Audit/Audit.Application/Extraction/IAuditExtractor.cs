using MassTransit;
using Haworks.Contracts;

namespace Haworks.Audit.Application.Extraction;

/// <summary>
/// Strategy interface — given an inbound <see cref="IDomainEvent"/>,
/// return the <see cref="AuditRow"/> it should be persisted as.
///
/// L1.A ships the implementations (one reflection-based default plus
/// hand-written overrides for events with ambiguous entity ids per spec
/// § 5.1). L0 ships the surface only so L1.B's generic
/// <c>AuditConsumer&lt;T&gt;</c> can take a stable dependency.
/// </summary>
public interface IAuditExtractor<in T> where T : class, IDomainEvent
{
    AuditRow Extract(T evt, ConsumeContext<T> ctx);
}
