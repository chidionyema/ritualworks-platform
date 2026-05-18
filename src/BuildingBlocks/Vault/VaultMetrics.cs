using System.Diagnostics.Metrics;

namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// OpenTelemetry metrics for Vault operations. Registered via
/// builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter("Haworks.Vault")).
/// </summary>
public static class VaultMetrics
{
    public static readonly Meter Meter = new("Haworks.Vault", "1.0.0");

    /// <summary>Successful Vault AppRole/token authentications.</summary>
    public static readonly Counter<long> AuthSuccess =
        Meter.CreateCounter<long>("vault.auth.success", description: "Successful Vault authentications");

    /// <summary>Failed Vault authentication attempts.</summary>
    public static readonly Counter<long> AuthFailure =
        Meter.CreateCounter<long>("vault.auth.failure", description: "Failed Vault authentication attempts");

    /// <summary>Duration of credential rotation operations.</summary>
    public static readonly Histogram<double> CredentialRotationDuration =
        Meter.CreateHistogram<double>(
            "vault.credential_rotation.duration_seconds",
            unit: "s",
            description: "Duration of Vault credential rotation operations");

    /// <summary>Failed credential rotation attempts.</summary>
    public static readonly Counter<long> CredentialRotationFailure =
        Meter.CreateCounter<long>(
            "vault.credential_rotation.failure",
            description: "Failed Vault credential rotation attempts");

    /// <summary>Duration of KV secret read operations.</summary>
    public static readonly Histogram<double> KvReadDuration =
        Meter.CreateHistogram<double>(
            "vault.kv.read.duration_seconds",
            unit: "s",
            description: "Duration of Vault KV secret read operations");

    /// <summary>Failed KV secret read attempts.</summary>
    public static readonly Counter<long> KvReadFailure =
        Meter.CreateCounter<long>(
            "vault.kv.read.failure",
            description: "Failed Vault KV secret read attempts");
}
