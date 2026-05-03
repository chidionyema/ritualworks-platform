using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Haworks.Identity.Infrastructure;

/// <summary>
/// Design-time factory used by `dotnet ef migrations` and `dotnet ef database update`.
///
/// At design time, IConfiguration / IHostEnvironment / Aspire env vars are not
/// available — EF needs to construct the DbContext from minimal inputs. This
/// factory uses a hardcoded localhost connection string suitable for migration
/// generation against a dev postgres (or to operate purely against the EF
/// in-memory model when only generating SQL, not applying it).
///
/// At runtime, the regular DI registration in <see cref="DependencyInjection"/>
/// is used and this factory is ignored.
/// </summary>
public sealed class AppIdentityDbContextFactory : IDesignTimeDbContextFactory<AppIdentityDbContext>
{
    public AppIdentityDbContext CreateDbContext(string[] args)
    {
        // Connection string is irrelevant for `migrations add` (which only
        // reads the model). Used only when running `database update` against
        // a real DB — override via DOTNET_EF_CONNECTION env var.
        var connectionString = Environment.GetEnvironmentVariable("DOTNET_EF_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=identity;Username=postgres;Password=postgres;SslMode=Disable";

        var optionsBuilder = new DbContextOptionsBuilder<AppIdentityDbContext>();
        optionsBuilder.UseNpgsql(connectionString);

        // The DbContext constructor requires IHostEnvironment, ILoggerFactory,
        // ICurrentUserService, ILogger — none of which exist at design time.
        // Pass nulls / fakes via the parameterless protected ctor branch.
        return new AppIdentityDbContext(
            optionsBuilder.Options,
            new DesignTimeHostEnvironment(),
            Microsoft.Extensions.Logging.LoggerFactory.Create(b => { }),
            new DesignTimeCurrentUserService(),
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<AppIdentityDbContext>());
    }

    private sealed class DesignTimeHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Design";
        public string ApplicationName { get; set; } = "Haworks.Identity.Infrastructure.Design";
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
