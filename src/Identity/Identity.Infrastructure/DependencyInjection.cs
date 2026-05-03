using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Haworks.BuildingBlocks.Persistence;
using Haworks.BuildingBlocks.Caching;
using Haworks.BuildingBlocks.Vault;

namespace Haworks.Identity.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // L1+L2 hybrid cache used by TokenRevocationService for fast JTI lookups.
        // L2 backing: in-memory fallback for now (single-process dev). When Aspire
        // injects ConnectionStrings__redis, swap to AddStackExchangeRedisCache.
        services.AddInMemoryDistributedCache();
        services.AddHybridCache();

        // Connection string resolution order:
        //   1. Aspire-injected ConnectionStrings__identity (preferred — Aspire owns the lifecycle)
        //   2. Standard ConnectionStrings:DefaultConnection (override / standalone runs)
        var connectionString = configuration.GetConnectionString("identity")
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "No identity database connection string. Expected 'ConnectionStrings:identity' " +
                "(Aspire-injected) or 'ConnectionStrings:DefaultConnection'.");

        services.AddDbContext<AppIdentityDbContext>((sp, options) =>
        {
            options.UseNpgsql(connectionString);

            // Vault dynamic credentials interceptor — optional for now. When the
            // VaultService stack is wired in (Phase 1 follow-up), the interceptor
            // is registered via services.AddVaultIntegration() and resolved here
            // to swap the static password for an issued one. Until then, plain
            // Aspire-injected creds work fine for dev.
            var interceptor = sp.GetService<DynamicCredentialsConnectionInterceptor>();
            if (interceptor is not null)
            {
                options.AddInterceptors(interceptor);
            }
        });

        services.AddIdentity<User, IdentityRole>(options =>
                {
                    // Sensible defaults; Phase 1 follow-up moves these to Options
                    options.Password.RequiredLength = 8;
                    options.Password.RequireDigit = true;
                    options.Password.RequireUppercase = true;
                    options.Password.RequireNonAlphanumeric = false;
                    options.Lockout.MaxFailedAccessAttempts = 5;
                    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                    options.User.RequireUniqueEmail = true;
                })
                .AddEntityFrameworkStores<AppIdentityDbContext>()
                .AddDefaultTokenProviders();

        // Domain repositories (EF-backed implementations from IdentityRepositories.cs).
        services.AddScoped<IUserRepository, IdentityUserRepository>();
        services.AddScoped<IUserProfileRepository, IdentityUserProfileRepository>();
        services.AddScoped<IRefreshTokenRepository, IdentityRefreshTokenRepository>();

        services.AddScoped<ITokenRevocationService, TokenRevocationService>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();

        // RSA signing keypair for JWT (RS256). Provider reads/writes
        // secret/identity/jwt-signing in Vault. Singleton because the
        // keypair lives for the process lifetime; Program.cs must call
        // VaultJwtSigningKeyProvider.InitializeAsync once at startup
        // before request handling.
        services.AddSingleton<IVaultAppRoleAuthenticator, VaultAppRoleAuthenticator>();
        services.AddSingleton<IJwtSigningKeyProvider>(sp =>
        {
            var cfg          = sp.GetRequiredService<IConfiguration>();
            var address      = cfg["Vault:Address"]      ?? throw new InvalidOperationException("Vault:Address missing");
            var roleIdPath   = cfg["Vault:RoleIdPath"]   ?? throw new InvalidOperationException("Vault:RoleIdPath missing");
            var secretIdPath = cfg["Vault:SecretIdPath"] ?? throw new InvalidOperationException("Vault:SecretIdPath missing");
            var roleId       = File.ReadAllText(roleIdPath).Trim();
            var secretId     = File.ReadAllText(secretIdPath).Trim();
            var auth         = sp.GetRequiredService<IVaultAppRoleAuthenticator>();
            return new VaultJwtSigningKeyProvider(address, "identity", auth, roleId, secretId);
        });

        return services;
    }
}
