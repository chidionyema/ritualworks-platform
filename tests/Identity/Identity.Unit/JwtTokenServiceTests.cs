using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using FluentAssertions;
using Haworks.BuildingBlocks.Vault;
using Haworks.Identity.Application.Interfaces;
using Haworks.Identity.Application.Options;
using Haworks.Identity.Domain;
using Haworks.Identity.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Xunit;

namespace Haworks.Identity.Unit;

/// <summary>
/// Unit tests for <see cref="JwtTokenService"/>: token generation produces a
/// well-formed RS256 JWT with the expected claims; validation accepts our
/// own signed tokens and rejects tampered ones.
/// </summary>
public sealed class JwtTokenServiceTests
{
    private readonly RSA _rsa;
    private readonly TestSigningKeyProvider _signingKeyProvider;

    public JwtTokenServiceTests()
    {
        _rsa = RSA.Create(2048);
        _signingKeyProvider = new TestSigningKeyProvider(_rsa);
    }

    [Fact]
    public async Task GenerateTokenAsync_emits_RS256_JWT_with_expected_claims()
    {
        var sut = CreateSut();
        var user = NewUser();
        var expiry = DateTime.UtcNow.AddMinutes(15);

        var token = await sut.GenerateTokenAsync(user, expiry);

        token.Header["alg"].Should().Be(SecurityAlgorithms.RsaSha256);
        token.Header["kid"].Should().Be(_signingKeyProvider.KeyId);
        token.Issuer.Should().Be("test-issuer");
        token.Audiences.Should().Contain("test-audience");
        token.Claims.Should().Contain(c =>
            c.Type == JwtRegisteredClaimNames.Sub && c.Value == user.Id);
        token.Claims.Should().Contain(c =>
            c.Type == JwtRegisteredClaimNames.Email && c.Value == user.Email);
        token.Claims.Should().Contain(c =>
            c.Type == JwtRegisteredClaimNames.Jti);
    }

    [Fact]
    public async Task ValidateToken_accepts_token_we_just_generated()
    {
        var sut = CreateSut();
        var user = NewUser();
        var expiry = DateTime.UtcNow.AddMinutes(15);

        var token = await sut.GenerateTokenAsync(user, expiry);
        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        var principal = sut.ValidateToken(tokenString);

        principal.Should().NotBeNull();
        principal!.FindFirst(JwtRegisteredClaimNames.Sub)?.Value.Should().Be(user.Id);
    }

    [Fact]
    public void ValidateToken_rejects_a_completely_garbage_string()
    {
        var sut = CreateSut();

        var principal = sut.ValidateToken("not.a.real.jwt");

        principal.Should().BeNull();
    }

    [Fact]
    public void ValidateToken_rejects_token_signed_with_a_DIFFERENT_key()
    {
        // Build a JWT signed with an UNRELATED RSA key — same claims/issuer,
        // wrong signature. Validation must reject (signature is the only
        // thing standing between trust and forgery).
        using var rogueRsa = RSA.Create(2048);
        var rogueKey = new RsaSecurityKey(rogueRsa);
        var rogueCreds = new SigningCredentials(rogueKey, SecurityAlgorithms.RsaSha256);
        var rogueToken = new JwtSecurityToken(
            issuer: "test-issuer",
            audience: "test-audience",
            claims: new[] { new System.Security.Claims.Claim("sub", "evil") },
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: rogueCreds);
        var rogueTokenString = new JwtSecurityTokenHandler().WriteToken(rogueToken);

        var sut = CreateSut();
        var principal = sut.ValidateToken(rogueTokenString);

        principal.Should().BeNull("a JWT signed by a different key must not validate");
    }

    private JwtTokenService CreateSut()
    {
        var jwtOptions = Options.Create(new JwtOptions
        {
            Key = "this-is-not-actually-used-anymore-RS256-uses-the-RsaKey-instead-XxxXxxX",
            Issuer = "test-issuer",
            Audience = "test-audience",
            TokenExpiryMinutes = 15,
        });

        var userMgrStore = Mock.Of<IUserStore<User>>();
        var userManager = new Mock<UserManager<User>>(
            userMgrStore, null!, null!, null!, null!, null!, null!, null!, null!);
        userManager.Setup(m => m.GetRolesAsync(It.IsAny<User>()))
            .ReturnsAsync(new List<string> { "ContentUploader" });
        userManager.Setup(m => m.GetClaimsAsync(It.IsAny<User>()))
            .ReturnsAsync(new List<System.Security.Claims.Claim>());

        var revocation = new Mock<ITokenRevocationService>();
        revocation.Setup(r => r.IsTokenRevokedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Development");

        return new JwtTokenService(
            userManager.Object,
            jwtOptions,
            NullLogger<JwtTokenService>.Instance,
            revocation.Object,
            _signingKeyProvider,
            environment.Object);
    }

    private static User NewUser() => new()
    {
        Id = Guid.NewGuid().ToString(),
        UserName = "alice",
        Email = "alice@example.com",
    };

    private sealed class TestSigningKeyProvider : IJwtSigningKeyProvider
    {
        public string KeyId { get; } = "test-key-001";
        public RsaSecurityKey SigningKey { get; }
        public JsonWebKey PublicJwk { get; }

        public TestSigningKeyProvider(RSA rsa)
        {
            SigningKey = new RsaSecurityKey(rsa) { KeyId = KeyId };
            var publicParams = rsa.ExportParameters(includePrivateParameters: false);
            PublicJwk = new JsonWebKey
            {
                Kty = "RSA",
                Use = "sig",
                Alg = SecurityAlgorithms.RsaSha256,
                Kid = KeyId,
                N = Base64UrlEncoder.Encode(publicParams.Modulus!),
                E = Base64UrlEncoder.Encode(publicParams.Exponent!),
            };
        }
    }
}
