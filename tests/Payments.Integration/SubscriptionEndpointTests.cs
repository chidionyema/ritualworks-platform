using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Application.DTOs.Subscriptions;
using Haworks.Payments.Api.Controllers;
using Haworks.Payments.Domain;
using Haworks.BuildingBlocks.Testing.Authentication;
using Haworks.Contracts.Payments;

namespace Haworks.Payments.Integration;

[Collection("Payments Integration")]
public sealed class SubscriptionEndpointTests : IAsyncLifetime
{
    private readonly PaymentsWebAppFactory _factory;
    private readonly Mock<ISubscriptionManager> _managerMock = new();
    private readonly Mock<ISubscriptionService> _serviceMock = new();

    public SubscriptionEndpointTests(PaymentsWebAppFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient CreateClient(bool authenticated = true)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped(_ => _managerMock.Object);
                services.AddScoped(_ => _serviceMock.Object);

                if (!authenticated)
                {
                    // To test 401, we need a scheme that fails or just no default scheme.
                    // Overriding the default "Test" scheme with one that always fails.
                    services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "Failing";
                        options.DefaultChallengeScheme = "Failing";
                    }).AddScheme<AuthenticationSchemeOptions, FailingAuthHandler>("Failing", _ => { });
                }
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task Status_returns_401_when_unauthenticated()
    {
        // Arrange
        var client = CreateClient(authenticated: false);

        // Act
        var resp = await client.GetAsync("/api/subscriptions/status");

        // Assert
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Status_returns_200_with_dto_when_subscription_exists()
    {
        // Arrange
        var userId = TestAuthenticationHandler.TestUserId;
        var expires = DateTime.UtcNow.AddDays(30);
        _managerMock.Setup(x => x.GetStatusAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubscriptionStatusResult
            {
                IsActive = true,
                PlanId = "plan-1",
                CurrentPeriodEnd = expires,
                Status = SubscriptionStatus.Active,
                Provider = PaymentProvider.Stripe
            });

        var client = CreateClient();

        // Act
        var resp = await client.GetAsync("/api/subscriptions/status");

        // Assert
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<SubscriptionStatusDto>();
        dto.Should().NotBeNull();
        dto!.IsSubscribed.Should().BeTrue();
        dto.PlanId.Should().Be("plan-1");
    }

    [Fact]
    public async Task Status_returns_200_with_IsSubscribed_false_when_no_subscription()
    {
        // Arrange
        var userId = TestAuthenticationHandler.TestUserId;
        _managerMock.Setup(x => x.GetStatusAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubscriptionStatusResult
            {
                IsActive = false,
                Provider = PaymentProvider.Stripe
            });

        var client = CreateClient();

        // Act
        var resp = await client.GetAsync("/api/subscriptions/status");

        // Assert
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<SubscriptionStatusDto>();
        dto!.IsSubscribed.Should().BeFalse();
    }

    [Fact]
    public async Task CreateCheckoutSession_returns_200_with_session_id()
    {
        // Arrange
        var request = new CreateSubscriptionCheckoutRequest("price-1", 20.0m, "/success");
        _serviceMock.Setup(x => x.CreateSubscriptionSessionAsync(It.IsAny<CreateSubscriptionSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CheckoutSessionResult
            {
                SessionId = "sess-123",
                SessionUrl = "https://checkout.stripe.com/123",
                Provider = PaymentProvider.Stripe
            });

        var client = CreateClient();

        // Act
        var resp = await client.PostAsJsonAsync("/api/subscriptions/create-checkout-session", request);

        // Assert
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await resp.Content.ReadFromJsonAsync<CreateSubscriptionCheckoutResultDto>();
        result.Should().NotBeNull();
        result!.SessionId.Should().Be("sess-123");
    }

    [Fact]
    public async Task CreateCheckoutSession_returns_400_when_amount_invalid()
    {
        // Arrange
        var request = new CreateSubscriptionCheckoutRequest("price-1", -5.0m, "/success");
        var client = CreateClient();

        // Act
        var resp = await client.PostAsJsonAsync("/api/subscriptions/create-checkout-session", request);

        // Assert
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private class FailingAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.Fail("Forced failure"));
    }
}
