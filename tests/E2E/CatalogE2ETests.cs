using Microsoft.AspNetCore.SignalR.Client;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;
using WireMock.Server;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit.Abstractions;

namespace Haworks.Tests.E2E;

[Collection("E2E Tests")]
public class CatalogE2ETests : IAsyncLifetime
{
    private readonly E2EEnvironmentFixture _fixture;
    private readonly ITestOutputHelper _output;
    private IAPIRequestContext _apiContext = null!;
    private WireMockServer _wireMock = null!;

    public CatalogE2ETests(E2EEnvironmentFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _apiContext = await _fixture.CreateApiContextAsync();
        // We reuse the same port as CheckoutE2ETests if they run sequentially, 
        // but E2E tests are often isolated.
        _wireMock = WireMockServer.Start(9091);
        
        _wireMock
            .Given(Request.Create().WithPath("/v1/checkout/sessions").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBodyAsJson(new { id = "cs_e2e_res_test", url = "https://checkout.stripe.com/e2e" }));
    }

    public async Task DisposeAsync()
    {
        await _apiContext.DisposeAsync();
        _wireMock.Stop();
        _wireMock.Dispose();
    }

    [Fact]
    public async Task Synchronous_Reservation_And_Confirm_Flow()
    {
        _output.WriteLine("--- STARTING E2E SYNC RESERVATION FLOW ---");

        // 1. Setup: Create Product
        var csrfResponse = await _apiContext.GetAsync("/api/Authentication/csrf-token");
        var csrfData = await csrfResponse.JsonAsync();
        var csrfToken = csrfData?.GetProperty("token").GetString();
        var csrfHeader = csrfData?.GetProperty("headerName").GetString();
        var headers = new Dictionary<string, string> { { csrfHeader!, csrfToken! } };

        var categoryResponse = await _apiContext.PostAsync("/api/Categories", new()
        {
            Headers = headers,
            DataObject = new { name = "Toys", description = "Testing" }
        });
        var category = await categoryResponse.JsonAsync();
        var categoryId = category?.GetProperty("id").GetGuid();

        var productResponse = await _apiContext.PostAsync("/api/Products", new()
        {
            Headers = headers,
            DataObject = new 
            { 
                name = "Lego Set", 
                description = "Classic", 
                unitPrice = 50.00m, 
                categoryId,
                initialStock = 10
            }
        });
        var product = await productResponse.JsonAsync();
        var productId = product?.GetProperty("id").GetGuid() ?? Guid.Empty;

        // 2. Step 1: Create Reservation (Anonymous)
        var reserveResponse = await _apiContext.PostAsync("/api/checkout/reservations", new()
        {
            Headers = headers,
            DataObject = new
            {
                items = new[] { new { productId, quantity = 2 } }
            }
        });
        reserveResponse.Status.Should().Be(201);
        var reserveData = await reserveResponse.JsonAsync();
        var reservationId = reserveData?.GetProperty("id").GetGuid();

        // 3. Step 2: Register (to get Auth Cookie for Confirm)
        var username = $"res_user_{Guid.NewGuid():N}";
        var email = $"{username}@example.com";
        var registerResponse = await _apiContext.PostAsync("/api/Authentication/register", new()
        {
            DataObject = new { username, email, password = "Password123!" }
        });
        registerResponse.Status.Should().Be(201);
        
        // 4. Step 3: Confirm Reservation
        var confirmResponse = await _apiContext.PostAsync($"/api/checkout/reservations/{reservationId}/confirm", new()
        {
            Headers = headers,
            DataObject = new { totalAmount = 100.00m, currency = "USD" }
        });
        confirmResponse.Status.Should().Be(200);
        var confirmData = await confirmResponse.JsonAsync();
        var sagaId = confirmData?.GetProperty("sagaId").GetGuid();

        // 5. Verification: Wait for CheckoutSaga to progress to Payment Session Created
        var hubUrl = $"{_fixture.BaseUrl}/hubs/checkout";
        var conn = new HubConnectionBuilder().WithUrl(hubUrl).Build();
        var tcs = new TaskCompletionSource<string>();
        conn.On<string, string>("OnPaymentSessionCreated", (id, url) => tcs.TrySetResult(url));
        await conn.StartAsync();
        await conn.InvokeAsync("SubscribeToSaga", sagaId.ToString());
        
        await Task.WhenAny(tcs.Task, Task.Delay(30000));
        tcs.Task.IsCompletedSuccessfully.Should().BeTrue("Confirmation should trigger the checkout saga");
        (await tcs.Task).Should().Be("https://checkout.stripe.com/e2e");

        await conn.StopAsync();
    }
}
