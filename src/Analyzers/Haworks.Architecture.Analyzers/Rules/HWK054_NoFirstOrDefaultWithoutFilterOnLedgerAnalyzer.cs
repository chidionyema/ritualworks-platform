using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

/// <summary>
/// HWK054: FirstOrDefaultAsync on financial/ledger tables without explicit
/// type-filtering Where clause produces non-deterministic results.
/// A payment has multiple ledger entries (seller credit + platform debit) —
/// FirstOrDefault without filtering by EntryType/AccountType picks randomly.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK054_NoFirstOrDefaultWithoutFilterOnLedgerAnalyzer : DiagnosticAnalyzer
{
    private static readonly string[] LedgerTableNames =
        { "LedgerEntries", "LedgerAccounts", "Payments", "Refunds", "Transactions" };

    private static readonly DiagnosticDescriptor Rule = new(
        id: "HWK054",
        title: "FirstOrDefaultAsync on financial tables must have explicit type/status filter",
        messageFormat: "'{0}' on financial table without explicit Where filter — non-deterministic row selection in multi-entry tables",
        category: "Haworks.Architecture",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax ma)
            return;

        var methodName = ma.Name.Identifier.Text;
        if (methodName is not ("FirstOrDefaultAsync" or "FirstAsync" or "SingleOrDefaultAsync"))
            return;

        // Walk back through the fluent chain to find the table name
        var chainText = GetFullChainText(ma);

        // Check if this is operating on a ledger/financial table
        if (!LedgerTableNames.Any(t => chainText.Contains(t, System.StringComparison.Ordinal)))
            return;

        // Check if there's a Where clause in the chain with a type/status filter
        if (chainText.Contains(".Where(") || chainText.Contains(".Join("))
            return;

        // Check if FirstOrDefaultAsync itself has a predicate argument
        if (invocation.ArgumentList.Arguments.Count > 0 &&
            invocation.ArgumentList.Arguments[0].Expression is LambdaExpressionSyntax)
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName));
    }

    private static string GetFullChainText(MemberAccessExpressionSyntax ma)
    {
        var statement = ma.FirstAncestorOrSelf<StatementSyntax>();
        return statement?.ToString() ?? ma.ToString();
    }
}
