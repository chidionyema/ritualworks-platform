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
                // Skip test directories and the analyzer itself
                var filePath = context.Node.SyntaxTree.FilePath ?? "";
                if (filePath.Contains("/tests/") || filePath.Contains("/Tests/") ||
                    filePath.Contains("\\tests\\") || filePath.Contains("\\Tests\\") ||
                    filePath.Contains(".Testing/") || filePath.Contains(".Testing\\") ||
                    filePath.Contains("HWK019_NoHardcodedLocalhostAnalyzer.cs"))
                    return;

                if (context.Node.Parent is AttributeArgumentSyntax)
                    return;

                // Skip validation/SSRF-blocking code (where localhost is being REJECTED, not USED)
                var classes = context.Node.Ancestors().OfType<ClassDeclarationSyntax>();
                if (classes.Any(c => 
                    c.Identifier.Text.Contains("Ssrf") || 
                    c.Identifier.Text.Contains("Guard") ||
                    c.Identifier.Text.Contains("Validator") || 
                    c.Identifier.Text.Contains("Sanitiz") ||
                    c.Identifier.Text.EndsWith("DbContextFactory", System.StringComparison.Ordinal)))
                    return;

                if (filePath.EndsWith("Program.cs", System.StringComparison.Ordinal))
                    return;

                var method = context.Node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                var methodName = method?.Identifier.Text ?? "";
                if (methodName.Contains("Valid") || methodName.Contains("Validate") || methodName.Contains("IsValid") ||
                    methodName.Contains("Block") || methodName.Contains("Sanitiz") || methodName.Contains("IsAllowed"))
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
