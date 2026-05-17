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

    public static readonly DiagnosticDescriptor MustUseContextPublishInConsumer = new(
        id: "HWK007",
        title: "Consumers must publish via ConsumeContext not IPublishEndpoint or IDomainEventPublisher",
        messageFormat: "'{0}' bypasses the consumer's outbox transaction — use context.Publish instead",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoExecuteUpdateInConsumer = new(
        id: "HWK008",
        title: "Do not use ExecuteUpdateAsync/ExecuteDeleteAsync inside MassTransit Consumers",
        messageFormat: "'{0}' bypasses the EF change tracker making entity changes invisible to the MassTransit Outbox",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoExternalIoInsideTransaction = new(
        id: "HWK009",
        title: "Do not make external HTTP/API calls inside a database transaction",
        messageFormat: "External call '{0}' is made while a database transaction is held open",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoSwallowedDbUpdateException = new(
        id: "HWK010",
        title: "Do not swallow DbUpdateException without re-throwing",
        messageFormat: "Catch block for '{0}' does not re-throw which poisons the DbContext",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoTaskRunWithScopedService = new(
        id: "HWK013",
        title: "Do not capture scoped services in Task.Run",
        messageFormat: "Scoped service '{0}' may be disposed before the background work completes",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoAsyncVoid = new(
        id: "HWK015",
        title: "Do not use async void methods",
        messageFormat: "Method '{0}' is async void which causes unobserved exceptions",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoPositionalRecordForEvents = new(
        id: "HWK016",
        title: "MassTransit events must not use positional record constructors",
        messageFormat: "Record '{0}' uses positional parameters which break MassTransit deserialization",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoNotImplementedException = new(
        id: "HWK018",
        title: "Do not use NotImplementedException in production code",
        messageFormat: "'{0}' will crash at runtime — implement the method or remove it",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoHardcodedLocalhost = new(
        id: "HWK019",
        title: "Do not hardcode localhost/127.0.0.1 in production code",
        messageFormat: "Hardcoded '{0}' will silently fail in containers — use configuration",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
