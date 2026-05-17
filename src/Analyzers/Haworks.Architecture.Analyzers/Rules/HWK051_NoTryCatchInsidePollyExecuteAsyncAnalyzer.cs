using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

/// <summary>
/// HWK051: Detects try/catch blocks INSIDE Polly ExecuteAsync delegates.
/// When you catch exceptions inside the retry block, Polly never sees them —
/// it can't retry 429/503 errors because the exception was swallowed.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK051_NoTryCatchInsidePollyExecuteAsyncAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "HWK051",
        title: "Do not catch exceptions inside Polly ExecuteAsync",
        messageFormat: "try/catch inside Polly ExecuteAsync swallows exceptions that Polly needs for retry decisions",
        category: "Haworks.Architecture",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeTryCatch, SyntaxKind.TryStatement);
    }

    private static void AnalyzeTryCatch(SyntaxNodeAnalysisContext context)
    {
        var tryStatement = (TryStatementSyntax)context.Node;

        // Walk up: is this try/catch inside a lambda that's an argument to ExecuteAsync?
        foreach (var ancestor in tryStatement.Ancestors())
        {
            if (ancestor is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax)
            {
                // Check if the lambda's parent is an argument to ExecuteAsync
                if (ancestor.Parent is ArgumentSyntax { Parent: { Parent: InvocationExpressionSyntax invocation } })
                {
                    if (IsPollyExecuteMethod(invocation, context))
                    {
                        // Only flag if the catch block returns a value (swallows the exception)
                        // Allow catch blocks that re-throw
                        foreach (var catchClause in tryStatement.Catches)
                        {
                            var blockText = catchClause.Block.ToString();
                            if (!blockText.Contains("throw"))
                            {
                                context.ReportDiagnostic(
                                    Diagnostic.Create(Rule, catchClause.CatchKeyword.GetLocation()));
                                return;
                            }
                        }
                    }
                }
                break; // Only check immediate parent lambda
            }
        }
    }

    private static bool IsPollyExecuteMethod(InvocationExpressionSyntax invocation, SyntaxNodeAnalysisContext context)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax ma)
            return false;

        var name = ma.Name.Identifier.Text;
        if (!name.StartsWith("Execute", System.StringComparison.Ordinal))
            return false;

        // Check receiver type contains Polly/Policy/Resilience
        var receiverText = ma.Expression.ToString();
        return receiverText.Contains("policy", System.StringComparison.OrdinalIgnoreCase) ||
               receiverText.Contains("resilience", System.StringComparison.OrdinalIgnoreCase) ||
               receiverText.Contains("Policy", System.StringComparison.Ordinal);
    }
}
