namespace Haworks.BuildingBlocks.Common;

/// <summary>
/// Categorizes the type of error for HTTP status code mapping.
/// </summary>
public enum ErrorType
{
    None,
    Validation,
    NotFound,
    Conflict,
    Forbidden,
    Timeout,
    Internal,
    Unauthorized
}

/// <summary>
/// Represents an error with a code, message, and type.
/// Used throughout the application for type-safe error handling.
///
/// Per-domain error catalogs (e.g., <c>Auth.InvalidCredentials</c>, <c>Orders.NotFound</c>)
/// live in the owning service, not in BuildingBlocks. This base type is shared;
/// the domain-specific catalogs are owned by their service.
/// </summary>
public sealed record Error(string Code, string Message, ErrorType Type = ErrorType.Internal)
{
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.None);

    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);
    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);
    public static Error Internal(string code, string message) => new(code, message, ErrorType.Internal);
    public static Error Timeout(string code, string message) => new(code, message, ErrorType.Timeout);
    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);
    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);
    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);
}
