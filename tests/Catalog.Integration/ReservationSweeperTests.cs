using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Haworks.Catalog.Application.Options;
using Haworks.Catalog.Domain;
using Haworks.Catalog.Infrastructure;
using Haworks.Catalog.Infrastructure.BackgroundServices;
using Haworks.Contracts.Catalog;

namespace Haworks.Catalog.Integration;

/// <summary>
/// Integration coverage for B3's <see cref="ReservationSweeperService"/>.
///
/// The sweeper is a <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>
/// in production, but its hosted-service registration is suppressed under
/// <c>ASPNETCORE_ENVIRONMENT=Test</c> so xUnit can drive expiry
/// deterministically via the internal <c>SweepOnceAsync</c> entry point
/// without waiting on the timer. Tests mutate reservation rows through the
/// EF context to simulate clock-side expiry without sleeping.
/// </summary>
[Collection("Catalog Integration")]
public sealed class ReservationSweeperTests : IAsyncLifetime
{
    private readonly CatalogWebAppFactory _factory;

    public ReservationSweeperTests(CatalogWebAppFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.EnsureSchemaAsync();
        await ResetReservationsAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SweepOnce_expires_reservations_with_past_deadline()
    {
        // Arrange: 1 expired-pending, 1 future-pending, 1 already-confirmed.
        var category = await SeedCategoryAsync();
        var product = await SeedProductAsync(category, initialStock: 100);

        var expiredId = await SeedReservationAsync(
            product,
            quantity: 5,
            expiresAt: DateTime.UtcNow.AddMinutes(-1),
            status: ReservationStatus.Pending);

        var futureId = await SeedReservationAsync(
            product,
            quantity: 3,
            expiresAt: DateTime.UtcNow.AddMinutes(10),
            status: ReservationStatus.Pending);

        var confirmedId = await SeedReservationAsync(
            product,
            quantity: 7,
            expiresAt: DateTime.UtcNow.AddMinutes(-2),
            status: ReservationStatus.Confirmed);

        var sweeper = CreateSweeper(_factory.Services, batchSize: 200);

        // Act
        var expiredCount = await sweeper.SweepOnceAsync(CancellationToken.None);

        // Assert — only the expired-pending row was processed.
        expiredCount.Should().Be(1);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var expired = await db.StockReservations.AsNoTracking().FirstAsync(r => r.Id == expiredId);
            var future = await db.StockReservations.AsNoTracking().FirstAsync(r => r.Id == futureId);
            var confirmed = await db.StockReservations.AsNoTracking().FirstAsync(r => r.Id == confirmedId);

            expired.Status.Should().Be(ReservationStatus.Expired,
                "the past-deadline pending row must transition to Expired");
            expired.ExpiredAt.Should().NotBeNull();

            future.Status.Should().Be(ReservationStatus.Pending,
                "future-deadline pending rows must not be touched");
            confirmed.Status.Should().Be(ReservationStatus.Confirmed,
                "already-Confirmed rows must not regress to Expired");
        }
    }

    [Fact]
    public async Task SweepOnce_caps_at_batch_size()
    {
        // Arrange: 250 expired-pending; configure batch size 100.
        var category = await SeedCategoryAsync();
        var product = await SeedProductAsync(category, initialStock: 1_000);

        const int seeded = 250;
        const int batchSize = 100;
        var pastDeadline = DateTime.UtcNow.AddMinutes(-5);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            for (int i = 0; i < seeded; i++)
            {
                var reservation = StockReservation.Create(
                    userId: $"u-{i}",
                    itemsJson: JsonSerializer.Serialize(new[]
                    {
                        new StockReservationItem
                        {
                            ProductId = product,
                            ProductName = "P",
                            Quantity = 1,
                        },
                    }),
                    ttl: TimeSpan.FromMinutes(15));
                ForceExpiresAt(reservation, pastDeadline);
                db.StockReservations.Add(reservation);
            }
            await db.SaveChangesAsync();
        }

        var sweeper = CreateSweeper(_factory.Services, batchSize: batchSize);

        // Act
        var expiredCount = await sweeper.SweepOnceAsync(CancellationToken.None);

