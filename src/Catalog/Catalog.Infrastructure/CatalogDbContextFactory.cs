using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Haworks.Catalog.Infrastructure;

/// <summary>
/// Design-time factory for `dotnet ef migrations`. See the equivalent in
/// Identity.Infrastructure for the rationale — we hardcode a localhost
/// connection (overridable via DOTNET_EF_CONNECTION) and stub out the
/// runtime-only DI dependencies.
/// </summary>
public sealed class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("DOTNET_EF_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=catalog;Username=postgres;Password=postgres;SslMode=Disable";

        var optionsBuilder = new DbContextOptionsBuilder<CatalogDbContext>();
        optionsBuilder.UseNpgsql(connectionString);

        return new CatalogDbContext(
            optionsBuilder.Options,
            new DesignTimeHostEnvironment(),
            Microsoft.Extensions.Logging.LoggerFactory.Create(b => { }),
            new DesignTimeCurrentUserService());
    }

    private sealed class DesignTimeHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Design";
        public string ApplicationName { get; set; } = "Haworks.Catalog.Infrastructure.Design";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class DesignTimeCurrentUserService : Haworks.BuildingBlocks.CurrentUser.ICurrentUserService
    {
        public string? UserId => null;
        public string? ClientIp => null;
    }
}
