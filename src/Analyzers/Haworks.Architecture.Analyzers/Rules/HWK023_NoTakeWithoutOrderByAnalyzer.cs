using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK023_NoTakeWithoutOrderByAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.NoTakeWithoutOrderBy);

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
        if (name is not ("Take" or "Skip"))
            return;

        // Walk the fluent chain backward looking for OrderBy/OrderByDescending
        var current = ma.Expression;
        while (current is InvocationExpressionSyntax parentInvocation)
        {
            if (parentInvocation.Expression is MemberAccessExpressionSyntax parentMa)
            {
                var parentName = parentMa.Name.Identifier.Text;
                if (parentName.StartsWith("OrderBy", System.StringComparison.Ordinal) ||
                    parentName.StartsWith("ThenBy", System.StringComparison.Ordinal))
                    return;

                current = parentMa.Expression;
            }
            else
            {
                break;
            }
        }

        // Check the full statement for OrderBy anywhere before Take
        var statement = invocation.FirstAncestorOrSelf<StatementSyntax>();
        if (statement is not null)
        {
            var text = statement.ToString();
            if (text.Contains("OrderBy") || text.Contains("ThenBy"))
                return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(Diagnostics.NoTakeWithoutOrderBy, invocation.GetLocation(), $".{name}()"));
    }
}
