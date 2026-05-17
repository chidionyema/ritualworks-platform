using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

/// <summary>
/// HWK037: Calls to IDistributedCache.SetAsync/SetStringAsync must include
/// DistributedCacheEntryOptions with an expiration. Without it, entries never expire.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK037_CacheMustHaveExpirationAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.CacheMustHaveExpiration);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax ma)
            return;

        var name = ma.Name.Identifier.Text;
        if (name is not ("SetAsync" or "SetStringAsync" or "Set" or "SetString"))
            return;

        var symbol = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol;
        if (symbol is not IMethodSymbol method)
            return;

        var containingType = method.ContainingType?.ToDisplayString() ?? "";
        if (!containingType.Contains("IDistributedCache") && !containingType.Contains("IHybridCache"))
            return;

        // Check if any argument is DistributedCacheEntryOptions
        var hasOptions = false;
        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            var argType = context.SemanticModel.GetTypeInfo(arg.Expression, context.CancellationToken).Type;
            if (argType?.Name.Contains("CacheEntryOptions") == true ||
                argType?.Name.Contains("HybridCacheEntryOptions") == true)
            {
                hasOptions = true;
                break;
            }
        }

        if (!hasOptions)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.CacheMustHaveExpiration, invocation.GetLocation(), name));
        }
    }
}
