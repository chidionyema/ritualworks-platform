using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK045_NoSecretsInLogsAnalyzer : DiagnosticAnalyzer
{
    private static readonly string[] SecretTerms =
        { "password", "secret", "token", "apikey", "api_key", "connectionstring", "credential", "private_key", "privatekey" };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.NoSecretsInLogs);

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

        // Check arguments for references to secret-sounding identifiers
        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            var argText = arg.Expression.ToString().ToLowerInvariant();
            foreach (var term in SecretTerms)
            {
                if (argText.Contains(term))
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(Diagnostics.NoSecretsInLogs, arg.GetLocation(), term));
                    return;
                }
            }
        }
    }
}
