using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Haworks.FeatureFlags.Api.Infrastructure;

/// <summary>Design-time factory for <c>dotnet ef migrations</c>.</summary>
public sealed class FeatureFlagsDbContextFactory : IDesignTimeDbContextFactory<FeatureFlagsDbContext>
{
    public FeatureFlagsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FeatureFlagsDbContext>()
            .UseNpgsql("Host=localhost;Database=featureflags;Username=postgres;Password=postgres")
            .Options;
        return new FeatureFlagsDbContext(options);
    }
}
