using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK020_NoDateTimeNowAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.NoDateTimeNow);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
    }

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
    {
        var memberAccess = (MemberAccessExpressionSyntax)context.Node;
        var propertyName = memberAccess.Name.Identifier.Text;

        if (propertyName is not "Now")
            return;

        if (memberAccess.Expression is not IdentifierNameSyntax id)
            return;

        if (id.Identifier.Text is not ("DateTime" or "DateTimeOffset"))
            return;

        var symbol = context.SemanticModel.GetSymbolInfo(memberAccess, context.CancellationToken).Symbol;
        if (symbol is not IPropertySymbol prop)
            return;

        var containingType = prop.ContainingType?.ToDisplayString();
        if (containingType is "System.DateTime" or "System.DateTimeOffset")
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.NoDateTimeNow, memberAccess.GetLocation(), $"{id.Identifier.Text}.Now"));
        }
    }
}
