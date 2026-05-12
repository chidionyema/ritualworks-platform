using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;
using Haworks.BuildingBlocks.Testing.Authentication;
using Haworks.BuildingBlocks.Testing.Containers;
using Testcontainers.RabbitMq;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System;
using System.Threading.Tasks;
using Npgsql;
using Testcontainers.PostgreSql;
using Haworks.BuildingBlocks.CurrentUser;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Location.Application.Interfaces;
using Moq;

namespace Haworks.Location.Integration;

public class LocationWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly RabbitMqContainer _rabbitMqContainer = new RabbitMqBuilder()
        .WithImage("rabbitmq:3-management")
        .Build();

    private readonly PostgreSqlContainer _postgreSqlContainer = new PostgreSqlBuilder()
        .WithImage("postgis/postgis:16-3.4-alpine")
        .WithDatabase("location")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;
    public string RabbitMqConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(
            _rabbitMqContainer.StartAsync(),
            _postgreSqlContainer.StartAsync()
        );

        RabbitMqConnectionString = _rabbitMqContainer.GetConnectionString();
        ConnectionString = _postgreSqlContainer.GetConnectionString();

        JwtTestDefaults.SetTestEnvironmentVariables();

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__location", ConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", RabbitMqConnectionString);
        Environment.SetEnvironmentVariable("Vault__Enabled", "false");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await Task.WhenAll(
            _rabbitMqContainer.DisposeAsync().AsTask(),
            _postgreSqlContainer.DisposeAsync().AsTask()
        );
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
                ["ConnectionStrings:rabbitmq"] = RabbitMqConnectionString,
                ["Vault:Enabled"] = "false",
                ["MigrateDatabase"] = "true",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Identity
            var currentUserMock = new Mock<ICurrentUserService>();
            currentUserMock.Setup(x => x.UserId).Returns("test-user");
            currentUserMock.Setup(x => x.ClientIp).Returns("127.0.0.1");
            services.AddSingleton(currentUserMock.Object);

            // Messaging (Stub out MassTransit dependencies in Test environment)
            var publisherMock = new Mock<IDomainEventPublisher>();
            services.AddScoped(_ => publisherMock.Object);

            // Geocoding (Mock for stability)
            var geocodingMock = new Mock<IGeocodingService>();
            geocodingMock.Setup(x => x.GeocodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((51.5074, -0.1278));
            services.AddScoped(_ => geocodingMock.Object);
        });
    }
}
