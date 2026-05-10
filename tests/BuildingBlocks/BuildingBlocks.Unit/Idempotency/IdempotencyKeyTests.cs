using FluentAssertions;
using Haworks.BuildingBlocks.Idempotency;
using Xunit;

namespace Haworks.BuildingBlocks.Unit.Idempotency;

public sealed class IdempotencyKeyTests
{
    [Fact]
    public void Derive_SameInputs_ProducesSameKey()
    {
        var a = IdempotencyKey.Derive("user-1", "checkout.start", "amount=99.99", "items=p1:2");
        var b = IdempotencyKey.Derive("user-1", "checkout.start", "amount=99.99", "items=p1:2");

        a.Should().Be(b);
    }

    [Fact]
    public void Derive_DifferentUserId_ProducesDifferentKey()
    {
        // Cross-user collisions are the whole reason this helper exists.
        // If alice and bob both submit the same cart with the same client
        // nonce, their keys must differ.
        var alice = IdempotencyKey.Derive("alice", "checkout.start", "amount=99.99");
        var bob = IdempotencyKey.Derive("bob", "checkout.start", "amount=99.99");

        alice.Should().NotBe(bob);
    }

    [Fact]
    public void Derive_DifferentOperation_ProducesDifferentKey()
    {
        var checkout = IdempotencyKey.Derive("user-1", "checkout.start", "amount=99.99");
        var refund = IdempotencyKey.Derive("user-1", "refund.create", "amount=99.99");

        checkout.Should().NotBe(refund);
    }

    [Fact]
    public void Derive_ComponentOrder_IsIgnored()
    {
        // The helper sorts components internally so caller order can't
        // accidentally produce different keys for semantically identical
        // requests.
        var a = IdempotencyKey.Derive("user-1", "checkout.start", "amount=99.99", "items=p1:2", "currency=USD");
        var b = IdempotencyKey.Derive("user-1", "checkout.start", "currency=USD", "amount=99.99", "items=p1:2");

        a.Should().Be(b);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Derive_BlankUserId_Throws(string? userId)
    {
        var act = () => IdempotencyKey.Derive(userId!, "checkout.start", "amount=99.99");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Derive_BlankOperation_Throws(string? operation)
    {
        var act = () => IdempotencyKey.Derive("user-1", operation!, "amount=99.99");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Derive_OutputIsUrlSafeBase64()
    {
        // Key gets shipped in headers and may end up in URLs / log lines —
        // standard base64 has '+' and '/' which break those contexts.
        var key = IdempotencyKey.Derive("user-1", "checkout.start", "amount=99.99");

        key.Should().NotContain("+");
        key.Should().NotContain("/");
        key.Should().NotContain("=");
    }

    [Fact]
    public void Derive_NoComponents_StillProducesKey()
    {
        // Some operations have no semantic body — POST /api/things/recompute,
        // for example. Just userId + operation should still hash cleanly.
        var key = IdempotencyKey.Derive("user-1", "things.recompute");

        key.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Derive_NullComponentEntries_HandledAsEmpty()
    {
        // Defensive: callers passing nullable strings shouldn't blow up.
        var withNull = IdempotencyKey.Derive("user-1", "op", "a", null!, "b");
        var withEmpty = IdempotencyKey.Derive("user-1", "op", "a", string.Empty, "b");

        withNull.Should().Be(withEmpty);
    }
}
