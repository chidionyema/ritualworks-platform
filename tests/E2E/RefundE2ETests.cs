using Microsoft.Playwright;
using Xunit;
using System.Text.Json;
using Haworks.Tests.E2E;
using FluentAssertions;

namespace Haworks.Tests.E2E;

[Collection("E2E Tests")]
public class RefundE2ETests(E2EEnvironmentFixture fixture)
{
    [Fact]
    public async Task Refund_Journey_Should_Complete_End_To_End()
    {
        // 1. Arrange: Auth & Product Setup
        var apiContext = await fixture.CreateApiContextAsync();
        var username = $"refund_e2e_{Guid.NewGuid():N}";
        
        await apiContext.PostAsync("/api/Authentication/register", new()
        {
            DataObject = new { username, email = $"{username}@example.com", password = "Password123!" }
        });

        var csrfResponse = await apiContext.GetAsync("/api/Authentication/csrf-token");
        var csrfData = await csrfResponse.JsonAsync();
        var headers = new Dictionary<string, string> { { csrfData?.GetProperty("headerName").GetString()!, csrfData?.GetProperty("token").GetString()! } };

        // Create Category & Product
        var catResp = await apiContext.PostAsync("/api/Categories", new() { Headers = headers, DataObject = new { name = "E2E Category" } });
        var categoryId = (await catResp.JsonAsync())?.GetProperty("id").GetGuid();

        var prodResp = await apiContext.PostAsync("/api/Products", new() { 
            Headers = headers, 
            DataObject = new { name = "Refundable Item", unitPrice = 50.00m, categoryId, initialStock = 100 } 
        });
        var productId = (await prodResp.JsonAsync())?.GetProperty("id").GetGuid();

        // 2. Act: Complete a Purchase
        var checkoutResp = await apiContext.PostAsync("/api/Checkout", new() {
            Headers = headers,
            DataObject = new { 
                userId = username, 
                customerEmail = $"{username}@example.com", 
                totalAmount = 50.00m,
                items = new[] { new { productId, productName = "Refundable Item", quantity = 1, unitPrice = 50.00m } }
            }
        });
        checkoutResp.Status.Should().Be(202);

        // Poll for Payment Completion (Saga sync)
        Guid? paymentId = null;
        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(1000);
            var paymentsResp = await apiContext.GetAsync($"/api/Admin/payments/user/{username}"); // Assuming admin endpoint
            if (paymentsResp.Ok)
            {
                var payments = await paymentsResp.JsonAsync();
                if (payments?.GetArrayLength() > 0)
                {
                    paymentId = payments.Value[0].GetProperty("id").GetGuid();
                    break;
                }
            }
        }

        paymentId.Should().NotBeNull("Checkout should have resulted in a payment record");

        // 3. Act: Request Refund
        var refundResp = await apiContext.PostAsync("/api/refunds", new() {
            Headers = headers,
            DataObject = new { paymentId, amount = 50.00m, currency = "USD", reason = "E2E Test" }
        });
        Assert.True(refundResp.Ok, $"Refund request failed: {refundResp.Status} {refundResp.StatusText}");
        var refundId = (await refundResp.JsonAsync())?.GetGuid();

        // 4. Assert: Poll Saga Status
        bool completed = false;
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(1000);
            var statusResp = await apiContext.GetAsync($"/api/refunds/{refundId}");
            if (statusResp.Ok)
            {
                var status = await statusResp.JsonAsync();
                if (status?.GetProperty("status").GetString() == "Refunded")
                {
                    completed = true;
                    break;
                }
            }
        }

        completed.Should().BeTrue("Refund Saga should reach 'Refunded' state");
    }
}
