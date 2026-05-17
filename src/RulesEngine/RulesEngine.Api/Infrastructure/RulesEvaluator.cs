using System.Linq.Dynamic.Core;
using System.Linq.Dynamic.Core.CustomTypeProviders;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Haworks.BuildingBlocks.Common;
using Haworks.RulesEngine.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Haworks.RulesEngine.Api.Infrastructure;

/// <summary>
/// Restricts Dynamic LINQ to primitive types only — prevents access to File, Process,
/// Environment, and any other dangerous BCL type via expression injection.
/// </summary>
internal sealed class SafeTypeProvider : IDynamicLinqCustomTypeProvider
{
    private static readonly HashSet<Type> AllowedTypes =
    [
        typeof(int),    typeof(long),   typeof(double), typeof(float),
        typeof(decimal),typeof(bool),   typeof(string), typeof(DateTime),
        typeof(DateTimeOffset), typeof(Guid), typeof(object)
    ];

    public HashSet<Type> GetCustomTypes() => AllowedTypes;

    public Dictionary<Type, List<MethodInfo>> GetExtensionMethods() => [];

    public Type? ResolveType(string typeName) => null;

    public Type? ResolveTypeBySimpleName(string simpleTypeName) => null;
}


public class RulesEvaluator : IRulesEvaluator
{
    private readonly RulesDbContext _db;
    private readonly ILogger<RulesEvaluator> _logger;

    // SQL injection guard: block common injection patterns in expressions
    private static readonly string[] ForbiddenTokens =
    [
        "--", ";", "DROP", "DELETE", "INSERT", "UPDATE", "EXEC", "EXECUTE",
        "SELECT", "UNION", "xp_", "sp_", "CAST(", "CONVERT(", "CHAR(",
        "NCHAR(", "VARCHAR(", "DECLARE"
    ];

    public RulesEvaluator(RulesDbContext db, ILogger<RulesEvaluator> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result<RuleEvaluationResult>> EvaluateAsync(
        Guid ruleId,
        Dictionary<string, object> inputs,
        CancellationToken cancellationToken)
    {
        var rule = await _db.Rules
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == ruleId && r.IsActive, cancellationToken);

        if (rule is null)
            return Result.Failure<RuleEvaluationResult>(
                Error.NotFound("RulesEngine.RuleNotFound", $"Active rule '{ruleId}' not found."));

        var validationError = ValidateExpression(rule.Expression);
        if (validationError is not null)
            return Result.Failure<RuleEvaluationResult>(validationError);

        try
        {
            var trace = new StringBuilder();
            trace.Append($"Rule: {rule.Name} | Expression: {rule.Expression} | Inputs: ");
            foreach (var kv in inputs)
                trace.Append($"{kv.Key}={kv.Value} ");

            // Use Dynamic LINQ to parse and evaluate the boolean expression
            bool outcome;
            try
            {
                // Build a single-element array, use DynamicExpressionParser to parse
                // the expression as a lambda over Dictionary<string,object>.
                // SafeTypeProvider restricts resolvable types to primitives only,
                // blocking access to File, Process, Environment, etc.
                var parsingConfig = new ParsingConfig
                {
                    CustomTypeProvider = new SafeTypeProvider(),
                    ResolveTypesBySimpleName = false,
                };
                var parameter = Expression.Parameter(typeof(Dictionary<string, object>), "inputs");
                var lambdaExpression = DynamicExpressionParser.ParseLambda(
                    parsingConfig,
                    new[] { parameter },
                    typeof(bool),
                    TransformExpression(rule.Expression, inputs),
                    []);

                var compiled = (Func<Dictionary<string, object>, bool>)lambdaExpression.Compile();
                outcome = compiled(inputs);
            }
            catch (Exception parseEx)
            {
                _logger.LogWarning(parseEx, "Dynamic expression parse failed for rule {RuleId}: {Expression}",
                    ruleId, rule.Expression);
                return Result.Failure<RuleEvaluationResult>(
                    Error.Validation("RulesEngine.ParseError",
                        $"Expression parse error: {parseEx.Message}"));
            }

            trace.Append($"| Result: {outcome}");

            _logger.LogInformation(
                "Rule {RuleName} ({RuleId}) evaluated to {Outcome}. Trace: {Trace}",
                rule.Name, ruleId, outcome, trace);

            return Result.Success(new RuleEvaluationResult(outcome, rule.Expression, trace.ToString()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error evaluating rule {RuleId}", ruleId);
            return Result.Failure<RuleEvaluationResult>(
                Error.Internal("RulesEngine.EvaluationError", $"Evaluation failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Transforms a plain expression like "age > 18 AND country == \"US\""
    /// into a Dynamic LINQ expression over typed constants.
    /// Variables in the expression are replaced with their literal values
    /// so Dynamic LINQ can evaluate the boolean result without needing
    /// a custom type.
    /// </summary>
    private static string TransformExpression(string expression, Dictionary<string, object> inputs)
    {
        var result = expression;
        // Replace "AND" / "OR" / "NOT" with C# operators
        result = result
            .Replace(" AND ", " && ", StringComparison.OrdinalIgnoreCase)
            .Replace(" OR ", " || ", StringComparison.OrdinalIgnoreCase)
            .Replace(" NOT ", " !", StringComparison.OrdinalIgnoreCase)
            .Replace("==", "==")
            .Replace("!=", "!=");

        // Substitute variable names with their typed literal values
        foreach (var kv in inputs.OrderByDescending(k => k.Key.Length)) // longest first to avoid partial match
        {
            var literal = ToLiteral(kv.Value);
            // Word-boundary replacement: only replace whole tokens
            result = ReplaceWholeWord(result, kv.Key, literal);
        }

        return result;
    }

    private static string ReplaceWholeWord(string source, string word, string replacement)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < source.Length)
        {
            int idx = source.IndexOf(word, i, StringComparison.Ordinal);
            if (idx < 0) { sb.Append(source, i, source.Length - i); break; }

            bool leftBound = idx == 0 || !char.IsLetterOrDigit(source[idx - 1]) && source[idx - 1] != '_';
            bool rightBound = idx + word.Length >= source.Length
                || !char.IsLetterOrDigit(source[idx + word.Length]) && source[idx + word.Length] != '_';

            sb.Append(source, i, idx - i);
            if (leftBound && rightBound)
                sb.Append(replacement);
            else
                sb.Append(word);

            i = idx + word.Length;
        }
        return sb.ToString();
    }

    private static string ToLiteral(object value) => value switch
    {
        bool b => b ? "true" : "false",
        string s => $"\"{s.Replace("\"", "\\\"")}\"",
        null => "null",
        _ => value.ToString() ?? "null"
    };

    private static object[] BuildDynamicArgs(Dictionary<string, object> inputs) => [];

    private static Error? ValidateExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return Error.Validation("RulesEngine.EmptyExpression", "Rule expression cannot be empty.");

        if (expression.Length > 4000)
            return Error.Validation("RulesEngine.ExpressionTooLong",
                "Rule expression must be 4000 characters or fewer.");

        var forbiddenToken = ForbiddenTokens.FirstOrDefault(token => expression.Contains(token, StringComparison.OrdinalIgnoreCase));
        if (forbiddenToken != null)
        {
            return Error.Validation("RulesEngine.InvalidExpression",
                $"Expression contains a forbidden token: '{forbiddenToken}'.");
        }

        return null;
    }
}
