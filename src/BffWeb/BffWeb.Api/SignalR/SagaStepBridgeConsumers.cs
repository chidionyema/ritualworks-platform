// SignalR bridge consumers — translate saga events to SignalR pushes. No resource acquisition.
using Haworks.BffWeb.Application.Interfaces;
using Haworks.Contracts.Catalog;
using Haworks.Contracts.Payments;
using MassTransit;

namespace Haworks.BffWeb.Api.SignalR;

// Consumers that bridge the saga's MT events to SignalR OnSagaStep notifications
// for the portfolio site's checkout demo. One consumer per logical step in
// the saga's state machine — the SagaId field on each event maps directly to
// the demo session id, so the SignalR group routing is just
// `demo-{SagaId}` (handled by SignalRDemoHubNotifier).
//
// Step naming + ProgressPercent values are coordinated with the frontend's
// CheckoutDemo.tsx — keep them in sync. Five-stage ladder:
//   stock_reserved          30%
//   payment_session_created 60%
//   payment_completed      100% (success terminal)
//   stock_failed           100% (failure terminal)
//   payment_failed         100% (failure terminal)
//
// PaymentAmountMismatchEvent is NOT bridged here because its payload lacks
// SagaId — the mismatch handler would need to look up the saga by OrderId,
// which means querying checkout-orchestrator. Tracked as follow-up.

public sealed class StockReservedSagaBridge(IDemoHubNotifier notifier, ILogger<StockReservedSagaBridge> logger)
    : IConsumer<StockReservedEvent>
{
    public Task Consume(ConsumeContext<StockReservedEvent> ctx)
    {
        logger.LogDebug("Bridging StockReserved -> OnSagaStep for saga {SagaId}", ctx.Message.SagaId);
        return notifier.NotifySagaStepAsync(new SagaStepEvent(
            SessionId: ctx.Message.SagaId,
            Step: "stock_reserved",
            Service: "catalog-svc",
            Status: "completed",
            Description: "Stock reserved",
            ProgressPercent: 30,
            Timestamp: DateTime.UtcNow), ctx.CancellationToken);
    }
}

public sealed class StockReservationFailedSagaBridge(IDemoHubNotifier notifier, ILogger<StockReservationFailedSagaBridge> logger)
    : IConsumer<StockReservationFailedEvent>
{
    public Task Consume(ConsumeContext<StockReservationFailedEvent> ctx)
    {
        logger.LogInformation("Bridging StockReservationFailed -> OnSagaStep for saga {SagaId}", ctx.Message.SagaId);
        return notifier.NotifySagaStepAsync(new SagaStepEvent(
            SessionId: ctx.Message.SagaId,
            Step: "stock_failed",
            Service: "catalog-svc",
            Status: "failed",
            Description: ctx.Message.Reason ?? "Insufficient stock",
            ProgressPercent: 100,
            Timestamp: DateTime.UtcNow), ctx.CancellationToken);
    }
}

public sealed class PaymentSessionCreatedSagaBridge(IDemoHubNotifier notifier, ILogger<PaymentSessionCreatedSagaBridge> logger)
    : IConsumer<PaymentSessionCreatedEvent>
{
    public Task Consume(ConsumeContext<PaymentSessionCreatedEvent> ctx)
    {
        logger.LogDebug("Bridging PaymentSessionCreated -> OnSagaStep for saga {SagaId}", ctx.Message.SagaId);
        return notifier.NotifySagaStepAsync(new SagaStepEvent(
            SessionId: ctx.Message.SagaId,
            Step: "payment_session_created",
            Service: "payments-svc",
            Status: "completed",
            Description: $"Payment session created via {ctx.Message.Provider}",
            ProgressPercent: 60,
            Timestamp: DateTime.UtcNow), ctx.CancellationToken);
    }
}

public sealed class PaymentSessionFailedSagaBridge(IDemoHubNotifier notifier, ILogger<PaymentSessionFailedSagaBridge> logger)
    : IConsumer<PaymentSessionFailedEvent>
{
    public Task Consume(ConsumeContext<PaymentSessionFailedEvent> ctx)
    {
        logger.LogInformation("Bridging PaymentSessionFailed -> OnSagaStep for saga {SagaId}", ctx.Message.SagaId);
        return notifier.NotifySagaStepAsync(new SagaStepEvent(
            SessionId: ctx.Message.SagaId,
            Step: "payment_failed",
            Service: "payments-svc",
            Status: "failed",
            Description: ctx.Message.ErrorMessage ?? "Payment session failed",
            ProgressPercent: 100,
            Timestamp: DateTime.UtcNow), ctx.CancellationToken);
    }
}

public sealed class PaymentCompletedSagaBridge(IDemoHubNotifier notifier, ILogger<PaymentCompletedSagaBridge> logger)
    : IConsumer<PaymentCompletedEvent>
{
    public Task Consume(ConsumeContext<PaymentCompletedEvent> ctx)
    {
        logger.LogInformation("Bridging PaymentCompleted -> OnSagaStep for saga {SagaId}", ctx.Message.SagaId);
        return notifier.NotifySagaStepAsync(new SagaStepEvent(
            SessionId: ctx.Message.SagaId,
            Step: "payment_completed",
            Service: "payments-svc",
            Status: "completed",
            Description: $"Payment completed: {ctx.Message.Amount} {ctx.Message.Currency}",
            ProgressPercent: 100,
            Timestamp: DateTime.UtcNow), ctx.CancellationToken);
    }
}


public sealed class PaymentAmountMismatchSagaBridge(
    IDemoHubNotifier notifier, 
    IHttpClientFactory httpClientFactory,
    ILogger<PaymentAmountMismatchSagaBridge> logger)
    : IConsumer<PaymentAmountMismatchEvent>
{
    public async Task Consume(ConsumeContext<PaymentAmountMismatchEvent> ctx)
    {
        logger.LogWarning("Bridging PaymentAmountMismatchEvent for order {OrderId}", ctx.Message.OrderId);
        
        var client = httpClientFactory.CreateClient(Haworks.BffWeb.Api.BackendClients.Checkout);
        try
        {
            using var resp = await client.GetAsync($"/api/checkouts/by-order/{ctx.Message.OrderId}", ctx.CancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                var body = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<System.Text.Json.JsonElement>(resp.Content, cancellationToken: ctx.CancellationToken);
                var sagaId = body.GetProperty("sagaId").GetGuid();
                
                await notifier.NotifySagaStepAsync(new SagaStepEvent(
                    SessionId: sagaId,
                    Step: "payment_failed",
                    Service: "payments-svc",
                    Status: "failed",
                    Description: $"Payment amount mismatch: Expected {ctx.Message.ExpectedTotal}, received {ctx.Message.ActualPaid}",
                    ProgressPercent: 70,
                    Timestamp: DateTime.UtcNow), ctx.CancellationToken);
            }
            else
            {
                logger.LogWarning("Failed to lookup saga for order {OrderId}: {StatusCode}", ctx.Message.OrderId, resp.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error looking up saga for order {OrderId}", ctx.Message.OrderId);
        }
    }
}
