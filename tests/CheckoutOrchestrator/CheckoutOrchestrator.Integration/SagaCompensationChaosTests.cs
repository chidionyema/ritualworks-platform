using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;
using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.Testing.Authentication;
using Haworks.Catalog.Application.Consumers;
using Haworks.Catalog.Domain;
using Haworks.Catalog.Domain.Interfaces;
using Haworks.CheckoutOrchestrator.Application.Sagas;
using Haworks.CheckoutOrchestrator.Domain;
using Haworks.CheckoutOrchestrator.Infrastructure;
using Haworks.Contracts.Catalog;
using Haworks.Contracts.Checkout;
using Haworks.Contracts.Payments;

namespace Haworks.CheckoutOrchestrator.Integration;

/// <summary>
/// THE SAGA COMPENSATION CHAOS TEST — the headline proof of the
/// platform's resilience story.
///
/// Scenario: a checkout starts, stock is reserved, then the payment
/// session creation fails. The saga MUST publish a compensation event
/// (StockReleaseRequested), catalog-svc's consumer MUST consume it and
/// release the reserved stock back to the original count, AND the saga
/// MUST end in Abandoned. No zombie reservations. No state where stock
/// is stuck reserved against an order that won't be paid.
///
/// What this test substitutes for the build plan's "kubectl pause
/// payments-svc" recipe: instead of pausing a pod, the test publishes
/// PaymentSessionFailed AFTER a successful StockReserved. Same observable
/// outcome from the saga's POV — payment didn't materialize, time to
/// compensate. Faster, deterministic, runs in CI.
///
/// What this test proves end-to-end:
///   1. The saga reaches StockReservedState after consuming StockReserved.
///   2. PaymentSessionFailed routes through the saga's compensation arrow.
///   3. The saga publishes StockReleaseRequested with the originally-reserved
///      items snapshotted into the saga state.
///   4. catalog-svc's StockReleaseRequestedConsumer reads it from the bus,
///      reverses each Product.ReleaseStock(qty), and publishes StockReleased.
///   5. The product's stock count returns to its pre-checkout level.
///   6. The saga's CurrentState ends in Abandoned with the right
///      FailureReason.
/// </summary>
public sealed class SagaCompensationChaosTests : IClassFixture<SagaCompensationFixture>, IAsyncLifetime
{
    private readonly SagaCompensationFixture _factory;

