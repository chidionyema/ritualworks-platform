using Microsoft.EntityFrameworkCore;
using Haworks.Cdc.Domain.Aggregates;

namespace Haworks.Cdc.Infrastructure.Persistence;

public partial class CdcDbContext
{
    public DbSet<CdcSource> Sources { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        modelBuilder.Entity<CdcSource>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.ServiceName).IsUnique();
        });
    }
}
