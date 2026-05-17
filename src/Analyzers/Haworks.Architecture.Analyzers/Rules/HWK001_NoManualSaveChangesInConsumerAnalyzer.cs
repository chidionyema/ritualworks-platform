using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK001_NoManualSaveChangesInConsumerAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.NoManualSaveChangesInConsumer, Diagnostics.NoBeginTransactionInConsumer);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var methodName = GetMethodName(invocation);
        if (methodName is not ("SaveChangesAsync" or "BeginTransactionAsync"))
            return;

        var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken);
        if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
            return;

        if (!IsEfCoreMethod(methodSymbol))
            return;

        var classDecl = invocation.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (classDecl is null)
            return;

        var classSymbol = context.SemanticModel.GetDeclaredSymbol(classDecl, context.CancellationToken);
        if (classSymbol is null || !ImplementsIConsumer(classSymbol))
            return;

        var descriptor = methodName == "BeginTransactionAsync"
            ? Diagnostics.NoBeginTransactionInConsumer
            : Diagnostics.NoManualSaveChangesInConsumer;

        context.ReportDiagnostic(Diagnostic.Create(descriptor, invocation.GetLocation(), methodName));
    }

    private static string? GetMethodName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            IdentifierNameSyntax id => id.Identifier.Text,
            _ => null
        };

    private static bool IsEfCoreMethod(IMethodSymbol method)
    {
        var type = method.ContainingType;
        while (type is not null)
        {
            var name = type.ToDisplayString();
            if (name == "Microsoft.EntityFrameworkCore.DbContext" ||
                name == "Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade")
                return true;
            type = type.BaseType;
        }
        // Also match repository interfaces exposing SaveChangesAsync
        if (method.Name == "SaveChangesAsync" && method.ContainingType?.TypeKind == TypeKind.Interface)
            return true;
        return false;
    }

    private static bool ImplementsIConsumer(INamedTypeSymbol classSymbol)
    {
        foreach (var iface in classSymbol.AllInterfaces)
        {
            if (iface.IsGenericType &&
                iface.OriginalDefinition.ToDisplayString() == "MassTransit.IConsumer<T>")
                return true;
        }
        return false;
    }
}
