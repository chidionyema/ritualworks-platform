using System.Text.Json;

namespace Haworks.Audit.Application.Redaction;

/// <summary>
/// Strips secrets from event payloads before they're persisted to
/// <c>audit_events</c>. Deny-list rules + Luhn-validated CC regex per
/// docs/agent-briefs/audit-service-spec.md § 5.2.
///
/// L1.A ships the implementation. L0 ships the surface so L1.B's
/// capture pipeline has a stable dependency.
/// </summary>
public interface ISecretRedactor
{
    JsonElement Redact(JsonElement input);
}
