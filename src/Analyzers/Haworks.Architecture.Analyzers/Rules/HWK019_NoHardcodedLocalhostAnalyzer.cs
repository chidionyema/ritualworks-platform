using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK019_NoHardcodedLocalhostAnalyzer : DiagnosticAnalyzer
{
    private static readonly string[] LocalhostPatterns = { "localhost", "127.0.0.1", "0.0.0.0" };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.NoHardcodedLocalhost);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeStringLiteral, SyntaxKind.StringLiteralExpression);
    }

    private static void AnalyzeStringLiteral(SyntaxNodeAnalysisContext context)
    {
        var literal = (LiteralExpressionSyntax)context.Node;
        var value = literal.Token.ValueText;

        if (string.IsNullOrEmpty(value))
            return;

        foreach (var pattern in LocalhostPatterns)
        {
            if (value.Contains(pattern))
            {
                // Skip test directories
                var filePath = context.Node.SyntaxTree.FilePath ?? "";
                if (filePath.Contains("/tests/") || filePath.Contains("/Tests/") ||
                    filePath.Contains("\\tests\\") || filePath.Contains("\\Tests\\") ||
                    filePath.Contains(".Testing/") || filePath.Contains(".Testing\\"))
                    return;

                if (context.Node.Parent is AttributeArgumentSyntax)
                    return;

                // Skip validation/SSRF-blocking code (where localhost is being REJECTED, not USED)
                var containingClass = context.Node.FirstAncestorOrSelf<ClassDeclarationSyntax>();
                var className = containingClass?.Identifier.Text ?? "";
                if (className.Contains("Ssrf") || className.Contains("Guard") ||
                    className.Contains("Validator") || className.Contains("Sanitiz"))
                    return;

                var method = context.Node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                var methodName = method?.Identifier.Text ?? "";
                if (methodName.Contains("Valid") || methodName.Contains("Block") ||
                    methodName.Contains("Sanitiz") || methodName.Contains("IsAllowed"))
                    return;

                // Skip equality checks (comparing against localhost to reject it)
                if (context.Node.Parent is ArgumentSyntax arg &&
                    arg.Parent?.Parent is InvocationExpressionSyntax inv &&
                    inv.Expression.ToString().Contains("Equals"))
                    return;

                context.ReportDiagnostic(
                    Diagnostic.Create(Diagnostics.NoHardcodedLocalhost, literal.GetLocation(), pattern));
                return;
            }
        }
    }
}
