using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK021_NoTaskResultOrWaitAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.NoTaskResultOrWait);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
    {
        var memberAccess = (MemberAccessExpressionSyntax)context.Node;
        if (memberAccess.Name.Identifier.Text != "Result")
            return;

        var symbol = context.SemanticModel.GetSymbolInfo(memberAccess, context.CancellationToken).Symbol;
        if (symbol is IPropertySymbol prop && IsTaskType(prop.ContainingType))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.NoTaskResultOrWait, memberAccess.Name.GetLocation(), ".Result"));
        }
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax ma)
            return;

        var name = ma.Name.Identifier.Text;
        if (name is not ("Wait" or "GetAwaiter"))
            return;

        var symbol = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol;
        if (symbol is IMethodSymbol method && IsTaskType(method.ContainingType))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.NoTaskResultOrWait, ma.Name.GetLocation(), $".{name}()"));
        }
    }

    private static bool IsTaskType(INamedTypeSymbol? type)
    {
        if (type is null) return false;
        var name = type.OriginalDefinition.ToDisplayString();
        return name.StartsWith("System.Threading.Tasks.Task", System.StringComparison.Ordinal) ||
               name.StartsWith("System.Threading.Tasks.ValueTask", System.StringComparison.Ordinal);
    }
}
