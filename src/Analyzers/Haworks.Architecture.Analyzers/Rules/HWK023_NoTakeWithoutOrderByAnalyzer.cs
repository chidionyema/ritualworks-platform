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

        // Only flag IQueryable chains (EF/database). In-memory collections
        // (arrays, lists, IEnumerable) produce deterministic results from
        // Skip/Take without OrderBy — no false positives on string[].Skip().
        var receiverType = context.SemanticModel.GetTypeInfo(ma.Expression, context.CancellationToken).Type;
        if (receiverType is not null && !IsQueryable(receiverType))
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

    /// <summary>
    /// Returns true if the type is IQueryable, IOrderedQueryable, or DbSet
    /// (types where Skip/Take without OrderBy produces non-deterministic SQL).
    /// Returns false for in-memory types (arrays, List, IEnumerable).
    /// </summary>
    private static bool IsQueryable(ITypeSymbol type)
    {
        // Check the type and all its interfaces
        if (IsQueryableSymbol(type))
            return true;

        foreach (var iface in type.AllInterfaces)
        {
            if (IsQueryableSymbol(iface))
                return true;
        }

        return false;
    }

    private static bool IsQueryableSymbol(ITypeSymbol type)
    {
        var name = type.Name;
        return name is "IQueryable" or "IOrderedQueryable" or "DbSet";
    }
}
