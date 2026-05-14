using FluentAssertions;
using Haworks.Contracts.Payments;
using Haworks.Payments.Domain;
using Xunit;

namespace Haworks.Payments.Unit.Domain;

public class SubscriptionTests
{
    private readonly string _userId = "user-42";
    private readonly PaymentProvider _provider = PaymentProvider.Stripe;
    private readonly string _providerSubId = "sub_stripe_123";
    private readonly string _planId = "plan_pro";
    private readonly DateTime _startsAt = DateTime.UtcNow;
    private readonly DateTime _expiresAt = DateTime.UtcNow.AddDays(30);

    private Subscription CreateDefault() =>
        Subscription.Create(_userId, _provider, _providerSubId, _planId, _startsAt, _expiresAt);

    #region Create Validation

    [Fact]
    public void Create_with_valid_params_succeeds()
    {
        var sub = CreateDefault();
        sub.UserId.Should().Be(_userId);
        sub.Provider.Should().Be(_provider);
        sub.ProviderSubscriptionId.Should().Be(_providerSubId);
        sub.PlanId.Should().Be(_planId);
        sub.Status.Should().Be(SubscriptionStatus.Incomplete);
    }

    [Fact]
    public void Create_with_empty_userId_throws()
    {
        var act = () => Subscription.Create("", _provider, _providerSubId, _planId, _startsAt, _expiresAt);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_with_empty_providerSubscriptionId_throws()
    {
        var act = () => Subscription.Create(_userId, _provider, "", _planId, _startsAt, _expiresAt);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_with_empty_planId_throws()
    {
        var act = () => Subscription.Create(_userId, _provider, _providerSubId, "", _startsAt, _expiresAt);
        act.Should().Throw<ArgumentException>();
    }

    #endregion

    #region Lifecycle

    [Fact]
    public void Activate_sets_status_to_Active()
    {
        var sub = CreateDefault();
        sub.Activate();
        sub.Status.Should().Be(SubscriptionStatus.Active);
    }

    [Fact]
    public void Cancel_sets_status_and_canceledAt()
    {
        var sub = CreateDefault();
        sub.Activate();
        sub.Cancel();
        sub.Status.Should().Be(SubscriptionStatus.Canceled);
        sub.CanceledAt.Should().NotBeNull();
    }

    [Fact]
    public void IsActive_returns_false_when_expired()
    {
        var sub = Subscription.Create(
            _userId, _provider, _providerSubId, _planId,
            DateTime.UtcNow.AddDays(-60),
            DateTime.UtcNow.AddDays(-1));
        sub.Activate();
        sub.IsActive.Should().BeFalse();
    }

    [Fact]
    public void IsActive_returns_true_when_active_and_not_expired()
    {
        var sub = CreateDefault();
        sub.Activate();
        sub.IsActive.Should().BeTrue();
    }

    [Fact]
    public void SetExpiresAt_updates_expiry()
    {
        var sub = CreateDefault();
        var newExpiry = DateTime.UtcNow.AddDays(90);
        sub.SetExpiresAt(newExpiry);
        sub.ExpiresAt.Should().Be(newExpiry);
    }

    #endregion
}
