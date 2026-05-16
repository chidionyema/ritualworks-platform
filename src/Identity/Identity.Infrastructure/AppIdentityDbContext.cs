using Haworks.Identity.Application;
using Haworks.BuildingBlocks.Persistence;
using Haworks.Identity.Domain;
using Haworks.Identity.Domain;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Haworks.Identity.Infrastructure;

/// <summary>
/// DbContext for Identity bounded context.
/// Manages Users, UserProfiles, RefreshTokens, and RevokedTokens.
/// Extends IdentityDbContext for ASP.NET Identity integration.
/// </summary>
public class AppIdentityDbContext : IdentityDbContext<User>
{
    private readonly IHostEnvironment _environment;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICurrentUserService _currentUserService;

    public AppIdentityDbContext(
        DbContextOptions<AppIdentityDbContext> options,
        IHostEnvironment environment,
        ILoggerFactory loggerFactory,
        ICurrentUserService currentUserService,
        ILogger<AppIdentityDbContext> logger)
        : base(options)
    {
        _environment = environment;
        _loggerFactory = loggerFactory;
        _currentUserService = currentUserService;
    }

    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<RevokedToken> RevokedTokens => Set<RevokedToken>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        ChangeTracker.LazyLoadingEnabled = false;

        optionsBuilder.UseLoggerFactory(_loggerFactory);

        // Enable sensitive data logging only in development
        if (_environment.IsDevelopment())
        {
            optionsBuilder.EnableSensitiveDataLogging();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Set default schema for Identity context
        modelBuilder.HasDefaultSchema("identity");

        // User configuration (extends IdentityUser)
        modelBuilder.Entity<User>(entity =>
        {
            // Global query filter — inactive users excluded by default.
            // Use .IgnoreQueryFilters() for admin/erasure scenarios.
            entity.HasQueryFilter(u => u.IsActive);

            entity.Property(u => u.IsActive)
                .HasDefaultValue(true);

            entity.Property(u => u.DeactivatedAt);

            entity.Property(u => u.CheckoutSessionId)
                .HasMaxLength(200);

            entity.Property(u => u.StripeCustomerId)
                .HasMaxLength(200);

            entity.HasIndex(u => u.StripeCustomerId)
                .HasFilter("\"StripeCustomerId\" IS NOT NULL")
                .HasDatabaseName("IX_Users_StripeCustomerId");

            // Profile relationship
            entity.HasOne(u => u.Profile)
                .WithOne(p => p.User)
                .HasForeignKey<UserProfile>(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // UserProfile configuration
        modelBuilder.Entity<UserProfile>(entity =>
        {
            entity.ToTable("UserProfiles");

            entity.Property(up => up.FirstName)
                .HasMaxLength(100);

            entity.Property(up => up.LastName)
                .HasMaxLength(100);

            entity.Property(up => up.Phone)
                .HasMaxLength(20);

            entity.Property(up => up.Address)
                .HasMaxLength(500);

            entity.Property(up => up.City)
                .HasMaxLength(100);

            entity.Property(up => up.State)
                .HasMaxLength(100);

            entity.Property(up => up.PostalCode)
                .HasMaxLength(20);

            entity.Property(up => up.Country)
                .HasMaxLength(100)
                .HasDefaultValue("US");

            entity.Property(up => up.Bio)
                .HasMaxLength(2000);

            entity.Property(up => up.Website)
                .HasMaxLength(500);

            entity.Property(up => up.AvatarUrl)
                .HasMaxLength(1000);

            entity.HasIndex(up => up.UserId)
                .IsUnique()
                .HasFilter("\"UserId\" IS NOT NULL")
                .HasDatabaseName("IX_UserProfiles_UserId");

            entity.Property(up => up.LastLogin)
                .HasDefaultValueSql("NOW()");
        });

        // RefreshToken configuration
        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("RefreshTokens");

            entity.Property(rt => rt.Token)
                .HasMaxLength(500)
                .IsRequired();

            entity.Property(rt => rt.UserId)
                .HasMaxLength(450)
                .IsRequired();

            entity.HasIndex(rt => rt.Token)
                .IsUnique()
                .HasDatabaseName("IX_RefreshTokens_Token");

            entity.HasIndex(rt => rt.UserId)
                .HasDatabaseName("IX_RefreshTokens_UserId");

            entity.HasIndex(rt => rt.Expires)
                .HasDatabaseName("IX_RefreshTokens_Expires");

            entity.Property(rt => rt.Expires)
                .HasDefaultValueSql("NOW() + INTERVAL '7 days'");

            entity.HasOne(rt => rt.User)
                .WithMany()
                .HasForeignKey(rt => rt.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // RevokedToken configuration
        modelBuilder.Entity<RevokedToken>(entity =>
        {
            entity.ToTable("RevokedTokens");

            entity.Property(rt => rt.Token)
                .HasMaxLength(500)
                .IsRequired();

            entity.Property(rt => rt.Reason)
                .HasMaxLength(200);

            entity.Property(rt => rt.UserId)
                .HasMaxLength(450);

            entity.HasIndex(rt => rt.Token)
                .IsUnique()
                .HasDatabaseName("IX_RevokedTokens_Token");

            entity.HasIndex(rt => rt.RevokedAt)
                .HasDatabaseName("IX_RevokedTokens_RevokedAt");

            entity.HasIndex(rt => rt.ExpiresAt)
                .HasDatabaseName("IX_RevokedTokens_ExpiresAt");

            entity.Property(rt => rt.RevokedAt)
                .HasDefaultValueSql("NOW()");

            entity.HasOne(rt => rt.User)
                .WithMany()
                .HasForeignKey(rt => rt.UserId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        AddAuditInfo();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void AddAuditInfo()
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
