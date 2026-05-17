using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK002_NoGuidNewGuidInPollyRetryAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.NoGuidNewGuidInPollyRetry);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (!IsGuidNewGuid(invocation))
            return;

        var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken);
        if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
            return;

        if (methodSymbol.ContainingType?.ToDisplayString() != "System.Guid" || methodSymbol.Name != "NewGuid")
            return;

        if (IsInsidePollyExecuteAsync(invocation, context.SemanticModel, context.CancellationToken))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.NoGuidNewGuidInPollyRetry, invocation.GetLocation(), "Guid.NewGuid()"));
        }
    }

    private static bool IsGuidNewGuid(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax ma &&
        ma.Name.Identifier.Text == "NewGuid" &&
        ma.Expression is IdentifierNameSyntax id &&
        id.Identifier.Text == "Guid";

    private static bool IsInsidePollyExecuteAsync(SyntaxNode node, SemanticModel model, System.Threading.CancellationToken ct)
    {
        foreach (var ancestor in node.Ancestors())
        {
            if (ancestor is not (LambdaExpressionSyntax or AnonymousMethodExpressionSyntax))
                continue;

            if (ancestor.Parent is ArgumentSyntax { Parent: { Parent: InvocationExpressionSyntax parentInvocation } })
            {
                if (IsPollyExecuteMethod(parentInvocation, model, ct))
                    return true;
            }
        }
        return false;
    }

    private static bool IsPollyExecuteMethod(InvocationExpressionSyntax invocation, SemanticModel model, System.Threading.CancellationToken ct)
    {
        var symbolInfo = model.GetSymbolInfo(invocation, ct);
        if (symbolInfo.Symbol is not IMethodSymbol method)
            return false;

        if (!method.Name.StartsWith("Execute", System.StringComparison.Ordinal))
            return false;

        var ns = method.ContainingType?.ContainingNamespace?.ToDisplayString() ?? "";
        var typeName = method.ContainingType?.ToDisplayString() ?? "";

        return ns.StartsWith("Polly", System.StringComparison.Ordinal) ||
               typeName.Contains("IAsyncPolicy") ||
               typeName.Contains("AsyncPolicy") ||
               typeName.Contains("ResiliencePipeline");
    }
}
