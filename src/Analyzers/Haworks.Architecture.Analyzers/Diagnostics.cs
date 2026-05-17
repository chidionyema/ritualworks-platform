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

    public static readonly DiagnosticDescriptor NoDateTimeNow = new(
        id: "HWK020",
        title: "Use DateTime.UtcNow instead of DateTime.Now",
        messageFormat: "'{0}' uses local timezone which causes ordering bugs in distributed services",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoTaskResultOrWait = new(
        id: "HWK021",
        title: "Do not use .Result or .Wait() on tasks",
        messageFormat: "'{0}' can deadlock the request pipeline — use await instead",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoNewHttpClient = new(
        id: "HWK022",
        title: "Do not instantiate HttpClient directly",
        messageFormat: "'{0}' causes socket exhaustion — use IHttpClientFactory",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoTakeWithoutOrderBy = new(
        id: "HWK023",
        title: "Do not use Take/Skip without OrderBy",
        messageFormat: "'{0}' without OrderBy produces non-deterministic results",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoThreadSleep = new(
        id: "HWK024",
        title: "Do not use Thread.Sleep in async code",
        messageFormat: "'{0}' blocks a thread pool thread — use Task.Delay instead",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoCatchAllWithoutLogging = new(
        id: "HWK025",
        title: "Do not catch Exception without logging or re-throwing",
        messageFormat: "Catch block swallows '{0}' without logging or re-throw — errors will vanish silently",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoStringConcatInSql = new(
        id: "HWK027",
        title: "Do not use string concatenation in SQL queries",
        messageFormat: "String interpolation/concatenation in '{0}' is a SQL injection vector — use parameterized queries",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoReturnTaskWithoutAwaitInTryCatch = new(
        id: "HWK028",
        title: "Do not return Task without await inside try/catch",
        messageFormat: "Returning a Task without await in '{0}' means the catch block will never execute for async exceptions",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoDecimalArithmeticWithoutRounding = new(
        id: "HWK030",
        title: "Financial decimal arithmetic must use explicit rounding",
        messageFormat: "Arithmetic on '{0}' may produce more than 2 decimal places — use Math.Round for financial precision",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoFloatForFinancial = new(
        id: "HWK029",
        title: "Do not use float or double for financial values",
        messageFormat: "Property '{0}' uses floating-point which causes precision loss in financial calculations — use decimal",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor FinancialEntityMustHaveConcurrencyToken = new(
        id: "HWK030",
        title: "Financial entities must have a concurrency token",
        messageFormat: "Entity '{0}' has financial properties but no concurrency token (xmin/RowVersion/[ConcurrencyCheck])",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EventsMustUseImmutableCollections = new(
        id: "HWK031",
        title: "Event records must use immutable collections",
        messageFormat: "Property '{0}' uses mutable collection '{1}' — use IReadOnlyList or ImmutableArray for events",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoAsNoTrackingWithSaveChanges = new(
        id: "HWK032",
        title: "Do not mix AsNoTracking() with SaveChangesAsync()",
        messageFormat: "Method '{0}' calls AsNoTracking() and SaveChangesAsync() which silently discards mutations",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PollyMustAcceptCancellationToken = new(
        id: "HWK033",
        title: "Polly ExecuteAsync must accept CancellationToken",
        messageFormat: "'{0}' without CancellationToken creates zombie tasks that cannot be cancelled",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoHardcodedCurrency = new(
        id: "HWK035",
        title: "Do not hardcode currency strings",
        messageFormat: "Hardcoded currency '{0}' must come from configuration or source event",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
}
