using FluentAssertions;
using Haworks.BuildingBlocks.Caching;
using Haworks.BuildingBlocks.Resilience;
using Haworks.Contracts.Payments;
using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Infrastructure.Stripe;
using Moq;
using Polly;
using Xunit;

namespace Haworks.Payments.Unit.Resilience;

/// <summary>
/// Stripe checkout-session calls go through the platform's combined
/// resilience policy (retry + circuit breaker + bulkhead, profile
/// <see cref="ResilienceOptions.Stripe"/>). Without this wrapping a
/// stuck Stripe API call hangs the saga consumer thread and a flapping
/// Stripe cascades into saga timeouts.
///
/// These tests drive the StripeCheckoutSessionService with a mocked
/// IStripeClientFactory that throws transient HTTP exceptions, and
/// verify the policy is actually applied (ExecuteAsync called, retries
/// fire, the right profile is used).
/// </summary>
public sealed class StripeCheckoutResilienceTests
{
    [Fact]
    public void Constructor_uses_Stripe_resilience_profile()
    {
        // The profile drives the retry count, CB threshold, bulkhead
        // limits — all dimensions of "how the service degrades under
        // pressure". Hardcoding the wrong profile (e.g. PayPal) gets
        // a quietly-different operational signature, so verify it
        // explicitly.
        var factory = new Mock<IResiliencePolicyFactory>();
        // Setup with explicit nulls for the optional parameters — Moq's
        // expression-tree compiler can't handle Setup() against methods
        // with optional args unless every argument is matched explicitly.
        factory.Setup(f => f.CreateCombinedPolicy(
                It.IsAny<ResilienceOptions>(),
                It.IsAny<Action<Exception, TimeSpan, int>?>(),
                It.IsAny<Action<Exception, TimeSpan>?>(),
                It.IsAny<Action?>()))
               .Returns(Policy.NoOpAsync());

        _ = new StripeCheckoutSessionService(
            Mock.Of<IStripeClientFactory>(),
            Mock.Of<IPaymentSessionCache>(),
            factory.Object);

        factory.Verify(
            f => f.CreateCombinedPolicy(
                It.Is<ResilienceOptions>(o => o.ServiceName == ResilienceOptions.Stripe.ServiceName),
                It.IsAny<Action<Exception, TimeSpan, int>?>(),
                It.IsAny<Action<Exception, TimeSpan>?>(),
                It.IsAny<Action?>()),
            Times.Once,
            "the service must request the dedicated Stripe resilience profile, not the generic Default");
    }

    [Fact]
    public async Task CreateSessionAsync_wraps_call_in_policy_and_retries_on_transient_failure()
    {
        // Capture how many times the wrapped delegate runs. Each retry
        // is a separate execution; the wrapping logic in the service
        // must hit ExecuteAsync, and the policy must invoke the
        // delegate enough times to satisfy the retry profile.
        var executions = 0;
        var clientFactory = new Mock<IStripeClientFactory>();
        clientFactory.Setup(f => f.GetClientAsync(It.IsAny<CancellationToken>()))
                     .Returns(() =>
                     {
                         executions++;
                         throw new HttpRequestException("simulated upstream Stripe blip");
                     });

        var policyFactory = new Mock<IResiliencePolicyFactory>();
        // Real retry policy with a low retry count so the test runs fast.
        // Default Stripe profile has 3 retries; we configure 2 here for
        // determinism in assertion math.
        policyFactory.Setup(f => f.CreateCombinedPolicy(
                It.IsAny<ResilienceOptions>(),
                It.IsAny<Action<Exception, TimeSpan, int>?>(),
                It.IsAny<Action<Exception, TimeSpan>?>(),
                It.IsAny<Action?>()))
                     .Returns(Policy.Handle<HttpRequestException>()
                                    .WaitAndRetryAsync(2, _ => TimeSpan.FromMilliseconds(10)));

        var sut = new StripeCheckoutSessionService(
            clientFactory.Object,
            Mock.Of<IPaymentSessionCache>(),
            policyFactory.Object);

        var act = async () => await sut.CreateSessionAsync(
            new CreateCheckoutSessionRequest
            {
                LineItems = Array.Empty<LineItem>(),
                SuccessUrl = "https://example.test/ok",
                CancelUrl = "https://example.test/cancel",
                CustomerEmail = "buyer@example.com",
                Metadata = new Dictionary<string, string>
                {
                    ["orderId"] = Guid.NewGuid().ToString(),
                    ["userId"] = "u1",
                },
                IdempotencyKey = "k",
            });

        // Final attempt still throws (we exhausted the retries) — that's
        // expected; we care that the policy *retried* before throwing.
        await act.Should().ThrowAsync<HttpRequestException>();

        executions.Should().Be(3,
            "policy gives 1 original + 2 retries = 3 total executions before surfacing the failure");
    }

    [Fact]
    public async Task GetSessionAsync_also_routes_through_policy()
    {
        // Same wrapping must apply to GetSessionAsync (used by webhook
        // verification + admin views). Mirror the retry assertion to
        // prove the wrapping isn't only on Create.
        var executions = 0;
        var clientFactory = new Mock<IStripeClientFactory>();
        clientFactory.Setup(f => f.GetClientAsync(It.IsAny<CancellationToken>()))
                     .Returns(() =>
                     {
                         executions++;
                         throw new HttpRequestException("transient");
                     });

        var policyFactory = new Mock<IResiliencePolicyFactory>();
        policyFactory.Setup(f => f.CreateCombinedPolicy(
                It.IsAny<ResilienceOptions>(),
                It.IsAny<Action<Exception, TimeSpan, int>?>(),
                It.IsAny<Action<Exception, TimeSpan>?>(),
                It.IsAny<Action?>()))
                     .Returns(Policy.Handle<HttpRequestException>()
                                    .WaitAndRetryAsync(1, _ => TimeSpan.FromMilliseconds(10)));

        var sut = new StripeCheckoutSessionService(
            clientFactory.Object,
            Mock.Of<IPaymentSessionCache>(),
            policyFactory.Object);

        var act = async () => await sut.GetSessionAsync("sess_test");
        await act.Should().ThrowAsync<HttpRequestException>();

        executions.Should().Be(2, "1 original + 1 retry = 2 executions");
    }
}
