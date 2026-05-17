using Microsoft.CodeAnalysis;

namespace Haworks.Architecture.Analyzers;

public static class Diagnostics
{
    private const string Category = "Haworks.Architecture";

    public static readonly DiagnosticDescriptor NoManualSaveChangesInConsumer = new(
        id: "HWK001",
        title: "Do not call SaveChangesAsync manually inside MassTransit Consumers",
        messageFormat: "Method '{0}' must not be called inside a MassTransit consumer because the EF Outbox commits automatically",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoGuidNewGuidInPollyRetry = new(
        id: "HWK002",
        title: "Do not generate idempotency keys inside Polly retry blocks",
        messageFormat: "'{0}' inside a Polly ExecuteAsync block defeats idempotency because each retry gets a different key",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor FinancialDecimalRequiresColumnType = new(
        id: "HWK003",
        title: "Financial decimal properties must have explicit numeric column types",
        messageFormat: "Property '{0}' represents a financial value but lacks a HasColumnType or HasPrecision configuration",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoPublishWithoutSaveChanges = new(
        id: "HWK004",
        title: "PublishAsync (outbox) must be followed by SaveChangesAsync",
        messageFormat: "'{0}' writes to the EF outbox but no SaveChangesAsync is called in this method",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoBeginTransactionInConsumer = new(
        id: "HWK005",
        title: "Do not call BeginTransactionAsync inside MassTransit Consumers",
        messageFormat: "'{0}' opens a manual transaction inside a MassTransit consumer where the EF Outbox already provides one",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
