using FluentAssertions;
using Haworks.Contracts.Payments;
using PactNet;
using Xunit;
using Xunit.Abstractions;

namespace Haworks.Payments.Contract;

/// <summary>
/// CONSUMER-SIDE Pact contracts for the events payments-svc publishes:
///   • PaymentCompletedEvent      (orders-svc consumer in Phase 4)
///   • PaymentSessionFailedEvent  (orders-svc + checkout-svc compensation)
///   • PaymentAmountMismatchEvent (orders-svc -> Order.RequiresReview)
///
/// These pacts pin the wire shape that downstream consumers depend on.
/// CI publishes them to the Pact Broker so payments-svc can't deploy a
/// breaking schema change without can-i-deploy catching it.
/// </summary>
public sealed class PaymentEventsConsumerTests
{
    private readonly IMessagePactBuilderV4 _pact;

    public PaymentEventsConsumerTests(ITestOutputHelper output)
    {
        var config = new PactConfig
        {
            PactDir = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "pacts"),
            DefaultJsonSettings = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            },
        };

        _pact = Pact.V4("ConsumerOfPayments", "payments-svc", config).WithMessageInteractions();
    }

    [Fact]
    public async Task PaymentCompletedEvent_carries_payment_order_saga_amount_currency_provider_and_optional_txref()
    {
        await _pact
            .ExpectsToReceive("a PaymentCompletedEvent for a Stripe-completed payment")
            .Given("payment 11111111-... for orderId 22222222-... was just marked Completed by the webhook consumer")
            .WithJsonContent(new
            {
                eventId              = "00000000-0000-0000-0000-000000000000",
                occurredAt           = "2026-05-03T12:00:00Z",
                paymentId            = "11111111-1111-1111-1111-111111111111",
                orderId              = "22222222-2222-2222-2222-222222222222",
                sagaId               = "33333333-3333-3333-3333-333333333333",
                amount               = 50.00m,
                currency             = "USD",
                provider             = "Stripe",
                transactionReference = "pi_abc",
            })
            .VerifyAsync<PaymentCompletedEvent>(evt =>
            {
                evt.Should().NotBeNull();
                evt.PaymentId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
                evt.OrderId.Should().Be(Guid.Parse("22222222-2222-2222-2222-222222222222"));
                evt.SagaId.Should().Be(Guid.Parse("33333333-3333-3333-3333-333333333333"));
                evt.Amount.Should().Be(50.00m);
                evt.Currency.Should().Be("USD");
                evt.Provider.Should().Be("Stripe");
                evt.TransactionReference.Should().Be("pi_abc");
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task PaymentSessionFailedEvent_carries_order_saga_provider_errorCode_message_attempt_finalflag()
    {
        await _pact
            .ExpectsToReceive("a PaymentSessionFailedEvent after Stripe rejected the session")
            .Given("payment session for orderId 44444444-... failed terminally on Stripe")
            .WithJsonContent(new
            {
                eventId        = "00000000-0000-0000-0000-000000000000",
                occurredAt     = "2026-05-03T12:00:00Z",
                orderId        = "44444444-4444-4444-4444-444444444444",
                sagaId         = "55555555-5555-5555-5555-555555555555",
                provider       = "Stripe",
                errorCode      = "payment_intent.payment_failed",
                errorMessage   = "Stripe reported payment_intent.payment_failed for session sess_x",
                attemptNumber  = 1,
                isFinalAttempt = true,
            })
            .VerifyAsync<PaymentSessionFailedEvent>(evt =>
            {
                evt.OrderId.Should().Be(Guid.Parse("44444444-4444-4444-4444-444444444444"));
                evt.SagaId.Should().Be(Guid.Parse("55555555-5555-5555-5555-555555555555"));
                evt.Provider.Should().Be("Stripe");
                evt.ErrorCode.Should().Be("payment_intent.payment_failed");
                evt.IsFinalAttempt.Should().BeTrue();
                evt.AttemptNumber.Should().Be(1);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task PaymentAmountMismatchEvent_carries_payment_order_provider_actualpaid_expectedtotal_difference_reason()
    {
        await _pact
            .ExpectsToReceive("a PaymentAmountMismatchEvent when Stripe captured a different amount than authorized")
            .Given("payment 66666666-... was authorized for 50.00 USD but Stripe captured 75.00 USD")
            .WithJsonContent(new
            {
                eventId       = "00000000-0000-0000-0000-000000000000",
                occurredAt    = "2026-05-03T12:00:00Z",
                paymentId     = "66666666-6666-6666-6666-666666666666",
                orderId       = "77777777-7777-7777-7777-777777777777",
                provider      = "Stripe",
                actualPaid    = 75.00m,
                expectedTotal = 50.00m,
                difference    = 25.00m,
                reason        = "Stripe captured 75.00 USD; expected 50.00 USD",
            })
            .VerifyAsync<PaymentAmountMismatchEvent>(evt =>
            {
                evt.PaymentId.Should().Be(Guid.Parse("66666666-6666-6666-6666-666666666666"));
                evt.OrderId.Should().Be(Guid.Parse("77777777-7777-7777-7777-777777777777"));
                evt.Provider.Should().Be("Stripe");
                evt.ActualPaid.Should().Be(75.00m);
                evt.ExpectedTotal.Should().Be(50.00m);
                evt.Difference.Should().Be(25.00m);
                evt.Reason.Should().NotBeNullOrEmpty();
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task RefundIssuedEvent_carries_payment_order_refundId_amount_currency_provider_reason()
    {
        await _pact
            .ExpectsToReceive("a RefundIssuedEvent for a Stripe refund")
            .Given("refund re_123 for payment 11111111-... was just issued")
            .WithJsonContent(new
            {
                eventId       = "00000000-0000-0000-0000-000000000000",
                occurredAt    = "2026-05-03T12:00:00Z",
                paymentId     = "11111111-1111-1111-1111-111111111111",
                orderId       = "22222222-2222-2222-2222-222222222222",
                refundId      = "re_123",
                amountCents   = 5000,
                currency      = "USD",
                provider      = "Stripe",
                reason        = "Requested by customer",
                issuedAt      = "2026-05-03T12:00:00Z",
            })
            .VerifyAsync<RefundIssuedEvent>(evt =>
            {
                evt.PaymentId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
                evt.RefundId.Should().Be("re_123");
                evt.AmountCents.Should().Be(5000);
                evt.Provider.Should().Be(PaymentProvider.Stripe);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task SubscriptionStartedEvent_carries_subscriptionId_userId_planId_provider_periodEnd()
    {
        await _pact
            .ExpectsToReceive("a SubscriptionStartedEvent for a new Stripe subscription")
            .Given("subscription sub_abc for user user_123 was just started")
            .WithJsonContent(new
            {
                eventId          = "00000000-0000-0000-0000-000000000000",
                occurredAt       = "2026-05-03T12:00:00Z",
                subscriptionId   = "sub_abc",
                userId           = "user_123",
                planId           = "plan_99",
                provider         = "Stripe",
                currentPeriodEnd = "2026-06-03T12:00:00Z",
                startedAt        = "2026-05-03T12:00:00Z",
            })
            .VerifyAsync<SubscriptionStartedEvent>(evt =>
            {
                evt.SubscriptionId.Should().Be("sub_abc");
                evt.UserId.Should().Be("user_123");
                evt.PlanId.Should().Be("plan_99");
                evt.Provider.Should().Be(PaymentProvider.Stripe);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task SubscriptionRenewedEvent_carries_subscriptionId_userId_provider_amount_currency_periodEnd()
    {
        await _pact
            .ExpectsToReceive("a SubscriptionRenewedEvent for a Stripe renewal")
            .Given("subscription sub_abc was renewed for user user_123")
            .WithJsonContent(new
            {
                eventId          = "00000000-0000-0000-0000-000000000000",
                occurredAt       = "2026-05-03T12:00:00Z",
                subscriptionId   = "sub_abc",
                userId           = "user_123",
                provider         = "Stripe",
                amountCents      = 9900,
                currency         = "USD",
                newPeriodEnd     = "2026-07-03T12:00:00Z",
                renewedAt        = "2026-05-03T12:00:00Z",
            })
            .VerifyAsync<SubscriptionRenewedEvent>(evt =>
            {
                evt.SubscriptionId.Should().Be("sub_abc");
                evt.AmountCents.Should().Be(9900);
                evt.Provider.Should().Be(PaymentProvider.Stripe);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task SubscriptionCancelledEvent_carries_subscriptionId_userId_provider_reason()
    {
        await _pact
            .ExpectsToReceive("a SubscriptionCancelledEvent for a Stripe cancellation")
            .Given("subscription sub_abc for user user_123 was cancelled")
            .WithJsonContent(new
            {
                eventId        = "00000000-0000-0000-0000-000000000000",
                occurredAt     = "2026-05-03T12:00:00Z",
                subscriptionId = "sub_abc",
                userId         = "user_123",
                provider       = "Stripe",
                reason         = "User request",
                cancelledAt    = "2026-05-03T12:00:00Z",
            })
            .VerifyAsync<SubscriptionCancelledEvent>(evt =>
            {
                evt.SubscriptionId.Should().Be("sub_abc");
                evt.UserId.Should().Be("user_123");
                evt.Provider.Should().Be(PaymentProvider.Stripe);
                return Task.CompletedTask;
            });
    }
}
