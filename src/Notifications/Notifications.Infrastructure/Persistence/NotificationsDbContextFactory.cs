using Haworks.BuildingBlocks.CurrentUser;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Haworks.Notifications.Infrastructure.Persistence;

/// <summary>Design-time factory for <c>dotnet ef migrations</c>.</summary>
public sealed class NotificationsDbContextFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql("Host=localhost;Database=notifications;Username=postgres;Password=postgres")
            .Options;

        var env = new DesignTimeHostEnvironment();
        return new NotificationsDbContext(options, env, LoggerFactory.Create(_ => { }), new DesignTimeUserService());
    }

    private sealed class DesignTimeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "DesignTime";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class DesignTimeUserService : ICurrentUserService
    {
        public string? UserId => "design-time";
        public string? ClientIp => "127.0.0.1";
    }
}
