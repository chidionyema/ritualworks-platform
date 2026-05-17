using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK022_NoNewHttpClientAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.NoNewHttpClient);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var creation = (ObjectCreationExpressionSyntax)context.Node;
        var typeName = creation.Type.ToString();

        if (typeName is not ("HttpClient" or "System.Net.Http.HttpClient"))
            return;

        // Verify via SemanticModel
        var typeInfo = context.SemanticModel.GetTypeInfo(creation, context.CancellationToken);
        if (typeInfo.Type?.ToDisplayString() != "System.Net.Http.HttpClient")
            return;

        // Allow in test harness helper classes (e.g. building test clients with handlers)
        var filePath = context.Node.SyntaxTree.FilePath ?? "";
        if (filePath.Contains("/tests/") || filePath.Contains("/Tests/") ||
            filePath.Contains("\\tests\\") || filePath.Contains("\\Tests\\"))
            return;

        // Allow when passing a handler (new HttpClient(handler)) — this is DelegatingHandler testing
        if (creation.ArgumentList?.Arguments.Count > 0)
            return;

        context.ReportDiagnostic(
            Diagnostic.Create(Diagnostics.NoNewHttpClient, creation.GetLocation(), "new HttpClient()"));
    }
}
