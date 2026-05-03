using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Haworks.CheckoutOrchestrator.Infrastructure;

public sealed class CheckoutDbContextFactory : IDesignTimeDbContextFactory<CheckoutDbContext>
{
    public CheckoutDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("DOTNET_EF_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=checkout;Username=postgres;Password=postgres;SslMode=Disable";

        var optionsBuilder = new DbContextOptionsBuilder<CheckoutDbContext>();
        optionsBuilder.UseNpgsql(connectionString);

        return new CheckoutDbContext(
            optionsBuilder.Options,
            new DesignTimeHostEnvironment(),
            Microsoft.Extensions.Logging.LoggerFactory.Create(b => { }));
    }

    private sealed class DesignTimeHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Design";
        public string ApplicationName { get; set; } = "Haworks.CheckoutOrchestrator.Infrastructure.Design";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
