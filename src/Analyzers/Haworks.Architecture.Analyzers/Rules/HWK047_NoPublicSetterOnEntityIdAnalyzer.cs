using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK047_NoPublicSetterOnEntityIdAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.NoPublicSetterOnEntityId);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeProperty, SyntaxKind.PropertyDeclaration);
    }

    private static void AnalyzeProperty(SyntaxNodeAnalysisContext context)
    {
        var prop = (PropertyDeclarationSyntax)context.Node;
        if (prop.Identifier.Text != "Id")
            return;

        // Must be in a class in a Domain namespace or ending with entity-like suffixes
        var classDecl = prop.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (classDecl is null) return;

        var ns = classDecl.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>();
        var nsName = ns?.Name.ToString() ?? "";
        if (!nsName.Contains("Domain") && !nsName.Contains("Entities"))
            return;

        // Check for public setter
        var setter = prop.AccessorList?.Accessors
            .FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));

        if (setter is null) return;

        // If setter has no modifier, it inherits the property's visibility (public)
        if (!setter.Modifiers.Any())
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.NoPublicSetterOnEntityId, prop.Identifier.GetLocation(), "Id"));
        }
    }
}
