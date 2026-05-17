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
                // Skip if it's in a test file or launchSettings-style context
                var filePath = context.Node.SyntaxTree.FilePath ?? "";
                if (filePath.Contains("Test") || filePath.Contains("test") ||
                    filePath.Contains("Stub") || filePath.Contains("Mock") ||
                    filePath.Contains("Fake") || filePath.Contains("launchSettings"))
                    return;

                // Skip XML doc comments and attribute arguments (e.g. [Url("http://localhost")])
                if (context.Node.Parent is AttributeArgumentSyntax)
                    return;

                context.ReportDiagnostic(
                    Diagnostic.Create(Diagnostics.NoHardcodedLocalhost, literal.GetLocation(), pattern));
                return;
            }
        }
    }
}
