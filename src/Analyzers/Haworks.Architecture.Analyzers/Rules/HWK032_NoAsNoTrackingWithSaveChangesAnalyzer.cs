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

        // If the method also calls .Add() or .AddRange(), the SaveChanges is for a NEW entity,
        // not the AsNoTracking-read one. This is a legitimate pattern (read one, create another).
        bool hasAdd = invocations.Any(i =>
            i.Expression is MemberAccessExpressionSyntax ma &&
            ma.Name.Identifier.Text is "Add" or "AddAsync" or "AddRange" or "AddRangeAsync");
        if (hasAdd) return;

        // If the method also does a tracked query (FirstOrDefaultAsync without AsNoTracking),
        // the SaveChanges is for the tracked entity — the AsNoTracking is a separate read.
        // This is a legitimate pattern: tracked write + untracked read-after-write.
        bool hasTrackedQuery = invocations.Any(i =>
            i.Expression is MemberAccessExpressionSyntax ma &&
            ma.Name.Identifier.Text is "FirstOrDefaultAsync" or "FirstAsync" or "SingleOrDefaultAsync" or "SingleAsync"
            && !IsChainedAfterAsNoTracking(i));
        if (hasTrackedQuery) return;

        if (hasAsNoTracking && hasSaveChanges)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.NoAsNoTrackingWithSaveChanges,
                    method.Identifier.GetLocation(), method.Identifier.Text));
        }
    }

    private static bool IsChainedAfterAsNoTracking(InvocationExpressionSyntax invocation)
    {
        // Walk up the fluent chain to see if AsNoTracking appears
        var current = invocation.Expression;
        while (current is MemberAccessExpressionSyntax ma)
        {
            if (ma.Expression is InvocationExpressionSyntax parent &&
                parent.Expression is MemberAccessExpressionSyntax parentMa &&
                parentMa.Name.Identifier.Text is "AsNoTracking" or "AsNoTrackingWithIdentityResolution")
            {
                return true;
            }
            current = ma.Expression;
        }
        return false;
    }
}
