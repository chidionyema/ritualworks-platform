using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;
using Haworks.BuildingBlocks.Testing.Authentication;
using Haworks.BuildingBlocks.Testing.Containers;
using Microsoft.Extensions.DependencyInjection;
using Haworks.BuildingBlocks.CurrentUser;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Location.Application.Interfaces;
using Moq;

namespace Haworks.Location.Integration;

public class LocationWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        ConnectionString = await SharedTestPostGIS.CreateDatabaseAsync("location");

        JwtTestDefaults.SetTestEnvironmentVariables();

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__location", ConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", "amqp://guest:guest@localhost:5672");
        Environment.SetEnvironmentVariable("Vault__Enabled", "false");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:location"] = ConnectionString,
                ["Vault:Enabled"] = "false",
                ["MigrateDatabase"] = "true",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            var currentUserMock = new Mock<ICurrentUserService>();
            currentUserMock.Setup(x => x.UserId).Returns("test-user");
            currentUserMock.Setup(x => x.ClientIp).Returns("127.0.0.1");
            services.AddSingleton(currentUserMock.Object);

            var publisherMock = new Mock<IDomainEventPublisher>();
            services.AddScoped(_ => publisherMock.Object);

            var geocodingMock = new Mock<IGeocodingService>();
            geocodingMock.Setup(x => x.GeocodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((51.5074, -0.1278));
            services.AddScoped(_ => geocodingMock.Object);
        });
    }
}
