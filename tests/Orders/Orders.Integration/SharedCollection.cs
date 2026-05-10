using Xunit;

namespace Haworks.Orders.Integration;

[CollectionDefinition("Orders Integration")]
public sealed class SharedCollection : ICollectionFixture<OrdersWebAppFactory>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}
