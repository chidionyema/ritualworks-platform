using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK035_NoHardcodedCurrencyAnalyzer : DiagnosticAnalyzer
{
    private static readonly string[] CurrencyCodes = { "USD", "EUR", "GBP", "CAD", "AUD", "JPY", "CHF", "CNY" };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.NoHardcodedCurrency);

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

        if (string.IsNullOrEmpty(value) || value.Length != 3)
            return;

        foreach (var code in CurrencyCodes)
        {
            if (string.Equals(value, code, System.StringComparison.Ordinal))
            {
                // Skip test files and constants definitions
                var filePath = context.Node.SyntaxTree.FilePath ?? "";
                if (filePath.Contains("/tests/") || filePath.Contains("/Tests/"))
                    return;

                // Skip if it's in a constant field declaration (defining the config)
                var field = context.Node.FirstAncestorOrSelf<FieldDeclarationSyntax>();
                if (field?.Modifiers.Any(SyntaxKind.ConstKeyword) == true)
                    return;

                // Skip enum-like assignments in Options/Config classes
                var containingClass = context.Node.FirstAncestorOrSelf<ClassDeclarationSyntax>();
                var className = containingClass?.Identifier.Text ?? "";
                if (className.Contains("Options") || className.Contains("Config") || className.Contains("Settings"))
                    return;

                context.ReportDiagnostic(
                    Diagnostic.Create(Diagnostics.NoHardcodedCurrency, literal.GetLocation(), value));
                return;
            }
        }
    }
}
