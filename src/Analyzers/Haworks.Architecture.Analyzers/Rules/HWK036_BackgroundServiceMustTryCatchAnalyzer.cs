using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK036_BackgroundServiceMustTryCatchAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.BackgroundServiceMustTryCatch);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        if (method.Identifier.Text != "ExecuteAsync")
            return;

        if (!method.Modifiers.Any(SyntaxKind.OverrideKeyword))
            return;

        // Verify the class inherits from BackgroundService
        var classDecl = method.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (classDecl is null) return;

        var classSymbol = context.SemanticModel.GetDeclaredSymbol(classDecl, context.CancellationToken);
        if (classSymbol is null || !InheritsBackgroundService(classSymbol))
            return;

        // Check if the method body has a root-level try statement
        if (method.Body is null) return;

        var hasRootTry = method.Body.Statements.OfType<TryStatementSyntax>().Any();
        if (!hasRootTry)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.BackgroundServiceMustTryCatch,
                    method.Identifier.GetLocation(), classDecl.Identifier.Text));
        }
    }

    private static bool InheritsBackgroundService(INamedTypeSymbol type)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.Name == "BackgroundService" || current.ToDisplayString().Contains("BackgroundService"))
                return true;
            current = current.BaseType;
        }
        return false;
    }
}