    public SagaCompensationChaosTests(SagaCompensationFixture factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.EnsureSchemaAsync();
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        harness.TestTimeout = TimeSpan.FromSeconds(30);
        harness.TestInactivityTimeout = TimeSpan.FromSeconds(10);
        await harness.Start();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Compensation_chain_returns_stock_to_pre_checkout_level_when_payment_fails()
    {
        // Seed: a Product with stock=10, then simulate a 3-unit reservation
        // that left it at stock=7 (the saga's view of the world after
        // StockReserved).
        var productId = Guid.NewGuid();
        var initialStock = 10;
        var reservedQuantity = 3;
        var stockAfterReservation = initialStock - reservedQuantity;

        await SeedProductAsync(productId, initialStockBeforeReservation: stockAfterReservation);

        var sagaId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        // Step 1: drive the saga to Initiated.
        await PublishAsync(new CheckoutInitiatedEvent
        {
            SagaId = sagaId,
            OrderId = orderId,
            UserId = "user-chaos",
            CustomerEmail = "chaos@example.com",
            TotalAmount = 30m,
            Items = new[] { new CheckoutItemData
            {
                ProductId = productId, ProductName = "Widget",
                Quantity = reservedQuantity, UnitPrice = 10m,
            }},
            IdempotencyKey = "chaos-key",
            IsGuest = false,
        });
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Initiated", TimeSpan.FromSeconds(15));

        // Step 2: drive the saga to StockReservedState — this is the
        // critical bit because StockReleaseRequested needs the reserved
        // items snapshotted into the saga state.
        await PublishAsync(new StockReservedEvent
        {
            OrderId = orderId, SagaId = sagaId, UserId = "user-chaos",
            TotalAmount = 30m, Currency = "USD", CustomerEmail = "chaos@example.com",
            Items = new[] { new Haworks.Contracts.Catalog.StockReservationItem
            {
                ProductId = productId, ProductName = "Widget",
                Quantity = reservedQuantity, RemainingStock = stockAfterReservation,
            }},
            OrderLineItems = new[] { new CheckoutItemData
            {
                ProductId = productId, ProductName = "Widget",
                Quantity = reservedQuantity, UnitPrice = 10m,
            }},
        });
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "StockReservedState", TimeSpan.FromSeconds(15));

        // Step 3: CHAOS — payment session creation fails. The saga MUST
        // publish StockReleaseRequested with the snapshotted items.
        await PublishAsync(new PaymentSessionFailedEvent
        {
            OrderId = orderId, SagaId = sagaId, Provider = "Stripe",
            ErrorCode = "card_declined", ErrorMessage = "Stripe rejected card",
            AttemptNumber = 1, IsFinalAttempt = true,
        });

        // Wait for the saga to reach Abandoned.
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Abandoned", TimeSpan.FromSeconds(15));

        // Wait for the StockReleaseRequested -> catalog consumer ->
        // StockReleased chain to complete. Polling for the consumer's
        // outbound publish is the cleanest signal that the chain finished.
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        await PollUntilAsync(
            () => harness.Published.Select<StockReleasedEvent>()
                .Any(p => p.Context.Message.OrderId == orderId),
            TimeSpan.FromSeconds(20));

        // ASSERTIONS — the headline guarantees of the compensation chain.

        // (a) Saga ended in Abandoned with the right reason.
        var sagaState = await ReadSagaAsync(sagaId);
        sagaState.Should().NotBeNull();
        sagaState!.CurrentState.Should().Be("Abandoned");
        sagaState.FailureReason.Should().Contain("PaymentSessionFailed");

        // (b) Saga published StockReleaseRequested with the right payload.
        var release = harness.Published.Select<StockReleaseRequestedEvent>()
            .FirstOrDefault(p => p.Context.Message.SagaId == sagaId);
        release.Should().NotBeNull(
            "the saga must compensate by publishing StockReleaseRequested when payment fails after stock reservation");
        release!.Context.Message.Items.Should().ContainSingle();
        release.Context.Message.Items.Single().ProductId.Should().Be(productId);
        release.Context.Message.Items.Single().Quantity.Should().Be(reservedQuantity);
        release.Context.Message.Reason.Should().Be("payment_session_failed");

        // (c) catalog-svc's consumer responded with StockReleased.
        var released = harness.Published.Select<StockReleasedEvent>()
            .FirstOrDefault(p => p.Context.Message.OrderId == orderId);
        released.Should().NotBeNull(
            "catalog-svc's StockReleaseRequestedConsumer must publish StockReleased after applying the release");
        released!.Context.Message.TotalUnitsReleased.Should().Be(reservedQuantity);

        // (d) Stock count is back to the pre-reservation level. THIS is
        // the hero assertion — proves the compensation actually moved
        // bytes in the database, not just emitted events.
        var finalStock = await ReadProductStockAsync(productId);
        finalStock.Should().Be(initialStock,
            "stock must be returned to the pre-checkout level after compensation completes");
    }

    private async Task SeedProductAsync(Guid productId, int initialStockBeforeReservation)
    {
        // Insert a Category + Product directly via the catalog DI repos.
        // Force the Product Id so the test can predict it via reflection on
        // the AuditableEntity Id setter.
        await using var scope = _factory.Services.CreateAsyncScope();
        var categoryRepo = scope.ServiceProvider.GetRequiredService<ICategoryRepository>();
        var productRepo = scope.ServiceProvider.GetRequiredService<IProductRepository>();

        var category = Category.Create("ChaosCategory", "for compensation tests");
        await categoryRepo.AddAsync(category);
        await categoryRepo.SaveChangesAsync();

        var product = Product.Create("Widget", "the chaos target", 10m, category.Id);
        typeof(Product).GetProperty("Id")!.SetValue(product, productId);
        product.RestockTo(initialStockBeforeReservation);
        await productRepo.AddAsync(product);
        await productRepo.SaveChangesAsync();
    }

    private async Task<int> ReadProductStockAsync(Guid productId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProductRepository>();
        var product = await repo.GetByIdAsync(productId);
        product.Should().NotBeNull();
        return product!.StockQuantity;
    }

    private async Task PublishAsync<T>(T evt) where T : class
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        await publisher.Publish(evt);
    }

    private string? SagaStateOrNull(Guid sagaId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CheckoutDbContext>();
        return db.CheckoutSagas.AsNoTracking()
            .Where(s => s.CorrelationId == sagaId)
            .Select(s => s.CurrentState)
            .FirstOrDefault();
    }

    private async Task<CheckoutSagaState?> ReadSagaAsync(Guid sagaId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CheckoutDbContext>();
        return await db.CheckoutSagas.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CorrelationId == sagaId);
    }

    private static async Task PollUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(250);
        }
    }
}

