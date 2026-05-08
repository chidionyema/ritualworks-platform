using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Haworks.BuildingBlocks.Extensions;

public static class AuthenticationExtensions
{
    public static IServiceCollection AddPlatformAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var issuer = configuration["Jwt:Issuer"] ?? throw new InvalidOperationException("Jwt:Issuer missing");
        var audience = configuration["Jwt:Audience"] ?? throw new InvalidOperationException("Jwt:Audience missing");
        var keyPem = configuration["Jwt:SigningKeyPem"] ?? throw new InvalidOperationException("Jwt:SigningKeyPem missing");

        var rsa = RSA.Create();
        rsa.ImportFromPem(DecodeMaybeBase64(keyPem));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = issuer,
                    ValidAudience = audience,
                    IssuerSigningKey = new RsaSecurityKey(rsa),
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
            });

        services.AddAuthorizationBuilder()
            .AddDefaultPolicy("Default", p => p.RequireAuthenticatedUser());

        return services;
    }

    private static string DecodeMaybeBase64(string input)
    {
        return input.Contains("-----BEGIN", StringComparison.Ordinal)
            ? input
            : Encoding.UTF8.GetString(Convert.FromBase64String(input));
    }
}
