using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK015_NoAsyncVoidAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.NoAsyncVoid);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        if (!method.Modifiers.Any(SyntaxKind.AsyncKeyword))
            return;

        if (method.ReturnType is not PredefinedTypeSyntax predefined)
            return;

        if (predefined.Keyword.IsKind(SyntaxKind.VoidKeyword))
        {
            // Allow event handlers (common pattern: object sender, EventArgs e)
            if (method.ParameterList.Parameters.Count == 2)
            {
                var secondParam = method.ParameterList.Parameters[1];
                var typeName = secondParam.Type?.ToString() ?? "";
                if (typeName.Contains("EventArgs"))
                    return;
            }

            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.NoAsyncVoid, method.Identifier.GetLocation(), method.Identifier.Text));
        }
    }
}
