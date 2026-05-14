using Haworks.BuildingBlocks.CurrentUser;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Haworks.Location.Infrastructure.Persistence;

/// <summary>
/// Factory for creating <see cref="LocationDbContext"/> at design time (e.g. for EF migrations).
/// Prevents DI resolution errors when running 'dotnet ef migrations'.
/// </summary>
public class LocationDbContextFactory : IDesignTimeDbContextFactory<LocationDbContext>
{
    public LocationDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<LocationDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=location;Username=postgres;Password=postgres", 
            o => o.UseNetTopologySuite());

        return new LocationDbContext(
            optionsBuilder.Options,
            new DummyHostEnvironment(),
            NullLoggerFactory.Instance,
            new DummyCurrentUserService());
    }

    private sealed class DummyHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Location.Api";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class DummyCurrentUserService : ICurrentUserService
    {
        public string? UserId => "system";
        public string? ClientIp => "127.0.0.1";
    }
}
