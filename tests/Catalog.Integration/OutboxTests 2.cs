using FluentAssertions;
using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.CurrentUser;
using Haworks.Catalog.Infrastructure;
using Haworks.Catalog.Infrastructure.Messaging;
using Haworks.Contracts.Catalog;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Haworks.Catalog.Integration;

public class OutboxTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder()
        .WithImage("rabbitmq:3-management-alpine")
        .Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _rabbit.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _rabbit.DisposeAsync();
    }

    private ServiceProvider BuildProvider(Action<IBusRegistrationConfigurator>? configureBus = null)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Production"));
        services.AddScoped(_ => Mock.Of<ICurrentUserService>(s => s.UserId == "test-user"));

        services.AddDbContext<CatalogDbContext>(options =>
        {
            options.UseNpgsql(_postgres.GetConnectionString());
        });

        services.AddMassTransit(mt =>
        {
            mt.AddEntityFrameworkOutbox<CatalogDbContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox();
            });

            mt.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(_rabbit.GetConnectionString());
                cfg.ConfigureEndpoints(context);
            });

            configureBus?.Invoke(mt);
        });

        services.AddDomainEventPublisher();

        return services.BuildServiceProvider();
    }

    private async Task EnsureSchema(ServiceProvider sp)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS catalog");
        await db.Database.EnsureCreatedAsync();
    }

    [Fact]
    public async Task PublishThenSave_EventIsStoredInOutbox()
    {
        await using var sp = BuildProvider();
        await EnsureSchema(sp);

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IDomainEventPublisher>();

        var @event = new StockReservedEvent 
        { 
            OrderId = Guid.NewGuid(),
            SagaId = Guid.NewGuid(),
            UserId = "user-123",
            Items = Array.Empty<StockReservationItem>(),
            OrderLineItems = Array.Empty<Haworks.Contracts.Checkout.CheckoutItemData>(),
            TotalAmount = 100,
            Currency = "USD",
            CustomerEmail = "test@example.com"
        };

        await publisher.PublishAsync(@event);
        await db.SaveChangesAsync();

        var outboxCount = await db.Database
            .SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM catalog.\"OutboxMessage\"")
            .FirstOrDefaultAsync();

        outboxCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Duplicate_Delivery_With_Same_MessageId_Is_Deduped_By_Inbox()
    {
        var messageId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        ProbeConsumer.HandleCount = 0;

        await using var sp = BuildProvider(mt =>
        {
            mt.AddConsumer<ProbeConsumer, CatalogConsumerDefinition<ProbeConsumer>>();
        });

        await EnsureSchema(sp);

        var bus = sp.GetRequiredService<IBusControl>();
        await bus.StartAsync();

        try
        {
            var @event = new StockReservedEvent 
            { 
                OrderId = orderId,
                SagaId = Guid.NewGuid(),
                UserId = "user-123",
                Items = Array.Empty<StockReservationItem>(),
                OrderLineItems = Array.Empty<Haworks.Contracts.Checkout.CheckoutItemData>(),
                TotalAmount = 100,
                Currency = "USD",
                CustomerEmail = "test@example.com"
            };

            await bus.Publish(@event, ctx => ctx.MessageId = messageId);
            await bus.Publish(@event, ctx => ctx.MessageId = messageId);

            // Poll for count
            for(int i=0; i<20 && ProbeConsumer.HandleCount == 0; i++) await Task.Delay(500);
            
            // Wait extra to see if it increases to 2
            await Task.Delay(2000);

            ProbeConsumer.HandleCount.Should().Be(1);
        }
        finally
        {
            await bus.StopAsync();
        }
    }

    public class ProbeConsumer : IConsumer<StockReservedEvent>
    {
        public static int HandleCount;
        public Task Consume(ConsumeContext<StockReservedEvent> context)
        {
            Interlocked.Increment(ref HandleCount);
            return Task.CompletedTask;
        }
    }
}
