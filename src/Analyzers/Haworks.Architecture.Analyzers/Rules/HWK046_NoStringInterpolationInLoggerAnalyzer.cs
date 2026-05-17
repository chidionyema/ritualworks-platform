using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK046_NoStringInterpolationInLoggerAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.NoStringInterpolationInLogger);

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
        if (!name.StartsWith("Log", System.StringComparison.Ordinal))
            return;

        // Check if the receiver looks like a logger
        var receiverText = ma.Expression.ToString();
        if (!receiverText.Contains("logger") && !receiverText.Contains("Logger") &&
            !receiverText.Contains("_log"))
            return;

        // Check first argument for interpolated string
        if (invocation.ArgumentList.Arguments.Count == 0)
            return;

        var firstArg = invocation.ArgumentList.Arguments[0].Expression;
        if (firstArg is InterpolatedStringExpressionSyntax)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.NoStringInterpolationInLogger, firstArg.GetLocation()));
        }
    }
}
