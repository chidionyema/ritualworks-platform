using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Haworks.BuildingBlocks.Vault.Options;

namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Validates server certificates for Vault TLS connections.
/// Supports thumbprint pinning, certificate file pinning, and chain validation.
/// </summary>
public class CertificateValidator : ICertificateValidator
{
    private readonly ILogger<CertificateValidator> _logger;
    private static readonly ConcurrentDictionary<string, X509Certificate2> _pinnedCertCache = new();

    public CertificateValidator(ILogger<CertificateValidator> logger) => _logger = logger;

    public bool ValidateServerCertificate(
        X509Certificate2? cert,
        X509Chain? chain,
        SslPolicyErrors policyErrors,
        VaultOptions options)
    {
        if (cert == null)
        {
            _logger.LogError("Vault server presented no certificate.");
            return false;
        }

        _logger.LogDebug("Validating cert: Subject={Subject}, Thumbprint={Thumbprint}",
            cert.Subject, cert.GetCertHashString(HashAlgorithmName.SHA256));

        // 1. Thumbprint pinning
        if (!string.IsNullOrEmpty(options.CertThumbprint))
        {
            var actual = cert.GetCertHashString(HashAlgorithmName.SHA256).Replace(":", "");
            var expected = options.CertThumbprint.Replace(":", "");
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Thumbprint mismatch: expected {Expected}, actual {Actual}", expected, actual);
                return false;
            }
            _logger.LogDebug("Thumbprint matched.");
            return true; // Pinning overrides chain errors
        }

        // 2. Pinned certificate file
        if (!string.IsNullOrEmpty(options.PinnedCertPath) && File.Exists(options.PinnedCertPath))
        {
            var pinned = _pinnedCertCache.GetOrAdd(options.PinnedCertPath, path =>
            {
                _logger.LogInformation("Loading pinned cert from {Path}", path);
                return X509Certificate2.CreateFromPemFile(path);
            });
            if (cert.RawData.AsSpan().SequenceEqual(pinned.RawData))
            {
                _logger.LogDebug("Pinned certificate matched.");
                return true;
            }
            _logger.LogError("Pinned certificate mismatch.");
            return false;
        }

        // 3. Chain validation (if CA certificate is provided)
        if (ValidateCertificateChain(cert, chain, options))
            return true;

        // 4. Development-only bypass
        if (policyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            var isDev = string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.Ordinal);
            if (isDev && options.AllowChainErrorsInDevelopment)
            {
                _logger.LogWarning("Allowing chain errors in development (INSECURE).");
                return true;
            }
        }

        _logger.LogError("Certificate validation failed. PolicyErrors: {Errors}", policyErrors);
        return false;
    }

    private bool ValidateCertificateChain(X509Certificate2 cert, X509Chain? chain, VaultOptions options)
    {
        if (chain == null || string.IsNullOrEmpty(options.CaCertPath) || !File.Exists(options.CaCertPath))
            return false;

        try
        {
            chain.ChainPolicy.RevocationMode = options.EnableCrlChecks
                ? X509RevocationMode.Online
                : X509RevocationMode.NoCheck;
            chain.ChainPolicy.ExtraStore.Add(X509Certificate2.CreateFromPemFile(options.CaCertPath));

            if (!chain.Build(cert))
            {
                foreach (var status in chain.ChainStatus)
                    _logger.LogWarning("Chain error: {Status}", status.StatusInformation);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chain validation error");
            return false;
        }
    }
}
