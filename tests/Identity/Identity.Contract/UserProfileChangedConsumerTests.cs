using FluentAssertions;
using Haworks.Contracts.Identity;
using PactNet;
using Xunit;
using Xunit.Abstractions;

namespace Haworks.Identity.Contract;

/// <summary>
/// CONSUMER-SIDE Pact contract for <see cref="UserProfileChangedEvent"/>.
///
/// This test plays the role of "any service that consumes Identity's
/// UserProfileChanged events" (orders-svc, payments-svc, content-svc all
/// will). It declares the shape the consumer expects; PactNet writes the
/// expectation to a JSON pact file under <c>pacts/</c>.
///
/// The matching PROVIDER-side test (in this same project, in a separate
/// file when Phase 1+ adds it) will replay this contract against
/// identity-svc and assert that what identity actually publishes matches.
/// Until then the pact file is published to the broker so future
/// consumers can declare compatibility via <c>pact-broker can-i-deploy</c>.
///
/// Per <c>docs/microservices-migration/04-testing-strategy.md</c>
/// section "Contract tests as the source of truth".
/// </summary>
public sealed class UserProfileChangedConsumerTests
{
    private readonly IMessagePactBuilderV4 _messagePact;

    public UserProfileChangedConsumerTests(ITestOutputHelper output)
    {
        var config = new PactConfig
        {
            // Where to drop the generated pact file. CI will then publish
            // this to the Pact Broker (Aspire-hosted in dev, Pactflow in prod).
            PactDir = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "pacts"),
            DefaultJsonSettings = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            },
        };

        // Consumer name is intentionally generic — this contract describes
        // what ANY consumer of UserProfileChangedEvent expects from Identity.
        _messagePact = Pact.V4("ConsumerOfIdentity", "identity-svc", config).WithMessageInteractions();
    }

    [Fact]
    public Task UserProfileChangedEvent_must_carry_userId_email_username_roles_and_reason()
    {
        return _messagePact
            .ExpectsToReceive("a UserProfileChangedEvent for a newly registered user")
            .Given("a new user has just registered with username 'alice', email 'alice@example.com', role 'ContentUploader'")
            .WithJsonContent(new
            {
                eventId      = "00000000-0000-0000-0000-000000000000",
                occurredAt   = "2026-05-03T12:00:00Z",
                userId       = "user-123",
                email        = "alice@example.com",
                userName     = "alice",
                roles        = new[] { "ContentUploader" },
                changeReason = "Registered",
            })
            .VerifyAsync<UserProfileChangedEvent>(evt =>
            {
                // PactNet deserializes the WithJsonContent payload into T using
                // the JsonSerializerOptions from PactConfig (camelCase here).
                // The lambda asserts the consumer can read every field it needs.
                evt.Should().NotBeNull();
                evt.UserId.Should().Be("user-123");
                evt.Email.Should().Be("alice@example.com");
                evt.UserName.Should().Be("alice");
                evt.Roles.Should().ContainSingle().Which.Should().Be("ContentUploader");
                evt.ChangeReason.Should().Be("Registered");

                return Task.CompletedTask;
            });
    }
}
