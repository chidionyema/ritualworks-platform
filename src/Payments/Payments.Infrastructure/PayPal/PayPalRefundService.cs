using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Domain.Interfaces;
using Haworks.BuildingBlocks.Telemetry;
using Haworks.BuildingBlocks.Resilience;
using Haworks.Contracts.Payments;
using Haworks.Payments.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Polly;
using System.Net.Http.Json;
using System.Text.Json;

namespace Haworks.Payments.Infrastructure.PayPal;

/// <summary>
/// PayPal implementation of IRefundService.
/// Handles refund operations using the PayPal Refunds API.
/// </summary>
internal sealed class PayPalRefundService(
    IPayPalClientFactory clientFactory,
    IPaymentRepository paymentRepository,
    IDomainEventPublisher eventPublisher,
    IResiliencePolicyFactory resiliencePolicyFactory,
    ILogger<PayPalRefundService> logger,
    ITelemetryService telemetry) : IRefundService
{
    private readonly IAsyncPolicy _resiliencePolicy = 
        resiliencePolicyFactory.CreateCombinedPolicy(ResilienceOptions.PayPal);

    /// <inheritdoc />
    public async Task<RefundResult> CreateRefundAsync(
        RefundRequest request, 
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.TransactionId))
        {
            throw new ArgumentException("TransactionId is required for refund", nameof(request));
        }

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["TransactionId"] = request.TransactionId,
            ["AmountCents"] = request.AmountCents?.ToString() ?? "full",
            ["Provider"] = PaymentProvider.PayPal.ToString()
        });

        return await _resiliencePolicy.ExecuteAsync(async (ctx, token) =>
        {
            var client = await clientFactory.GetAuthenticatedClientAsync(token);
            
            var refundReq = new PayPalRefundRequest();
            if (request.Metadata != null && request.Metadata.TryGetValue("refund_id", out var sagaId))
            {
                refundReq.CustomId = sagaId;
            }

            if (request.AmountCents.HasValue)
            {
                refundReq.Amount = new PayPalRefundAmount 
                { 
                    CurrencyCode = request.Currency ?? "USD", 
                    Value = (request.AmountCents.Value / CheckoutConstants.CentMultiplier).ToString("F2") 
                };
            }
            
            if (!string.IsNullOrEmpty(request.Reason))
            {
                refundReq.NoteToPayer = request.Reason;
            }

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, PayPalEndpoints.RefundCapture(request.TransactionId))
            {
                Content = JsonContent.Create(refundReq, options: PayPalJsonOptions.Default)
            };

            if (!string.IsNullOrEmpty(request.IdempotencyKey))
            {
                httpRequest.Headers.Add("PayPal-Request-Id", request.IdempotencyKey);
            }

            try
            {
                logger.LogInformation("Creating PayPal refund for capture {TransactionId}", request.TransactionId);

                var response = await client.SendAsync(httpRequest, token);
                var responseBody = await response.Content.ReadAsStringAsync(token);

                if (!response.IsSuccessStatusCode)
                {
                    var errorMessage = TryParsePayPalError(responseBody) ?? responseBody;
                    logger.LogError("PayPal refund failed: {Error}", errorMessage);
                    
                    return new RefundResult 
                    { 
                        RefundId = string.Empty, 
                        Status = RefundStatus.Failed, 
                        AmountCents = request.AmountCents ?? 0,
                        FailureReason = errorMessage,
                        Provider = PaymentProvider.PayPal 
                    };
                }

                var refund = JsonSerializer.Deserialize<PayPalRefundResponse>(responseBody, PayPalJsonOptions.Default);
                var payment = await paymentRepository.GetByProviderTransactionIdAsync(request.TransactionId, token);
                
                var status = MapRefundStatus(refund?.Status);

                if (payment != null && status == RefundStatus.Succeeded)
                {
                    await eventPublisher.PublishAsync(new RefundIssuedEvent 
                    { 
                        PaymentId = payment.Id, 
                        OrderId = payment.OrderId, 
                        RefundId = refund!.Id!, 
                        AmountCents = request.AmountCents ?? (long)(payment.Amount * CheckoutConstants.CentMultiplier), 
                        Currency = payment.Currency, 
                        Provider = PaymentProvider.PayPal,
                        Reason = request.Reason
                    }, token);
                }

                TrackRefundEvent("RefundCreated", refund?.Id ?? string.Empty, request.TransactionId, status);

                return new RefundResult 
                { 
                    RefundId = refund?.Id ?? string.Empty, 
                    Status = status, 
                    AmountCents = request.AmountCents ?? 0,
                    Provider = PaymentProvider.PayPal 
                };
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create PayPal refund for capture {TransactionId}", request.TransactionId);
                return new RefundResult 
                { 
                    RefundId = string.Empty, 
                    Status = RefundStatus.Failed, 
                    FailureReason = ex.Message,
                    Provider = PaymentProvider.PayPal 
                };
            }
        }, new Context(), ct);
    }

    /// <inheritdoc />
    public async Task<RefundResult> GetRefundStatusAsync(
        string refundId, 
        CancellationToken ct = default)
    {
        var client = await clientFactory.GetAuthenticatedClientAsync(ct);
        
        try
        {
            var response = await client.GetAsync(PayPalEndpoints.GetRefund(refundId), ct);
            if (!response.IsSuccessStatusCode)
            {
                return new RefundResult { RefundId = refundId, Status = RefundStatus.Failed, Provider = PaymentProvider.PayPal };
            }

            var refund = await response.Content.ReadFromJsonAsync<PayPalRefundResponse>(PayPalJsonOptions.Default, ct);
            return new RefundResult 
            { 
                RefundId = refund!.Id!, 
                Status = MapRefundStatus(refund.Status), 
                AmountCents = (long)(decimal.Parse(refund.Amount?.Value ?? "0") * CheckoutConstants.CentMultiplier),
                Provider = PaymentProvider.PayPal 
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving PayPal refund status for {RefundId}", refundId);
            return new RefundResult
            {
                RefundId = refundId,
                Status = RefundStatus.Failed,
                FailureReason = ex.Message,
                Provider = PaymentProvider.PayPal
            };
        }
    }

    private static RefundStatus MapRefundStatus(string? paypalStatus)
    {
        return paypalStatus?.ToUpperInvariant() switch
        {
            "COMPLETED" => RefundStatus.Succeeded,
            "PENDING" => RefundStatus.Pending,
            "FAILED" => RefundStatus.Failed,
            "CANCELLED" => RefundStatus.Canceled,
            _ => RefundStatus.Pending
        };
    }

    private static string? TryParsePayPalError(string responseBody)
    {
        try
        {
            var error = JsonSerializer.Deserialize<PayPalErrorResponse>(responseBody, PayPalJsonOptions.Default);
            return error?.Message ?? error?.Details?.FirstOrDefault()?.Description;
        }
        catch { return null; }
    }

    private void TrackRefundEvent(string eventName, string refundId, string transactionId, RefundStatus status)
    {
        telemetry.TrackEvent(eventName, new Dictionary<string, string>
        {
            ["Provider"] = PaymentProvider.PayPal.ToString(),
            ["RefundId"] = refundId,
            ["TransactionId"] = transactionId,
            ["Status"] = status.ToString()
        });
    }
}
