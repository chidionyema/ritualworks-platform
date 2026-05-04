using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging;

namespace Haworks.Content.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for `dotnet ef migrations`. Mirrors the pattern used
/// by Catalog/Identity/Payments — hardcoded localhost connection (overridable
/// via DOTNET_EF_CONNECTION) and stubs for runtime-only DI dependencies.
/// </summary>
public sealed class ContentDbContextFactory : IDesignTimeDbContextFactory<ContentDbContext>
{
    public ContentDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("DOTNET_EF_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=content;Username=postgres;Password=postgres;SslMode=Disable";

        var optionsBuilder = new DbContextOptionsBuilder<ContentDbContext>();
        optionsBuilder.UseNpgsql(connectionString);

        var loggerFactory = LoggerFactory.Create(b => { });

        return new ContentDbContext(
            optionsBuilder.Options,
            new DesignTimeHostEnvironment(),
            loggerFactory,
            new DesignTimeCurrentUserService(),
            loggerFactory.CreateLogger<ContentDbContext>());
    }

    private sealed class DesignTimeHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Design";
        public string ApplicationName { get; set; } = "Haworks.Content.Infrastructure.Design";
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