/// <summary>
/// Combined fixture that wires BOTH the CheckoutSaga AND catalog-svc's
/// StockReleaseRequestedConsumer in the same in-memory MT harness, with
/// each consumer's DbContext backed by its own Testcontainers postgres
/// instance. The two services communicate purely via published events on
/// the shared in-memory bus — same as production, with the broker
/// replaced by MT's in-memory transport for test determinism.
/// </summary>
public sealed class SagaCompensationFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _checkoutPostgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine").WithDatabase("checkout")
        .WithUsername("postgres").WithPassword("postgres").Build();

    private readonly PostgreSqlContainer _catalogPostgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine").WithDatabase("catalog")
        .WithUsername("postgres").WithPassword("postgres").Build();

    public string CheckoutConnectionString => _checkoutPostgres.GetConnectionString();
    public string CatalogConnectionString => _catalogPostgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_checkoutPostgres.StartAsync(), _catalogPostgres.StartAsync());

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__checkout", CheckoutConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__catalog", CatalogConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", "amqp://guest:guest@localhost:5672/");
        Environment.SetEnvironmentVariable("Vault__Enabled", "false");
        // AddJwksAuthentication's [Required] + ValidateOnStart trips host
        // build without these. Test scheme overrides real validation later.
        JwtTestDefaults.SetTestEnvironmentVariables();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await Task.WhenAll(_checkoutPostgres.DisposeAsync().AsTask(), _catalogPostgres.DisposeAsync().AsTask());
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((ctx, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:checkout"]  = CheckoutConnectionString,
                ["ConnectionStrings:catalog"]   = CatalogConnectionString,
                ["ConnectionStrings:rabbitmq"]  = "amqp://guest:guest@localhost:5672/",
                ["Vault:Enabled"]               = "false",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Wire catalog's DbContext + repository registrations alongside
            // checkout-svc's. catalog AddInfrastructure already short-circuits
            // on Test env (skips its production MassTransit wiring) — so we
            // get IProductRepository / ICategoryRepository / CatalogDbContext
            // registered without conflicting with our combined MT harness
            // below. The Test fixture supplied ConnectionStrings:catalog via
            // env vars in InitializeAsync.
            var catalogConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:catalog"] = CatalogConnectionString,
                })
                .Build();
            // The fixture is in Test env; we synthesize an IHostEnvironment
            // for the side-call so Catalog's AddInfrastructure short-circuits
            // its production MassTransit wiring (same path as the env-var
            // pattern that used to be threaded via OS-level env globals).
            Haworks.Catalog.Infrastructure.DependencyInjection.AddInfrastructure(
                services,
                catalogConfig,
                new TestHostEnvironment("Test"));

            // Combined harness — saga + catalog consumer share one in-memory
            // bus. Production runs them in separate processes against
            // RabbitMQ; the message contracts are identical, so the
            // observable behavior matches.
            services.AddMassTransitTestHarness(mt =>
            {
                mt.AddSagaStateMachine<CheckoutSaga, CheckoutSagaState>()
                    .EntityFrameworkRepository(r =>
                    {
                        r.ExistingDbContext<CheckoutDbContext>();
                        r.UsePostgres();
                    });
                mt.AddConsumer<StockReleaseRequestedConsumer>();
            });

            // catalog's consumer + saga both need IDomainEventPublisher to
            // wrap the harness's IPublishEndpoint. Catalog AddInfrastructure's
            // call to AddDomainEventPublisher() bailed early on Test env, so
            // do it here.
            services.AddDomainEventPublisher();
        });
    }

    public async Task EnsureSchemaAsync()
    {
        using var scope = Services.CreateScope();
        var checkoutDb = scope.ServiceProvider.GetRequiredService<CheckoutDbContext>();
        await checkoutDb.Database.MigrateAsync();
        var catalogDb = scope.ServiceProvider.GetRequiredService<Haworks.Catalog.Infrastructure.CatalogDbContext>();
        await catalogDb.Database.MigrateAsync();
    }
}

/// <summary>
/// Minimal IHostEnvironment for test-side calls to AddInfrastructure that
/// happen outside a normal Program.cs flow. The concrete HostingEnvironment
/// types in ASP.NET Core are internal; this stub satisfies the interface
/// without pulling in extra infrastructure.
/// </summary>
internal sealed class TestHostEnvironment(string environmentName) : Microsoft.Extensions.Hosting.IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.PhysicalFileProvider(AppContext.BaseDirectory);
}
