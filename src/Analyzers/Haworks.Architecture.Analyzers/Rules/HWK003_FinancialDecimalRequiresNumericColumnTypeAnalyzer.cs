using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK003_FinancialDecimalRequiresNumericColumnTypeAnalyzer : DiagnosticAnalyzer
{
    private static readonly string[] FinancialTerms =
    {
        "amount", "balance", "price", "total", "tax", "commission",
        "subtotal", "discount", "fee", "cost", "revenue", "refund",
        "payout", "linetotal", "unitprice", "netamount", "grossamount"
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.FinancialDecimalRequiresColumnType);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return;

        if (memberAccess.Name.Identifier.Text != "Property")
            return;

        var propertyName = ExtractPropertyName(invocation);
        if (propertyName is null || !IsFinancialName(propertyName))
            return;

        var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken);
        if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
            return;

        var containingType = methodSymbol.ContainingType?.ToDisplayString() ?? "";
        if (!containingType.Contains("EntityTypeBuilder") && !containingType.Contains("OwnedNavigationBuilder"))
            return;

        if (methodSymbol.IsGenericMethod && methodSymbol.TypeArguments.Length > 0 &&
            methodSymbol.TypeArguments[0].SpecialType != SpecialType.System_Decimal)
            return;

        if (HasColumnTypeInStatement(invocation))
            return;

        context.ReportDiagnostic(
            Diagnostic.Create(Diagnostics.FinancialDecimalRequiresColumnType, invocation.GetLocation(), propertyName));
    }

    private static string? ExtractPropertyName(InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList.Arguments.Count == 0)
            return null;

        var arg = invocation.ArgumentList.Arguments[0].Expression;
        if (arg is SimpleLambdaExpressionSyntax lambda && lambda.Body is MemberAccessExpressionSyntax body)
            return body.Name.Identifier.Text;
        if (arg is ParenthesizedLambdaExpressionSyntax pLambda && pLambda.Body is MemberAccessExpressionSyntax pBody)
            return pBody.Name.Identifier.Text;
        return null;
    }

    private static bool IsFinancialName(string name)
    {
        var lower = name.ToLowerInvariant();
        return FinancialTerms.Any(t => lower.Contains(t));
    }

    private static bool HasColumnTypeInStatement(InvocationExpressionSyntax propertyCall)
    {
        var statement = propertyCall.FirstAncestorOrSelf<StatementSyntax>();
        if (statement is null) return false;

        return statement.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Any(inv => inv.Expression is MemberAccessExpressionSyntax ma &&
                        (ma.Name.Identifier.Text == "HasColumnType" || ma.Name.Identifier.Text == "HasPrecision"));
    }
}
