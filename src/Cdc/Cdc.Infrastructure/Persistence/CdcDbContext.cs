using Microsoft.EntityFrameworkCore;

namespace Haworks.Cdc.Infrastructure.Persistence;

/// <summary>
/// EF Core context for the cdc-svc Postgres database.
///
/// Scaffolded by 'wave run' as an empty shell. L1 tracks add their entities
/// + DbSets via partial classes (one partial file per track) so this base
/// file is never edited after L0 — keeping the parallel-execution contract.
/// </summary>
public partial class CdcDbContext : DbContext
{
    public CdcDbContext(DbContextOptions<CdcDbContext> options) : base(options) { }
}
