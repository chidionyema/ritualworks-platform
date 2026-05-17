using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK025_NoCatchAllWithoutLoggingAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.NoCatchAllWithoutLogging);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeCatch, SyntaxKind.CatchClause);
    }

    private static void AnalyzeCatch(SyntaxNodeAnalysisContext context)
    {
        var catchClause = (CatchClauseSyntax)context.Node;

        // Skip test infrastructure
        var filePath = context.Node.SyntaxTree.FilePath ?? "";
        if (filePath.Contains("/tests/") || filePath.Contains("/Tests/") ||
            filePath.Contains(".Testing/") || filePath.Contains(".Testing\\"))
            return;

        // Only flag catch(Exception) or bare catch
        var typeName = catchClause.Declaration?.Type.ToString() ?? "";
        if (typeName is not ("" or "Exception" or "System.Exception"))
            return;

        if (catchClause.Block is null)
            return;

        var blockText = catchClause.Block.ToString();

        // Check if the catch block logs or re-throws
        if (blockText.Contains("throw") ||
            blockText.Contains("Log") ||
            blockText.Contains("log") ||
            blockText.Contains("_logger") ||
            blockText.Contains("logger"))
            return;

        // Check for any invocation that might be logging
        var invocations = catchClause.Block.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var inv in invocations)
        {
            var text = inv.ToString();
            if (text.Contains("Log") || text.Contains("Trace") || text.Contains("Debug") ||
                text.Contains("Console.Write") || text.Contains("Publish"))
                return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(Diagnostics.NoCatchAllWithoutLogging, catchClause.CatchKeyword.GetLocation(),
                string.IsNullOrEmpty(typeName) ? "Exception" : typeName));
    }
}
