using Haworks.Cdc.Application.Interfaces;
using Haworks.Cdc.Domain.Aggregates;
using Haworks.Cdc.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Cdc.Infrastructure;

public sealed class EfCdcStore : ICdcStore
{
    private readonly CdcDbContext _db;

    public EfCdcStore(CdcDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyCollection<CdcSource>> GetEnabledSourcesAsync(CancellationToken ct = default)
    {
        return await _db.Sources.Where(s => s.Enabled).ToListAsync(ct);
    }

    public async Task<CdcSource?> GetSourceByNameAsync(string name, CancellationToken ct = default)
    {
        return await _db.Sources.FirstOrDefaultAsync(s => s.ServiceName == name, ct);
    }

    public async Task AddSourceAsync(CdcSource source, CancellationToken ct = default)
    {
        _db.Sources.Add(source);
        await Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _db.SaveChangesAsync(ct);
    }
}
