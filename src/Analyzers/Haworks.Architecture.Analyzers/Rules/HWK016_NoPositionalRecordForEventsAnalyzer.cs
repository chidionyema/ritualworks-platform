using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

/// <summary>
/// HWK016: MassTransit uses System.Text.Json for deserialization.
/// Positional records (record Foo(string Bar)) generate a constructor with parameters,
/// and STJ cannot deserialize into them without a parameterless constructor.
/// Events must use { get; init; } properties.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK016_NoPositionalRecordForEventsAnalyzer : DiagnosticAnalyzer
{
    private static readonly string[] EventSuffixes = { "Event", "Command", "Message" };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.NoPositionalRecordForEvents);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeRecord, SyntaxKind.RecordDeclaration);
    }

    private static void AnalyzeRecord(SyntaxNodeAnalysisContext context)
    {
        var record = (RecordDeclarationSyntax)context.Node;

        // Only flag records with positional parameters
        if (record.ParameterList is null || record.ParameterList.Parameters.Count == 0)
            return;

        var name = record.Identifier.Text;

        // Check if this looks like a MassTransit event/command/message
        if (!EventSuffixes.Any(s => name.EndsWith(s, System.StringComparison.Ordinal)))
            return;

        // Also check if it's in a Contracts namespace
        var ns = record.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>();
        var nsName = ns?.Name.ToString() ?? "";
        var isContracts = nsName.Contains("Contracts") || nsName.Contains("Events") || nsName.Contains("Messages");

        if (isContracts || EventSuffixes.Any(s => name.EndsWith(s, System.StringComparison.Ordinal)))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.NoPositionalRecordForEvents, record.Identifier.GetLocation(), name));
        }
    }
}
