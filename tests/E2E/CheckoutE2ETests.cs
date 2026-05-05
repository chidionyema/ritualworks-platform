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
public class CheckoutE2ETests : IAsyncLifetime
{
    private readonly E2EEnvironmentFixture _fixture;
    private readonly ITestOutputHelper _output;
    private IAPIRequestContext _apiContext = null!;
    private WireMockServer _wireMock = null!;

    public CheckoutE2ETests(E2EEnvironmentFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _apiContext = await _fixture.CreateApiContextAsync();
        _wireMock = WireMockServer.Start(9091);
        
        _wireMock
            .Given(Request.Create().WithPath("/v1/checkout/sessions").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBodyAsJson(new { id = "cs_e2e_test", url = "https://checkout.stripe.com/e2e" }));
    }

    public async Task DisposeAsync()
    {
        await _apiContext.DisposeAsync();
        _wireMock.Stop();
        _wireMock.Dispose();
    }

    [Fact]
    public async Task HappyPath_Checkout_Completes()
    {
        _output.WriteLine("--- STARTING E2E HAPPY PATH ---");

        // 1. Register
        var username = $"e2e_{Guid.NewGuid():N}";
        var email = $"{username}@example.com";
        var password = "Password123!";
        var registerResponse = await _apiContext.PostAsync("/api/Authentication/register", new APIRequestContextOptions 
        { 
            DataObject = new { username, email, password } 
        });
        registerResponse.Status.Should().Be(201);

        // 2. Get CSRF
        var csrfResponse = await _apiContext.GetAsync("/api/Authentication/csrf-token");
        var csrfData = await csrfResponse.JsonAsync();
        var csrfToken = csrfData?.GetProperty("token").GetString();
        var csrfHeader = csrfData?.GetProperty("headerName").GetString();

        // 3. Create Product (Catalog)
        var categoryResponse = await _apiContext.PostAsync("/api/Categories", new APIRequestContextOptions
        {
            Headers = new Dictionary<string, string> { { csrfHeader!, csrfToken! } },
            DataObject = new { name = "Electronics", description = "E2E Testing" }
        });
        var categoryData = await categoryResponse.JsonAsync();
        var categoryId = categoryData?.GetProperty("id").GetGuid() ?? Guid.Empty;

        var productResponse = await _apiContext.PostAsync("/api/Products", new APIRequestContextOptions
        {
            Headers = new Dictionary<string, string> { { csrfHeader!, csrfToken! } },
            DataObject = new 
            { 
                name = "E2E Product", 
                description = "Testing", 
                unitPrice = 100.00m, 
                categoryId,
                initialStock = 100
            }
        });
        var productData = await productResponse.JsonAsync();
        var productId = productData?.GetProperty("id").GetGuid() ?? Guid.Empty;

        // 4. Start Checkout (BffWeb)
        var checkoutResponse = await _apiContext.PostAsync("/api/Checkout", new APIRequestContextOptions
        {
            Headers = new Dictionary<string, string> { { csrfHeader!, csrfToken! } },
            DataObject = new 
            { 
                userId = username,
                customerEmail = email,
                totalAmount = 100.00m,
                items = new[] 
                { 
                    new { productId, productName = "E2E Product", quantity = 1, unitPrice = 100.00m } 
                } 
            }
        });
        checkoutResponse.Status.Should().Be(202);
        var result = await checkoutResponse.JsonAsync();
        var sagaId = result?.GetProperty("sagaId").GetGuid();

        // 5. Verify via SignalR
        var hubUrl = $"{_fixture.BaseUrl}/hubs/checkout";
        var conn = new HubConnectionBuilder().WithUrl(hubUrl).Build();
        var tcs = new TaskCompletionSource<string>();
        conn.On<string, string>("OnPaymentSessionCreated", (id, url) => tcs.TrySetResult(url));
        await conn.StartAsync();
        await conn.InvokeAsync("SubscribeToSaga", sagaId.ToString());
        
        await Task.WhenAny(tcs.Task, Task.Delay(30000));
        tcs.Task.IsCompletedSuccessfully.Should().BeTrue("Should receive payment URL via SignalR");
        (await tcs.Task).Should().Be("https://checkout.stripe.com/e2e");

        await conn.StopAsync();
    }
}
