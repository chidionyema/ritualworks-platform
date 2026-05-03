using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Haworks.Payments.Api.Webhooks;
using Xunit;

namespace Haworks.Payments.Unit;

public sealed class StripeSignatureValidatorTests
{
    private const string Secret = "whsec_test_secret_long_enough_for_hmac";
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddSeconds(1_777_000_000);

    [Fact]
    public void Valid_signature_passes()
    {
        var payload = "{\"id\":\"evt_1\",\"type\":\"checkout.session.completed\"}";
        var header = SignedHeader(payload, Secret, Now);
        StripeSignatureValidator.TryValidate(payload, header, Secret, utcNow: Now)
            .Should().BeTrue();
    }

    [Fact]
    public void Wrong_secret_fails()
    {
        var payload = "{}";
        var header = SignedHeader(payload, Secret, Now);
        StripeSignatureValidator.TryValidate(payload, header, "different-secret", utcNow: Now)
            .Should().BeFalse();
    }

    [Fact]
    public void Tampered_payload_fails()
    {
        var payload = "{\"a\":1}";
        var header = SignedHeader(payload, Secret, Now);
        StripeSignatureValidator.TryValidate("{\"a\":2}", header, Secret, utcNow: Now)
            .Should().BeFalse();
    }

    [Fact]
    public void Old_timestamp_outside_tolerance_fails()
    {
        var payload = "{}";
        var oldTime = Now.AddMinutes(-10);
        var header = SignedHeader(payload, Secret, oldTime);
        StripeSignatureValidator.TryValidate(payload, header, Secret,
            tolerance: TimeSpan.FromMinutes(5), utcNow: Now)
            .Should().BeFalse();
    }

    [Fact]
    public void Future_timestamp_outside_tolerance_fails()
    {
        var payload = "{}";
        var futureTime = Now.AddMinutes(10);
        var header = SignedHeader(payload, Secret, futureTime);
        StripeSignatureValidator.TryValidate(payload, header, Secret,
            tolerance: TimeSpan.FromMinutes(5), utcNow: Now)
            .Should().BeFalse();
    }

    [Fact]
    public void Missing_v1_signature_fails()
    {
        var unix = Now.ToUnixTimeSeconds();
        var headerWithoutV1 = $"t={unix}";
        StripeSignatureValidator.TryValidate("{}", headerWithoutV1, Secret, utcNow: Now)
            .Should().BeFalse();
    }

    [Fact]
    public void Missing_timestamp_fails()
    {
        var headerWithoutT = "v1=deadbeef";
        StripeSignatureValidator.TryValidate("{}", headerWithoutT, Secret, utcNow: Now)
            .Should().BeFalse();
    }

    [Fact]
    public void Empty_header_throws()
    {
        Action act = () => StripeSignatureValidator.TryValidate("{}", "", Secret, utcNow: Now);
        act.Should().Throw<ArgumentException>();
    }

    private static string SignedHeader(string payload, string secret, DateTimeOffset signedAt)
    {
        var unix = signedAt.ToUnixTimeSeconds();
        var signedPayload = $"{unix}.{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hex = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload))).ToLowerInvariant();
        return $"t={unix},v1={hex}";
    }
}
