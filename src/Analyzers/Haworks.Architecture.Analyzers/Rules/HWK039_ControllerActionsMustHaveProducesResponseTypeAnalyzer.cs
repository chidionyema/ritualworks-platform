using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK039_ControllerActionsMustHaveProducesResponseTypeAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.ControllerActionsMustHaveProducesResponseType);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        if (!method.Modifiers.Any(SyntaxKind.PublicKeyword))
            return;

        // Must be inside a class ending with "Controller"
        var classDecl = method.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (classDecl is null || !classDecl.Identifier.Text.EndsWith("Controller", System.StringComparison.Ordinal))
            return;

        // Must have an HTTP method attribute
        var hasHttpAttr = method.AttributeLists
            .SelectMany(al => al.Attributes)
            .Any(a =>
            {
                var name = a.Name.ToString();
                return name.StartsWith("Http", System.StringComparison.Ordinal) ||
                       name.Contains("Route");
            });

        if (!hasHttpAttr)
            return;

        // Check for ProducesResponseType
        var hasProducesAttr = method.AttributeLists
            .SelectMany(al => al.Attributes)
            .Any(a => a.Name.ToString().Contains("ProducesResponseType"));

        if (!hasProducesAttr)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.ControllerActionsMustHaveProducesResponseType,
                    method.Identifier.GetLocation(), method.Identifier.Text));
        }
    }
}
