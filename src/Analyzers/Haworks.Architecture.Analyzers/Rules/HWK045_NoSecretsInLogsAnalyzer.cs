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

    private static readonly string[] OperationMethodExclusions =
        { "rotate", "refresh", "revoke", "renew" };

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax ma)
            return;

        var name = ma.Name.Identifier.Text;
        // Must be a logger method (LogInformation, LogWarning, etc.) — not LoginAsync, Logout, etc.
        if (!name.StartsWith("Log", System.StringComparison.Ordinal) ||
            name.StartsWith("Login", System.StringComparison.Ordinal) ||
            name.StartsWith("Logout", System.StringComparison.Ordinal))
            return;
        if (name is "Log" or "LogTrace" or "LogDebug" or "LogInformation" or "LogWarning" or "LogError" or "LogCritical")
        { /* valid logger method */ }
        else return;

        // Skip if the enclosing method is a token/credential rotation/revocation operation.
        // These methods are ABOUT tokens (rotating, revoking, refreshing) — not logging secret values.
        var containingMethod = invocation.Ancestors()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();
        if (containingMethod != null)
        {
            var methodName = containingMethod.Identifier.Text.ToLowerInvariant();
            foreach (var exclusion in OperationMethodExclusions)
            {
                if (methodName.Contains(exclusion))
                    return;
            }
        }

        // Only flag when a VARIABLE/IDENTIFIER containing a secret term is passed as an argument.
        // String literals mentioning "token" in descriptions (e.g., "Vault token rotated") are fine —
        // only variables like `password`, `apiToken`, `secretKey` being logged are dangerous.
        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            // Skip string literals and interpolated strings — they're message templates, not secret values
            if (arg.Expression is LiteralExpressionSyntax or InterpolatedStringExpressionSyntax)
                continue;

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