        // Assert
        expiredCount.Should().Be(batchSize,
            "the sweeper must cap each iteration at the configured batch size");

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var stillPending = await db.StockReservations
                .AsNoTracking()
                .CountAsync(r => r.Status == ReservationStatus.Pending);
            stillPending.Should().Be(seeded - batchSize,
                "the rest of the candidates must still be Pending for the next sweep");
        }
    }

    [Fact]
    public async Task SweepOnce_skips_already_confirmed_reservations()
    {
        // Arrange: a Confirmed row whose ExpiresAt is in the past. The
        // ListExpiredReservationsAsync query already filters by
        // Status == Pending, so a Confirmed row should never even reach the
        // aggregate; this test is the regression guard for that invariant.
        var category = await SeedCategoryAsync();
        var product = await SeedProductAsync(category, initialStock: 50);

        var confirmedId = await SeedReservationAsync(
            product,
            quantity: 4,
            expiresAt: DateTime.UtcNow.AddHours(-1),
            status: ReservationStatus.Confirmed);

        var sweeper = CreateSweeper(_factory.Services, batchSize: 200);

        // Act
        var expiredCount = await sweeper.SweepOnceAsync(CancellationToken.None);

        // Assert
        expiredCount.Should().Be(0);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var confirmed = await db.StockReservations.AsNoTracking().FirstAsync(r => r.Id == confirmedId);
        confirmed.Status.Should().Be(ReservationStatus.Confirmed);
        confirmed.ExpiredAt.Should().BeNull(
            "Expire() must be a no-op on a Confirmed row even if the deadline is past");
    }

    // ---------- helpers ----------

    private static ReservationSweeperService CreateSweeper(IServiceProvider rootSp, int batchSize)
    {
        // The hosted service is suppressed under Test so we manufacture an
        // instance directly. ActivatorUtilities resolves IServiceScopeFactory
        // and ILogger<T> from the DI graph; we hand-craft IOptions<T> so
        // each test can pin its own batch size.
        var scopeFactory = rootSp.GetRequiredService<IServiceScopeFactory>();
        var logger = rootSp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ReservationSweeperService>>();
        var options = Options.Create(new ReservationSweeperOptions
        {
            SweepInterval = TimeSpan.FromMinutes(1),
            BatchSize = batchSize,
        });
        return new ReservationSweeperService(scopeFactory, options, logger);
    }

    private async Task<Guid> SeedCategoryAsync()
    {
        var id = Guid.NewGuid();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var category = Category.Create($"Sweep-Cat-{id:N}", "test");
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return category.Id;
    }

    private async Task<Guid> SeedProductAsync(Guid categoryId, int initialStock)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var product = Product.Create($"Sweep-P-{Guid.NewGuid():N}", "x", 9.99m, categoryId);
        product.RestockTo(initialStock);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private async Task<Guid> SeedReservationAsync(
        Guid productId,
        int quantity,
        DateTime expiresAt,
        ReservationStatus status)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var itemsJson = JsonSerializer.Serialize(new[]
        {
            new StockReservationItem
            {
                ProductId = productId,
                ProductName = "P",
                Quantity = quantity,
            },
        });

        StockReservation reservation;
        if (status == ReservationStatus.Confirmed)
        {
            reservation = StockReservation.CreateConfirmed(
                orderId: Guid.NewGuid(),
                sagaId: Guid.NewGuid(),
                userId: "u",
                itemsJson: itemsJson);
            ForceExpiresAt(reservation, expiresAt);
        }
        else
        {
            // Pending — Create() sets ExpiresAt = UtcNow + ttl. We need to
            // simulate clock-skip without Task.Delay, so override the column
            // afterwards.
            reservation = StockReservation.Create("u", itemsJson, TimeSpan.FromMinutes(15));
            ForceExpiresAt(reservation, expiresAt);
        }

        db.StockReservations.Add(reservation);
        await db.SaveChangesAsync();
        return reservation.Id;
    }

    private async Task ResetReservationsAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.StockReservations.ExecuteDeleteAsync();
    }

    /// <summary>
    /// The aggregate's <c>ExpiresAt</c> setter is private, but EF still
    /// reads/writes the backing column. Reflect to set it on the seed entity
    /// so a row can be born already-expired without sleeping.
    /// </summary>
    private static void ForceExpiresAt(StockReservation reservation, DateTime expiresAt)
    {
        var prop = typeof(StockReservation).GetProperty(
            nameof(StockReservation.ExpiresAt),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        prop!.SetValue(reservation, expiresAt);
    }
}
