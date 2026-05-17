using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK040_AsyncMethodMustAcceptCancellationTokenAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.AsyncMethodMustAcceptCancellationToken);

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

        if (!method.Modifiers.Any(SyntaxKind.AsyncKeyword))
            return;

        // Skip interface implementations, overrides, and event handlers
        if (method.Modifiers.Any(SyntaxKind.OverrideKeyword))
            return;

        // Skip test/testing infrastructure code
        var filePath = context.Node.SyntaxTree.FilePath ?? "";
        if (filePath.Contains("/tests/") || filePath.Contains("/Tests/") ||
            filePath.Contains("\\tests\\") || filePath.Contains("\\Tests\\") ||
            filePath.Contains(".Testing/") || filePath.Contains(".Testing\\"))
            return;

        // Skip if method name is a well-known entry point (Main, Consume, Handle)
        var name = method.Identifier.Text;
        if (name is "Main" or "Consume" or "Handle" or "InvokeAsync" or "ExecuteAsync")
            return;

        // Check if any parameter is CancellationToken
        var hasCt = method.ParameterList.Parameters.Any(p =>
        {
            var typeName = p.Type?.ToString() ?? "";
            return typeName.Contains("CancellationToken");
        });

        if (!hasCt)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.AsyncMethodMustAcceptCancellationToken,
                    method.Identifier.GetLocation(), name));
        }
    }
}
