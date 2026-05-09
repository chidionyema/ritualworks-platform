using Xunit;

namespace Haworks.Notifications.Integration;

/// <summary>
/// Single shared <see cref="NotificationsWebAppFactory"/> across every
/// integration test class in this assembly. One host build per
/// `dotnet test` run instead of one per fixture, per .claude/rules/testing.md.
/// Tests that need different mocks override per-test via
/// <c>factory.WithWebHostBuilder(b => b.ConfigureTestServices(...))</c>
/// rather than subclassing the factory.
/// </summary>
// Suppress CA1711 — xUnit's collection-definition convention is to name the
// type ending in "Collection". The rule's normal "no Collection suffix on
// non-collection types" guidance doesn't apply to xunit collection markers.
#pragma warning disable CA1711
[CollectionDefinition("Notifications Integration")]
public sealed class NotificationsIntegrationCollection
    : ICollectionFixture<NotificationsWebAppFactory>;
#pragma warning restore CA1711
