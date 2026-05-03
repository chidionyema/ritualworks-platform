namespace Haworks.BuildingBlocks.Vault.Options;

/// <summary>
/// Configuration options for HashiCorp Vault integration.
/// </summary>
public sealed class VaultOptions
{
    public const string SectionName = "Vault";

    public bool Enabled { get; set; } = true;
    public string Address { get; set; } = string.Empty;

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
