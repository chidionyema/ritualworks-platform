using Haworks.BuildingBlocks.CurrentUser;
using Haworks.BuildingBlocks.Persistence;
using Haworks.Location.Application.Interfaces;
using Haworks.Location.Domain.Entities;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Haworks.Location.Infrastructure.Persistence;

/// <summary>
/// DbContext for the Location bounded context. 
/// Uses PostGIS for geospatial data and EF Core Outbox for transactional messaging.
/// </summary>
public class LocationDbContext : DbContext, ILocationDbContext
{
    private readonly IHostEnvironment _environment;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICurrentUserService _currentUserService;

    public LocationDbContext(
        DbContextOptions<LocationDbContext> options,
        IHostEnvironment environment,
        ILoggerFactory loggerFactory,
        ICurrentUserService currentUserService)
        : base(options)
    {
        _environment = environment;
        _loggerFactory = loggerFactory;
        _currentUserService = currentUserService;

        ChangeTracker.LazyLoadingEnabled = false;
    }

    public DbSet<Address> Addresses => Set<Address>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        optionsBuilder.UseLoggerFactory(_loggerFactory);

        if (_environment.IsDevelopment())
        {
            optionsBuilder.EnableSensitiveDataLogging();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Ensure PostGIS extension is available
        modelBuilder.HasPostgresExtension("postgis");

        modelBuilder.HasDefaultSchema("location");

        modelBuilder.Entity<Address>(entity =>
        {
            entity.ToTable("Addresses");
            entity.HasKey(a => a.Id);
            
            entity.Property(a => a.Street).HasMaxLength(500).IsRequired();
            entity.Property(a => a.City).HasMaxLength(200).IsRequired();
            entity.Property(a => a.Postcode).HasMaxLength(20).IsRequired();
            entity.Property(a => a.Country).HasMaxLength(100).IsRequired();
            
            // Geography Point with SRID 4326 (WGS 84)
            entity.Property(a => a.Coordinates)
                .HasColumnType("geography(Point, 4326)")
                .IsRequired();
                
            entity.Property(a => a.Geohash).HasMaxLength(12).IsRequired();
            entity.Property(a => a.Metadata).HasColumnType("jsonb");

            // Spatial index for coordinates
            entity.HasIndex(a => a.Coordinates)
                .HasMethod("gist");
                
            entity.HasIndex(a => a.Geohash);
        });

        // MassTransit Outbox entities
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampAuditFields();
        return await base.SaveChangesAsync(cancellationToken);
    }

    private void StampAuditFields()
    {
        var entries = ChangeTracker.Entries<AuditableEntity>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified);

        foreach (var entry in entries)
        {
            entry.Entity.LastModifiedDate = DateTime.UtcNow;
            entry.Entity.LastModifiedBy = _currentUserService.UserId ?? "system";
            entry.Entity.ModifiedFromIp = _currentUserService.ClientIp ?? "unknown";

            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = DateTime.UtcNow;
                entry.Entity.CreatedBy = _currentUserService.UserId ?? "system";
                entry.Entity.CreatedFromIp = _currentUserService.ClientIp ?? "unknown";
            }
        }
    }
}
