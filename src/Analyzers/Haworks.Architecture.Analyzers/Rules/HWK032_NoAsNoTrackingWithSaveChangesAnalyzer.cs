using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK032_NoAsNoTrackingWithSaveChangesAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.NoAsNoTrackingWithSaveChanges);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        if (method.Body is null && method.ExpressionBody is null)
            return;

        var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();

        bool hasAsNoTracking = invocations.Any(i =>
            i.Expression is MemberAccessExpressionSyntax ma &&
            ma.Name.Identifier.Text is "AsNoTracking" or "AsNoTrackingWithIdentityResolution");

        bool hasSaveChanges = invocations.Any(i =>
            i.Expression is MemberAccessExpressionSyntax ma &&
            ma.Name.Identifier.Text is "SaveChangesAsync" or "SaveChanges");

        if (hasAsNoTracking && hasSaveChanges)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.NoAsNoTrackingWithSaveChanges,
                    method.Identifier.GetLocation(), method.Identifier.Text));
        }
    }
}
