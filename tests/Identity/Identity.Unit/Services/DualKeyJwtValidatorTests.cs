using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using FluentAssertions;
using Haworks.Identity.Application.Options;
using Haworks.Identity.Application.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace Haworks.Identity.Unit.Services;

public class DualKeyJwtValidatorTests
{
    private readonly DualKeyJwtValidator _validator;
    private readonly RsaSecurityKey _currentKey;
    private readonly RsaSecurityKey _previousKey;
    private readonly RsaSecurityKey _unknownKey;
    private readonly TokenValidationParameters _baseParams;

    public DualKeyJwtValidatorTests()
    {
        var options = new Mock<IOptionsMonitor<JwtOptions>>();
        options.Setup(x => x.CurrentValue).Returns(new JwtOptions
        {
            Issuer = "test-issuer",
            Audience = "test-audience",
            OverlapMinutes = 15
        });

        _validator = new DualKeyJwtValidator(options.Object, Mock.Of<ILogger<DualKeyJwtValidator>>());

        _currentKey = CreateRsaKey();
        _previousKey = CreateRsaKey();
        _unknownKey = CreateRsaKey();

        _baseParams = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "test-issuer",
            ValidateAudience = true,
            ValidAudience = "test-audience",
            ValidateLifetime = true,
            ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 }
        };
    }

    [Fact]
    public void Validates_token_signed_with_current_key()
    {
        var token = GenerateToken(_currentKey);

        var result = _validator.ValidateToken(token, _currentKey, _baseParams);

        result.Should().NotBeNull();
        result!.Identity!.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public void Validates_token_signed_with_previous_key_during_overlap()
    {
        _validator.SetPreviousKey(_previousKey, 15);
        var token = GenerateToken(_previousKey);

        var result = _validator.ValidateToken(token, _currentKey, _baseParams);

        result.Should().NotBeNull();
        result!.Identity!.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public void Rejects_token_signed_with_unknown_key()
    {
        var token = GenerateToken(_unknownKey);

        var result = _validator.ValidateToken(token, _currentKey, _baseParams);

        result.Should().BeNull();
    }

    [Fact]
    public void Clears_previous_key_after_overlap_window()
    {
        _validator.SetPreviousKey(_previousKey, 15);
        _validator.ClearPreviousKey();

        var key = _validator.GetPreviousKeyIfValid();
        key.Should().BeNull();
    }

    [Fact]
    public void GetPreviousKeyIfValid_returns_null_when_no_key_set()
    {
        _validator.GetPreviousKeyIfValid().Should().BeNull();
    }

    private static RsaSecurityKey CreateRsaKey()
    {
        var rsa = RSA.Create(2048);
        return new RsaSecurityKey(rsa);
    }

    private static string GenerateToken(SecurityKey signingKey)
    {
        var handler = new JwtSecurityTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, "test-user"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            }),
            Expires = DateTime.UtcNow.AddHours(1),
            Issuer = "test-issuer",
            Audience = "test-audience",
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)
        };
        var token = handler.CreateToken(descriptor);
        return handler.WriteToken(token);
    }
}
