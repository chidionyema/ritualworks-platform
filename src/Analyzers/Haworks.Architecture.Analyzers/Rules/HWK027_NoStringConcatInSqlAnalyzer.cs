using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

/// <summary>
/// HWK027: Detects string interpolation or concatenation passed to methods like
/// FromSqlRaw, ExecuteSqlRaw, ExecuteSqlRawAsync — SQL injection vector.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK027_NoStringConcatInSqlAnalyzer : DiagnosticAnalyzer
{
    private static readonly string[] SqlMethods = { "FromSqlRaw", "ExecuteSqlRaw", "ExecuteSqlRawAsync" };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.NoStringConcatInSql);

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

        var methodName = ma.Name.Identifier.Text;
        if (!IsSqlMethod(methodName))
            return;

        if (invocation.ArgumentList.Arguments.Count == 0)
            return;

        var firstArg = invocation.ArgumentList.Arguments[0].Expression;

        // Flag interpolated strings or binary + expressions with strings
        if (firstArg is InterpolatedStringExpressionSyntax ||
            (firstArg is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression)))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.NoStringConcatInSql, firstArg.GetLocation(), methodName));
        }
    }

    private static bool IsSqlMethod(string name)
    {
        foreach (var m in SqlMethods)
            if (string.Equals(name, m, System.StringComparison.Ordinal)) return true;
        return false;
    }
}
