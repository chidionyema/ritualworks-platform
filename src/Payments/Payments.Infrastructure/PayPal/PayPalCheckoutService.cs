using Haworks.Payments.Application.Interfaces;
using Haworks.Contracts.Payments;
using Haworks.BuildingBlocks.Resilience;
using Haworks.Payments.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Polly;
using System.Net.Http.Json;
using System.Text.Json;

namespace Haworks.Payments.Infrastructure.PayPal;

/// <summary>
/// PayPal implementation of ICheckoutSessionService.
/// Uses PayPal Orders API v2 for checkout session management.
/// </summary>
internal sealed class PayPalCheckoutService(
    IPayPalClientFactory clientFactory,
    IResiliencePolicyFactory resiliencePolicyFactory,
    ILogger<PayPalCheckoutService> logger) : ICheckoutSessionService, ISubscriptionService
{
    private readonly IAsyncPolicy _resiliencePolicy = 
        resiliencePolicyFactory.CreateCombinedPolicy(ResilienceOptions.PayPal);

    /// <inheritdoc />
    public async Task<CheckoutSessionResult> CreateSessionAsync(
        CreateCheckoutSessionRequest request, 
        CancellationToken ct = default)
    {
        var totalCents = request.LineItems.Sum(i => i.UnitAmountCents * i.Quantity);
        if (totalCents <= 0)
        {
            throw new ArgumentException("Total amount must be greater than zero", nameof(request));
        }

        return await _resiliencePolicy.ExecuteAsync(async (ctx, token) =>
        {
            var client = await clientFactory.GetAuthenticatedClientAsync(token);

            var currency = !string.IsNullOrEmpty(request.Currency)
                ? request.Currency.ToUpperInvariant()
                : request.LineItems.Count > 0 ? request.LineItems[0].Currency?.ToUpperInvariant() ?? "USD" : "USD";

            var orderRequest = new PayPalOrderRequest
            {
                Intent = "CAPTURE",
                PurchaseUnits = new List<PayPalPurchaseUnit>
                {
                    new PayPalPurchaseUnit
                    {
                        ReferenceId = request.IdempotencyKey ?? Guid.NewGuid().ToString(),
                        Amount = new PayPalAmount
                        {
                            CurrencyCode = currency,
                            Value = FormatAmount(totalCents)
                        },
                        Description = string.Join(", ", request.LineItems.Take(3).Select(i => i.Name)),
                        CustomId = request.Metadata?.GetValueOrDefault("orderId") ?? string.Empty
                    }
                },
                ApplicationContext = new PayPalApplicationContext
                {
                    ReturnUrl = request.SuccessUrl,
                    CancelUrl = request.CancelUrl,
                    BrandName = "RitualWorks",
                    UserAction = "PAY_NOW"
                }
            };

            logger.LogInformation(
                "Creating PayPal order with idempotency key {Key}",
                request.IdempotencyKey);

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, PayPalEndpoints.CheckoutOrders)
            {
                Content = JsonContent.Create(orderRequest, options: PayPalJsonOptions.Default)
            };

            if (!string.IsNullOrEmpty(request.IdempotencyKey))
            {
                httpRequest.Headers.Add("PayPal-Request-Id", request.IdempotencyKey);
            }

            var response = await client.SendAsync(httpRequest, token);
            var responseBody = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
            {
                var errorMessage = TryParsePayPalError(responseBody) ?? responseBody;
                logger.LogError("PayPal order creation failed: {Error}", errorMessage);
                throw new HttpRequestException($"PayPal order failed: {response.StatusCode} - {errorMessage}");
            }

            var order = JsonSerializer.Deserialize<PayPalOrder>(responseBody, PayPalJsonOptions.Default);
            var approvalLink = order?.Links?.FirstOrDefault(l => l.Rel == "approve");

            return new CheckoutSessionResult
            {
                SessionId = order!.Id!,
                SessionUrl = approvalLink?.Href ?? string.Empty,
                TransactionId = order.Id,
                Provider = PaymentProvider.PayPal
            };
        }, new Context(), ct);
    }

    /// <inheritdoc />
    public async Task<CheckoutSessionResult> CreateSubscriptionSessionAsync(
        CreateSubscriptionSessionRequest request, 
        CancellationToken ct = default)
    {
        return await _resiliencePolicy.ExecuteAsync(async (ctx, token) =>
        {
            var client = await clientFactory.GetAuthenticatedClientAsync(token);

            var subRequest = new PayPalSubscriptionRequest
            {
                PlanId = request.PlanId,
                ApplicationContext = new PayPalApplicationContext
                {
                    ReturnUrl = request.SuccessUrl,
                    CancelUrl = request.CancelUrl,
                    BrandName = "RitualWorks",
                    UserAction = "SUBSCRIBE_NOW"
                }
            };

            if (!string.IsNullOrEmpty(request.CustomerEmail))
            {
                subRequest.Subscriber = new PayPalSubscriber { EmailAddress = request.CustomerEmail };
            }

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, PayPalEndpoints.Subscriptions)
            {
                Content = JsonContent.Create(subRequest, options: PayPalJsonOptions.Default)
            };

            if (!string.IsNullOrEmpty(request.IdempotencyKey))
            {
                httpRequest.Headers.Add("PayPal-Request-Id", request.IdempotencyKey);
            }

            var response = await client.SendAsync(httpRequest, token);
            var responseBody = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("PayPal subscription creation failed: {Body}", responseBody);
                throw new HttpRequestException($"PayPal sub failed: {response.StatusCode}");
            }

            var sub = JsonSerializer.Deserialize<PayPalSubscription>(responseBody, PayPalJsonOptions.Default);
            var approvalLink = sub?.Links?.FirstOrDefault(l => l.Rel == "approve");

            return new CheckoutSessionResult
            {
                SessionId = sub!.Id!,
                SessionUrl = approvalLink?.Href ?? string.Empty,
                TransactionId = sub.Id,
                Provider = PaymentProvider.PayPal
            };
        }, new Context(), ct);
    }

    /// <inheritdoc />
    public async Task<CheckoutSession?> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;

        return await _resiliencePolicy.ExecuteAsync(async (ctx, token) =>
        {
            var client = await clientFactory.GetAuthenticatedClientAsync(token);
            var response = await client.GetAsync(PayPalEndpoints.GetOrder(sessionId), token);
            
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) return null;

            var order = await response.Content.ReadFromJsonAsync<PayPalOrder>(PayPalJsonOptions.Default, token);
            if (order == null) return null;

            var amountTotal = order.PurchaseUnits?
                .Sum(pu => decimal.TryParse(pu.Amount?.Value, out var val) ? val : 0) ?? 0;

            return new CheckoutSession
            {
                SessionId = order.Id ?? string.Empty,
                Status = MapOrderStatus(order.Status),
                TransactionId = order.PurchaseUnits?.FirstOrDefault()?.Payments?.Captures?.FirstOrDefault()?.Id ?? order.Id,
                CustomerId = order.Payer?.PayerId,
                AmountTotal = (long)Math.Round(amountTotal * CheckoutConstants.CentMultiplier),
                Currency = order.PurchaseUnits?.FirstOrDefault()?.Amount?.CurrencyCode ?? "USD",
                Provider = PaymentProvider.PayPal,
                Metadata = new Dictionary<string, string>
                {
                    ["orderId"] = order.PurchaseUnits?.FirstOrDefault()?.CustomId ?? string.Empty
                }
            };
        }, new Context(), ct);
    }

    /// <inheritdoc />
    public Task<bool> ExpireSessionAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(true);

    private static string FormatAmount(long amountCents) => 
        (amountCents / CheckoutConstants.CentMultiplier).ToString("F2");

    private static SessionStatus MapOrderStatus(string? status) => status?.ToUpperInvariant() switch
    {
        PayPalOrderStatuses.Created or PayPalOrderStatuses.Approved => SessionStatus.Open,
        PayPalOrderStatuses.Completed => SessionStatus.Complete,
        PayPalOrderStatuses.Voided => SessionStatus.Expired,
        _ => SessionStatus.Open
    };

    private static string? TryParsePayPalError(string responseBody)
    {
        try
        {
            var error = JsonSerializer.Deserialize<PayPalErrorResponse>(responseBody, PayPalJsonOptions.Default);
            return error?.Message ?? error?.Details?.FirstOrDefault()?.Description;
        }
        catch { return null; }
    }
}
