using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK029_NoFloatForFinancialAnalyzer : DiagnosticAnalyzer
{
    private static readonly string[] FinancialTerms =
        { "amount", "balance", "price", "total", "tax", "commission", "fee", "cost", "revenue", "refund", "payout", "subtotal", "discount" };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.NoFloatForFinancial);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeProperty, SyntaxKind.PropertyDeclaration);
    }

    private static void AnalyzeProperty(SyntaxNodeAnalysisContext context)
    {
        var prop = (PropertyDeclarationSyntax)context.Node;
        var name = prop.Identifier.Text.ToLowerInvariant();

        if (!FinancialTerms.Any(t => name.Contains(t)))
            return;

        var typeText = prop.Type.ToString();
        if (typeText is "float" or "double" or "Single" or "Double" or "System.Single" or "System.Double")
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.NoFloatForFinancial, prop.Identifier.GetLocation(), prop.Identifier.Text));
        }
    }
}
