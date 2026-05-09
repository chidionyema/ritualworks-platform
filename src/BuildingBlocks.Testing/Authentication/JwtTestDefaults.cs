namespace Haworks.BuildingBlocks.Testing.Authentication;

/// <summary>
/// Test-grade JWT configuration shared across every integration test fixture.
/// Setting these env vars before the WebApplicationFactory builds its host
/// satisfies the <c>AddPlatformAuthentication</c> helper's required-config
/// check; the test fixture's <see cref="TestAuthenticationHandler"/> then
/// overrides the default scheme via <c>AddTestAuth()</c> in
/// <c>ConfigureTestServices</c>, so authenticated calls in tests bypass the
/// real JWT pipeline.
///
/// The signing key is a throwaway RSA 2048 generated for tests only — it
/// has never been used in production, doesn't sign anything visible to a
/// user, and lives in source on purpose so the value is identical across
/// every developer machine and CI runner.
/// </summary>
public static class JwtTestDefaults
{
    public const string Issuer = "test-issuer";
    public const string Audience = "test-audience";

    /// <summary>RSA 2048 PEM (PKCS#8). Test-only — never used by Identity in production.</summary>
    public const string SigningKeyPem = @"-----BEGIN PRIVATE KEY-----
MIIEvwIBADANBgkqhkiG9w0BAQEFAASCBKkwggSlAgEAAoIBAQCqUM8W3+gqh3ab
N267vt0tyHEjLVSSA5R4gBZ8kGImYI1AOGussYOFj8JxHC0JErpsyoBn0WKSbNiA
TNksRSpAJoY+vNMXUiN3qPzeignif+f3nYC8F/t8LaoopWYDp1wH2Ad2adyPLlm/
+fEJyygeGaa12flcoUYEMxmA9X9HB0dslleGewRQpTM9Zmz7QFjggmtCmZI5zAEy
evczIVBZ4pUMQTOq7LJFoCOKZnkz6KE9Id5uJ4Djol4zn41apitqOsUmElbiFLC1
asNslUroX+GPF0dL2SCC/BbESH/irCVeEwm6CN2GVotupuHsG/cBkThtaUCmU82C
Wp1ktMs5AgMBAAECggEAD9AYZh6MUvSIT9F1+8L1DKTlUjeJeFQSQxzbWZ3bKwaA
XtPONXa2yB4IltFfog3F1sd3ZAw1+3PVJxZbfIeAbHvXL3h/HdVvuxKqxaupzsiC
3EPYmb4dSJKBz6vJnLG7cvP8/2LFSlrqlM8rMLZWx3Kovo4aH55OnqGy7rz8X/b3
gTxoj0JfbZsHWQJFwK7weFZGzoFKPI00EvRSTv1Hh0VBp1f79vl6UrlkMptXHiTD
lmZ7MriDW9G6PRr32rEnTfHfOhf8id8M/01UdXa+NYBqMpRUsxY+NS7+WP8xcolM
lo5c+PGBdWFaUgJ3jDpvyfs2d7tGz+vuv9Dp/q/IfQKBgQDXPLz+6IZJ5yoLRJUc
jwRVcev/6X+/ctNT3WckiceUDlBsFK50lvySPqg4aQ5XtfEg8J3g9QFKvqEPfNXd
BbyTjd63rJjO/9jTHM9VUdYgx2FdknfAQLIzto7feUaizb7pUiwUavIG6m42OMWk
PmWP5q9NFF/8DwL3Fhbbbkpm7QKBgQDKkiehZSvuBncr950PS+zeaDwa2gnF6OUQ
6TqvzU7msQmeBM9JPs/qq8HI/ROFuD32c8mKy+Lhvm3R/Kafhw1KsiSdcpuiqzW9
NOG+PXKuIZlayaQHLbImtCnV8spPMYaKhzKHkjzYJkKR17MAPqGrvTKdx3dJED4Z
AKWrB2P//QKBgQCShZCLXzN7v9gJT6jKhjmHCUSFNCl45Owj3UbHwtuQWKY6zWFt
kRNjYzAVJr9SylLZ/7MaXu+AOIFgD7Vu/ua+9Ac3tlFYKScroCMsi8dfDRulHX5T
7DbjqVVdoCuLzNA3+W50f9E/D/vzAXbaNnfhHEMeD86/wmBcYDczLcOMiQKBgQCo
dyo8QC5rkrbzOsdEnGkOgfNShXhRPiGakcx7vivbEOqlnuxgxrsVN+g+ZbIhqBrn
5l17b5ptEPi2BP7xdthoAYUP5+tlOivEAcGne+TuygSGi2E9kxQwue58/qCfgdmZ
RVyRgN3XCOKd9ZvpHS1I7Vy1+NfMTJTIKFCeztOsZQKBgQCneZyXtjqNgxjcX0us
LXHHbUHGMVG0qdWvtqbD6RJ+Y5+avYeh0VdTnEsgEskMBKpOX91jmv7vnvfkysuE
wqIgPyJHi609xvuODQxo4lPlN2sH9cCUDu4BDlphpOgDFwW9RbDITlwmBOfRsT/I
UymtHEbEMgcO+1Sd/8UXFIbF4A==
-----END PRIVATE KEY-----";

    /// <summary>
    /// Convenience: set the env vars the WebApplicationFactory's host needs
    /// before it builds. Call from <c>InitializeAsync</c> (or earlier).
    /// </summary>
    public static void SetTestEnvironmentVariables()
    {
        // Legacy AddPlatformAuthentication keys — still set so any fixture
        // booted against a pre-JWKS-swap branch keeps working.
        Environment.SetEnvironmentVariable("Jwt__Issuer", Issuer);
        Environment.SetEnvironmentVariable("Jwt__Audience", Audience);
        Environment.SetEnvironmentVariable("Jwt__SigningKeyPem", SigningKeyPem);

        // AddJwksAuthentication keys (JwksOptions). [Required] + ValidateOnStart
        // means the host fails to boot if these are absent. Tests override the
        // scheme via TestAuthenticationHandler so the JWKS endpoint is never
        // actually hit; values just need to satisfy validation.
        Environment.SetEnvironmentVariable("Authentication__Jwks__JwksUri",
            "http://test-identity.invalid/.well-known/jwks.json");
        Environment.SetEnvironmentVariable("Authentication__Jwks__Issuer", Issuer);
        Environment.SetEnvironmentVariable("Authentication__Jwks__Audience", Audience);
        Environment.SetEnvironmentVariable("Authentication__Jwks__AutomaticRefresh", "false");
    }
}
