using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK018_NoNotImplementedExceptionAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.NoNotImplementedException);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeThrow, SyntaxKind.ThrowExpression, SyntaxKind.ThrowStatement);
    }

    private static void AnalyzeThrow(SyntaxNodeAnalysisContext context)
    {
        ExpressionSyntax? expression = context.Node switch
        {
            ThrowStatementSyntax ts => ts.Expression,
            ThrowExpressionSyntax te => te.Expression,
            _ => null
        };

        if (expression is not ObjectCreationExpressionSyntax creation)
            return;

        var typeName = creation.Type.ToString();
        if (typeName is "NotImplementedException" or "System.NotImplementedException")
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.NoNotImplementedException, context.Node.GetLocation(), "NotImplementedException"));
        }
    }
}
