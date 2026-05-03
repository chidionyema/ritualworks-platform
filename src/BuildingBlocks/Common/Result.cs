namespace Haworks.BuildingBlocks.Common;

/// <summary>
/// Represents the outcome of an operation that can either succeed or fail.
/// Used instead of throwing exceptions for expected failure cases.
/// </summary>
public class Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error Error { get; }

    protected Result(bool isSuccess, Error error)
    {
        if (isSuccess && error != Error.None)
            throw new InvalidOperationException("Success result cannot have an error");
        if (!isSuccess && error == Error.None)
            throw new InvalidOperationException("Failure result must have an error");

        IsSuccess = isSuccess;
        Error = error;
    }

    public static Result Success() => new(true, Error.None);
    public static Result Failure(Error error) => new(false, error);
    public static Result<T> Success<T>(T value) => new(value, true, Error.None);
    public static Result<T> Failure<T>(Error error) => new(default, false, error);

    public static Result Create(bool condition, Error error) =>
        condition ? Success() : Failure(error);
}

/// <summary>
/// Represents the outcome of an operation that returns a value on success.
/// </summary>
public class Result<T> : Result
{
    private readonly T? _value;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot access value of a failed result");

    protected internal Result(T? value, bool isSuccess, Error error)
        : base(isSuccess, error)
    {
        _value = value;
    }

    public static implicit operator Result<T>(T value) => Success(value);

    public Result<TNew> Map<TNew>(Func<T, TNew> mapper) =>
        IsSuccess ? Result.Success(mapper(Value)) : Result.Failure<TNew>(Error);

    public async Task<Result<TNew>> BindAsync<TNew>(Func<T, Task<Result<TNew>>> binder) =>
        IsSuccess ? await binder(Value).ConfigureAwait(false) : Result.Failure<TNew>(Error);

    public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<Error, TResult> onFailure) =>
        IsSuccess ? onSuccess(Value) : onFailure(Error);
}

public static class ResultExtensions
{
    public static Result<T> Ensure<T>(this Result<T> result, Func<T, bool> predicate, Error error) =>
        result.IsFailure ? result :
        predicate(result.Value) ? result : Result.Failure<T>(error);

    public static Result Combine(params Result[] results)
    {
        foreach (var result in results)
        {
            if (result.IsFailure)
                return result;
        }
        return Result.Success();
    }

    public static Result<T> Tap<T>(this Result<T> result, Action<T> action)
    {
        if (result.IsSuccess)
            action(result.Value);
        return result;
    }

    public static Result<T> ToResult<T>(this T? value, Error errorIfNull) where T : class =>
        value is not null ? Result.Success(value) : Result.Failure<T>(errorIfNull);
}

public static class ResultActionResultExtensions
{
    public static Microsoft.AspNetCore.Mvc.IActionResult ToActionResult(this Result result)
    {
        if (result.IsSuccess)
            return new Microsoft.AspNetCore.Mvc.OkResult();

        return ToErrorActionResult(result.Error);
    }

    public static Microsoft.AspNetCore.Mvc.IActionResult ToActionResult<T>(this Result<T> result)
    {
        if (result.IsSuccess)
            return new Microsoft.AspNetCore.Mvc.OkObjectResult(result.Value);

        return ToErrorActionResult(result.Error);
    }

    public static Microsoft.AspNetCore.Mvc.IActionResult ToCreatedActionResult<T>(
        this Result<T> result,
        string actionName,
        object routeValues)
    {
        if (result.IsSuccess)
            return new Microsoft.AspNetCore.Mvc.CreatedAtActionResult(
                actionName, null, routeValues, result.Value);

        return ToErrorActionResult(result.Error);
    }

    public static Microsoft.AspNetCore.Mvc.IActionResult ToNoContentActionResult(this Result result)
    {
        if (result.IsSuccess)
            return new Microsoft.AspNetCore.Mvc.NoContentResult();

        return ToErrorActionResult(result.Error);
    }

    private static Microsoft.AspNetCore.Mvc.IActionResult ToErrorActionResult(Error error)
    {
        var statusCode = error.Type switch
        {
            ErrorType.Validation => 400,
            ErrorType.NotFound => 404,
            ErrorType.Unauthorized => 401,
            ErrorType.Forbidden => 403,
            ErrorType.Conflict => 409,
            ErrorType.Timeout => 408,
            ErrorType.Internal => 500,
            _ => 500
        };

        return new Microsoft.AspNetCore.Mvc.ObjectResult(new { error = error.Message })
        {
            StatusCode = statusCode
        };
    }
}
