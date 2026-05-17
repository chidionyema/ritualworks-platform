using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

/// <summary>
/// HWK052: Commands that trigger side effects must have an IdempotencyKey.
/// Without it, network retries create duplicate operations (double-refunds,
/// double-emails, double-provisioning, orphaned resources).
/// Only exempts query commands (Get*, List*, Search*, Validate*).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK052_FinancialCommandMustHaveIdempotencyKeyAnalyzer : DiagnosticAnalyzer
{
    private static readonly string[] QueryPrefixes =
        { "Get", "List", "Search", "Find", "Validate", "Check", "Count", "Exists", "Calculate" };

    private static readonly DiagnosticDescriptor Rule = new(
        id: "HWK052",
        title: "Commands must have an IdempotencyKey property",
        messageFormat: "Command '{0}' lacks an IdempotencyKey — network retries will create duplicate side effects",
        category: "Haworks.Architecture",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeRecord, SyntaxKind.RecordDeclaration);
    }

    private static void AnalyzeRecord(SyntaxNodeAnalysisContext context)
    {
        var record = (RecordDeclarationSyntax)context.Node;
        var name = record.Identifier.Text;

        // Must be a Command
        if (!name.EndsWith("Command", System.StringComparison.Ordinal))
            return;

        // Exempt query commands (read-only, no side effects)
        var commandPrefix = name.Replace("Command", "");
        if (QueryPrefixes.Any(p => commandPrefix.StartsWith(p, System.StringComparison.Ordinal)))
            return;

        // Check if it implements IRequest (MediatR command)
        var baseList = record.BaseList?.ToString() ?? "";
        if (!baseList.Contains("IRequest"))
            return;

        // Check if it has an IdempotencyKey property or parameter
        var hasIdempotencyKey = false;

        if (record.ParameterList is not null)
        {
            hasIdempotencyKey = record.ParameterList.Parameters
                .Any(p => string.Equals(p.Identifier.Text, "IdempotencyKey", System.StringComparison.Ordinal));
        }

        if (!hasIdempotencyKey && record.Members.Count > 0)
        {
            hasIdempotencyKey = record.Members.OfType<PropertyDeclarationSyntax>()
                .Any(p => string.Equals(p.Identifier.Text, "IdempotencyKey", System.StringComparison.Ordinal));
        }

        if (!hasIdempotencyKey)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, record.Identifier.GetLocation(), name));
        }
    }
}
