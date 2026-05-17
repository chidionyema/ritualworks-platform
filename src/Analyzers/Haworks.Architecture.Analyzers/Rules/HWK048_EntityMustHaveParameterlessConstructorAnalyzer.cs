using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK048_EntityMustHaveParameterlessConstructorAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.EntityMustHaveParameterlessConstructor);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeClass, SyntaxKind.ClassDeclaration);
    }

    private static void AnalyzeClass(SyntaxNodeAnalysisContext context)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;

        // Only check classes in Domain/Entities namespaces
        var ns = classDecl.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>();
        var nsName = ns?.Name.ToString() ?? "";
        if (!nsName.Contains("Domain") && !nsName.Contains("Entities"))
            return;

        // Must have an Id property (entity heuristic)
        var hasId = classDecl.Members.OfType<PropertyDeclarationSyntax>()
            .Any(p => p.Identifier.Text == "Id");
        if (!hasId) return;

        // Get all constructors
        var ctors = classDecl.Members.OfType<ConstructorDeclarationSyntax>().ToList();

        // If no explicit constructors, the compiler provides a parameterless one — OK
        if (ctors.Count == 0) return;

        // Check if any constructor is parameterless (any access modifier)
        var hasParameterless = ctors.Any(c => c.ParameterList.Parameters.Count == 0);
        if (!hasParameterless)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.EntityMustHaveParameterlessConstructor,
                    classDecl.Identifier.GetLocation(), classDecl.Identifier.Text));
        }
    }
}
