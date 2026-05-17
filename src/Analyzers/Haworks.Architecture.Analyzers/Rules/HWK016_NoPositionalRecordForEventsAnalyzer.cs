using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

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
        if (record.ParameterList is null || record.ParameterList.Parameters.Count == 0)
            return;

        var name = record.Identifier.Text;

        // Only flag Event/Message suffix (MassTransit contracts serialized over the wire).
        // MediatR Commands are local in-process — positional records are fine for those.
        if (!name.EndsWith("Event", System.StringComparison.Ordinal) &&
            !name.EndsWith("Message", System.StringComparison.Ordinal))
            return;

        // Only flag records in Contracts namespace or that inherit DomainEvent
        var ns = record.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>();
        var nsName = ns?.Name.ToString() ?? "";
        if (!nsName.Contains("Contracts") && !nsName.Contains("Events") &&
            !nsName.Contains("Messages"))
        {
            // Check if it's in a file that looks like a hub notifier DTO (not serialized over broker)
            var filePath = context.Node.SyntaxTree.FilePath ?? "";
            if (filePath.Contains("Hub") || filePath.Contains("Notifier") || filePath.Contains("SignalR"))
                return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(Diagnostics.NoPositionalRecordForEvents, record.Identifier.GetLocation(), name));
    }
}
