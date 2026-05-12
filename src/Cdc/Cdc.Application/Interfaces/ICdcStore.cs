using Haworks.Cdc.Domain.Aggregates;

namespace Haworks.Cdc.Application.Interfaces;

public interface ICdcStore
{
    Task<IReadOnlyCollection<CdcSource>> GetEnabledSourcesAsync(CancellationToken ct = default);
    Task<CdcSource?> GetSourceByNameAsync(string name, CancellationToken ct = default);
    Task AddSourceAsync(CdcSource source, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
