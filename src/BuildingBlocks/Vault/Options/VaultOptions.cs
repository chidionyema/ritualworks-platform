namespace Haworks.BuildingBlocks.Vault.Options;

/// <summary>
/// Configuration options for HashiCorp Vault integration.
/// </summary>
public sealed class VaultOptions
{
    public const string SectionName = "Vault";

    public bool Enabled { get; set; } = true;
    public string Address { get; set; } = string.Empty;

    // Direct AppRole creds (preferred). Staged as Fly secrets by
    // ci-stage-vault-creds.sh on every deploy. When set, RoleIdPath /
    // SecretIdPath are NOT required — VaultClientFactory uses these
    // values directly (mirroring VaultConfigBootstrap's logic) so the
    // bootstrap-time and runtime auth paths share one truth.
    public string RoleId { get; set; } = string.Empty;
    public string SecretId { get; set; } = string.Empty;

    // True when SecretId is a Vault response-wrapping token rather than
    // the raw secret_id. VaultClientFactory unwraps it on first login
    // and caches the unwrapped value for subsequent re-auths (wrapping
    // tokens are single-use).
    public bool SecretIdIsWrapped { get; set; }

    // Legacy file-on-disk AppRole creds. Kept for backwards compatibility
    // with the old bootstrap-shim approach. Used only when RoleId /
    // SecretId are not configured.
    public string RoleIdPath { get; set; } = string.Empty;
    public string SecretIdPath { get; set; } = string.Empty;
    public string CertThumbprint { get; set; } = string.Empty;
    public string? PinnedCertPath { get; set; }
    public string? HmacKeyPath { get; set; }
    public string? CaCertPath { get; set; } = "/certs-volume/ca.crt";
    public bool AllowChainErrorsInDevelopment { get; set; } = false;
    public string KvMountPoint { get; set; } = "secret";
    public int CircuitBreakerThreshold { get; set; } = 5;
    public int CircuitBreakerDurationSeconds { get; set; } = 30;
    public int HttpTimeoutSeconds { get; set; } = 60;
    public bool RequireHmacValidation { get; set; } = false;
    public bool EnableCrlChecks { get; set; } = true;
    public int TokenRenewalThresholdMinutes { get; set; } = 10;
    public int MaxRetryAttempts { get; set; } = 5;
}
