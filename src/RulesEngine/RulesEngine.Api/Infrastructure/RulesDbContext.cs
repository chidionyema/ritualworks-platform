using Haworks.RulesEngine.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haworks.RulesEngine.Api.Infrastructure;

public sealed class RulesDbContext : DbContext
{
    public RulesDbContext(DbContextOptions<RulesDbContext> options) : base(options) { }

    public DbSet<Rule> Rules => Set<Rule>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("rules");

        modelBuilder.Entity<Rule>(b =>
        {
            b.ToTable("rules");
            b.HasKey(r => r.Id);
            b.Property(r => r.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            b.Property(r => r.Name).HasColumnName("name").IsRequired().HasMaxLength(200);
            b.Property(r => r.Expression).HasColumnName("expression").IsRequired().HasMaxLength(4000);
            b.Property(r => r.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            b.Property(r => r.CreatedAt).HasColumnName("created_at");
            b.Property(r => r.UpdatedAt).HasColumnName("updated_at");
            b.HasIndex(r => r.Name).IsUnique();
        });
    }
}
