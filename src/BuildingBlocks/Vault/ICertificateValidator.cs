using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Haworks.BuildingBlocks.Vault.Options;

namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Validates server certificates for Vault TLS connections.
/// </summary>
public interface ICertificateValidator
{
    bool ValidateServerCertificate(
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors policyErrors,
        VaultOptions options);
}
