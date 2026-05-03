using FluentValidation;
using Haworks.BuildingBlocks.Common;
using MediatR;
using ValidationException = Haworks.BuildingBlocks.Common.ValidationException;

namespace Haworks.BuildingBlocks.Behaviors;

/// <summary>
/// MediatR pipeline behavior that validates requests before handling.
/// </summary>
public sealed class ValidationBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!_validators.Any())
            return await next();

        var context = new ValidationContext<TRequest>(request);

        var validationResults = await Task.WhenAll(
            _validators.Select(v => v.ValidateAsync(context, cancellationToken)));

        var failures = validationResults
            .SelectMany(r => r.Errors)
            .Where(f => f is not null)
            .ToList();

        if (failures.Count > 0)
        {
            // Check if the response type is a Result type
            var responseType = typeof(TResponse);
            if (responseType.IsGenericType &&
                responseType.GetGenericTypeDefinition() == typeof(Result<>))
            {
                // Create a failure result. Tag the error as Validation so the
                // response maps to HTTP 400 (default ErrorType.Internal would
                // bubble out as 500 instead).
                var errorMessages = string.Join("; ", failures.Select(f => f.ErrorMessage));
                var error = Error.Validation("Validation.Failed", errorMessages);

                // Use reflection to call Result.Failure<T>
                var failureMethod = typeof(Result)
                    .GetMethod(nameof(Result.Failure), 1, new[] { typeof(Error) })!
                    .MakeGenericMethod(responseType.GetGenericArguments()[0]);

                return (TResponse)failureMethod.Invoke(null, new object[] { error })!;
            }

            // For non-Result responses, throw validation exception.
            // Global exception handler maps this to HTTP 400 with structured errors.
            throw new ValidationException(
                failures.GroupBy(f => f.PropertyName)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(f => f.ErrorMessage).ToArray()));
        }

        return await next();
    }
}
