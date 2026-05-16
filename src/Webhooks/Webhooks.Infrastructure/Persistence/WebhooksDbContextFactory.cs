using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Haworks.Webhooks.Infrastructure.Persistence;

/// <summary>Design-time factory for <c>dotnet ef migrations</c>.</summary>
public sealed class WebhooksDbContextFactory : IDesignTimeDbContextFactory<WebhooksDbContext>
{
    public WebhooksDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WebhooksDbContext>()
            .UseNpgsql("Host=localhost;Database=webhooks;Username=postgres;Password=postgres")
            .Options;
        return new WebhooksDbContext(options);
    }
}
