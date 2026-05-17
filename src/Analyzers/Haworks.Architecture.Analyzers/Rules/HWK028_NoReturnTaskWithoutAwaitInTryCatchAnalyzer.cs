using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

/// <summary>
/// HWK028: If a method has try/catch and returns a Task without awaiting it,
/// the catch block is dead code for async exceptions.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK028_NoReturnTaskWithoutAwaitInTryCatchAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.NoReturnTaskWithoutAwaitInTryCatch);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;

        // Only non-async methods that return Task
        if (method.Modifiers.Any(SyntaxKind.AsyncKeyword))
            return;

        var returnType = method.ReturnType.ToString();
        if (!returnType.Contains("Task"))
            return;

        // Must have a try/catch
        var tryCatches = method.DescendantNodes().OfType<TryStatementSyntax>().ToList();
        if (tryCatches.Count == 0)
            return;

        // Check if any return statement inside a try block returns a non-awaited task
        foreach (var tryCatch in tryCatches)
        {
            var returns = tryCatch.Block.DescendantNodes().OfType<ReturnStatementSyntax>();
            foreach (var ret in returns)
            {
                if (ret.Expression is InvocationExpressionSyntax)
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(Diagnostics.NoReturnTaskWithoutAwaitInTryCatch,
                            ret.GetLocation(), method.Identifier.Text));
                    return;
                }
            }
        }
    }
}
