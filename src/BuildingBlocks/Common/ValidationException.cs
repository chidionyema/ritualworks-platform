namespace Haworks.BuildingBlocks.Common;

/// <summary>
/// Thrown by <c>ValidationBehavior</c> when a request fails validation and the
/// response type is NOT a <see cref="Result{T}"/> (in which case a failure
/// Result is returned instead). Carries per-property error lists for the
/// global exception handler to map to HTTP 400 with a structured body.
/// </summary>
public sealed class ValidationException : Exception
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public ValidationException()
        : base("One or more validation failures occurred.")
    {
        Errors = new Dictionary<string, string[]>();
    }

    public ValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("One or more validation failures occurred.")
    {
        Errors = errors;
    }

    public ValidationException(string message) : base(message)
    {
        Errors = new Dictionary<string, string[]>();
    }

    public ValidationException(string message, Exception inner) : base(message, inner)
    {
        Errors = new Dictionary<string, string[]>();
    }
}
