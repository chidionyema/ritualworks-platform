using Xunit;

namespace Haworks.Payments.Integration;

/// <summary>
/// Shared xUnit collection for every Payments.Integration test class.
/// Forces them to run sequentially against ONE
/// <see cref="PaymentsWebAppFactory"/> (one Testcontainers Postgres,
/// one MassTransit harness). Without this, xUnit runs each test class
/// in parallel — the GitHub Actions runner can't reliably stand up
/// two Postgres containers at once (intermittent
/// "Failed to connect to 127.0.0.1:&lt;port&gt;" failures), and the
/// concurrency tests cross-contaminate each other's harness state.
/// </summary>
[CollectionDefinition("Payments Integration")]
public sealed class PaymentsIntegrationCollection : ICollectionFixture<PaymentsWebAppFactory>
{
}
