using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Haworks.Payments.Infrastructure;

/// <summary>
/// Design-time factory for `dotnet ef migrations`. Hardcodes a localhost
/// connection (overridable via DOTNET_EF_CONNECTION). Stubs runtime-only
/// DI dependencies (IHostEnvironment, ILoggerFactory, ICurrentUserService).
/// </summary>
public sealed class PaymentDbContextFactory : IDesignTimeDbContextFactory<PaymentDbContext>
{
    public PaymentDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("DOTNET_EF_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=payments;Username=postgres;Password=postgres;SslMode=Disable";

        var optionsBuilder = new DbContextOptionsBuilder<PaymentDbContext>();
        optionsBuilder.UseNpgsql(connectionString);

        return new PaymentDbContext(
            optionsBuilder.Options,
            new DesignTimeHostEnvironment(),
            Microsoft.Extensions.Logging.LoggerFactory.Create(b => { }),
            new DesignTimeCurrentUserService());
    }

    private sealed class DesignTimeHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Design";
        public string ApplicationName { get; set; } = "Haworks.Payments.Infrastructure.Design";
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
