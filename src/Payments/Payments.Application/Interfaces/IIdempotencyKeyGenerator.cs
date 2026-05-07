namespace Haworks.Payments.Application.Interfaces;

/// <summary>
/// Generates deterministic, collision-resistant idempotency keys.
/// </summary>
public interface IIdempotencyKeyGenerator
{
    /// <summary>
    /// Generates a deterministic key from the provided components.
    /// </summary>
    /// <param name="userId">User ID associated with the operation</param>
    /// <param name="operation">Operation name (e.g. "create_session")</param>
    /// <param name="components">Additional unique components (e.g. OrderId)</param>
    /// <returns>Base64 encoded SHA256 hash</returns>
    string GenerateKey(string userId, string operation, params string[] components);
}
