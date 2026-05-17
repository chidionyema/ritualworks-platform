using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haworks.Architecture.Analyzers.Rules;

/// <summary>
/// HWK031: Event/Command/Message records must not have mutable collection properties.
/// List, Dictionary, Collection are mutable — use IReadOnlyList, ImmutableArray, etc.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HWK031_EventsMustUseImmutableCollectionsAnalyzer : DiagnosticAnalyzer
{
    private static readonly string[] EventSuffixes = { "Event", "Command", "Message" };
    private static readonly string[] MutablePrefixes = { "System.Collections.Generic.List<", "System.Collections.Generic.Dictionary<", "System.Collections.ObjectModel.Collection<", "System.Collections.Generic.HashSet<" };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.EventsMustUseImmutableCollections);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeRecord, SyntaxKind.RecordDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeClass, SyntaxKind.ClassDeclaration);
    }

    private static void AnalyzeRecord(SyntaxNodeAnalysisContext context)
    {
        var record = (RecordDeclarationSyntax)context.Node;
        if (!IsEventType(record.Identifier.Text)) return;
        CheckProperties(context, record.Members);
    }

    private static void AnalyzeClass(SyntaxNodeAnalysisContext context)
    {
        var cls = (ClassDeclarationSyntax)context.Node;
        if (!IsEventType(cls.Identifier.Text)) return;
        CheckProperties(context, cls.Members);
    }

    private static void CheckProperties(SyntaxNodeAnalysisContext context, SyntaxList<MemberDeclarationSyntax> members)
    {
        foreach (var member in members.OfType<PropertyDeclarationSyntax>())
        {
            var typeInfo = context.SemanticModel.GetTypeInfo(member.Type, context.CancellationToken);
            var typeSymbol = typeInfo.Type;
            if (typeSymbol is null) continue;

            var fullName = typeSymbol.OriginalDefinition.ToDisplayString();
            foreach (var mutable in MutablePrefixes)
            {
                if (fullName.StartsWith(mutable.TrimEnd('<'), System.StringComparison.Ordinal))
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(Diagnostics.EventsMustUseImmutableCollections,
                            member.Identifier.GetLocation(), member.Identifier.Text, member.Type.ToString()));
                    break;
                }
            }
        }
    }

    private static bool IsEventType(string name) =>
        EventSuffixes.Any(s => name.EndsWith(s, System.StringComparison.Ordinal));
}
