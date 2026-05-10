using FluentAssertions;
using Haworks.Catalog.Domain;
using Xunit;

namespace Haworks.Catalog.Unit.Domain;

/// <summary>
/// Lifecycle tests for B1's <see cref="StockReservation"/> aggregate. The
/// transition table the aggregate must enforce:
///
///   Pending  ─Confirm()─►  Confirmed   (idempotent: false on re-call)
///       │
///       └─Expire()──►      Expired     (idempotent: false on re-call)
///
/// These tests are pure-domain (no EF, no DI) so they live in the
/// Catalog.Unit project alongside ProductTests.
/// </summary>
public class StockReservationTests
{
    private const string UserId = "user-1";
    private const string ItemsJson = "[{\"productId\":\"00000000-0000-0000-0000-000000000001\",\"quantity\":2}]";
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);

    [Fact]
    public void Create_starts_in_Pending_with_ExpiresAt_after_now()
    {
        var before = DateTime.UtcNow;

        var r = StockReservation.Create(UserId, ItemsJson, DefaultTtl);

        r.Status.Should().Be(ReservationStatus.Pending);
        r.UserId.Should().Be(UserId);
        r.ItemsJson.Should().Be(ItemsJson);
        r.OrderId.Should().BeNull();
        r.SagaId.Should().BeNull();
        r.ConfirmedAt.Should().BeNull();
        r.ExpiredAt.Should().BeNull();
        r.ReservedAt.Should().BeOnOrAfter(before);
        r.ExpiresAt.Should().BeAfter(r.ReservedAt);
        (r.ExpiresAt - r.ReservedAt).Should().BeCloseTo(DefaultTtl, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Confirm_transitions_Pending_to_Confirmed()
    {
        var r = StockReservation.Create(UserId, ItemsJson, DefaultTtl);
        var orderId = Guid.NewGuid();
        var sagaId = Guid.NewGuid();

        var ok = r.Confirm(orderId, sagaId);

        ok.Should().BeTrue();
        r.Status.Should().Be(ReservationStatus.Confirmed);
        r.OrderId.Should().Be(orderId);
        r.SagaId.Should().Be(sagaId);
        r.ConfirmedAt.Should().NotBeNull();
    }

    [Fact]
    public void Confirm_returns_false_if_already_confirmed()
    {
        var r = StockReservation.Create(UserId, ItemsJson, DefaultTtl);
        var firstOrderId = Guid.NewGuid();
        r.Confirm(firstOrderId, Guid.NewGuid()).Should().BeTrue();

        // Second call must be a no-op so a duplicate Confirm doesn't
        // overwrite the original orderId/sagaId.
        var second = r.Confirm(Guid.NewGuid(), Guid.NewGuid());

        second.Should().BeFalse();
        r.OrderId.Should().Be(firstOrderId);
    }

    [Fact]
    public void Confirm_returns_false_if_already_expired()
    {
        var r = StockReservation.Create(UserId, ItemsJson, DefaultTtl);
        r.Expire().Should().BeTrue();

        var ok = r.Confirm(Guid.NewGuid(), Guid.NewGuid());

        ok.Should().BeFalse();
        r.Status.Should().Be(ReservationStatus.Expired);
    }

    [Fact]
    public void Expire_transitions_Pending_to_Expired()
    {
        var r = StockReservation.Create(UserId, ItemsJson, DefaultTtl);

        var ok = r.Expire();

        ok.Should().BeTrue();
        r.Status.Should().Be(ReservationStatus.Expired);
        r.ExpiredAt.Should().NotBeNull();
    }

    [Fact]
    public void Expire_returns_false_if_confirmed()
    {
        var r = StockReservation.Create(UserId, ItemsJson, DefaultTtl);
        r.Confirm(Guid.NewGuid(), Guid.NewGuid()).Should().BeTrue();

        var ok = r.Expire();

        ok.Should().BeFalse();
        r.Status.Should().Be(ReservationStatus.Confirmed);
        r.ExpiredAt.Should().BeNull();
    }

    [Fact]
    public void CreateConfirmed_starts_in_Confirmed_with_orderId_set()
    {
        var orderId = Guid.NewGuid();
        var sagaId = Guid.NewGuid();

        var r = StockReservation.CreateConfirmed(orderId, sagaId, UserId, ItemsJson);

        r.Status.Should().Be(ReservationStatus.Confirmed);
        r.OrderId.Should().Be(orderId);
        r.SagaId.Should().Be(sagaId);
        r.UserId.Should().Be(UserId);
        r.ItemsJson.Should().Be(ItemsJson);
        r.ConfirmedAt.Should().NotBeNull();
        // ExpiresAt is set far in the future so the sweeper never targets
        // saga-path reservations.
        r.ExpiresAt.Should().BeAfter(DateTime.UtcNow.AddDays(30));
    }
}
