using FluentAssertions;
using Haworks.Notifications.Application.Common.Idempotency;
using Xunit;

namespace Haworks.Notifications.Unit.Idempotency;

public class IdempotencyTests
{
    private readonly IIdempotencyKeyGenerator _sut = new IdempotencyKeyGenerator();

    [Fact]
    public void Generate_SameInputs_ProducesSameKey()
    {
        var k1 = _sut.Generate("tenant-1", "welcome.v1", "user@example.com", "client-key-1");
        var k2 = _sut.Generate("tenant-1", "welcome.v1", "user@example.com", "client-key-1");

        k1.Should().Be(k2);
    }

    [Fact]
    public void Generate_DifferentTemplate_ProducesDifferentKey()
    {
        var k1 = _sut.Generate("tenant-1", "welcome.v1", "user@example.com", "k");
        var k2 = _sut.Generate("tenant-1", "welcome.v2", "user@example.com", "k");

        k1.Should().NotBe(k2);
    }

    [Fact]
    public void Generate_DifferentTenant_ProducesDifferentKey()
    {
        var k1 = _sut.Generate("tenant-a", "welcome.v1", "user@example.com", "k");
        var k2 = _sut.Generate("tenant-b", "welcome.v1", "user@example.com", "k");

        k1.Should().NotBe(k2);
    }

    [Fact]
    public void Generate_DifferentRecipient_ProducesDifferentKey()
    {
        var k1 = _sut.Generate("t", "welcome.v1", "alice@example.com", null);
        var k2 = _sut.Generate("t", "welcome.v1", "bob@example.com", null);

        k1.Should().NotBe(k2);
    }

    [Fact]
    public void Generate_RecipientCaseInsensitive_ProducesSameKey()
    {
        var lower = _sut.Generate("t", "welcome.v1", "user@example.com", null);
        var upper = _sut.Generate("t", "welcome.v1", "USER@Example.COM", null);
        var padded = _sut.Generate("t", "welcome.v1", "  User@Example.com  ", null);

        upper.Should().Be(lower);
        padded.Should().Be(lower);
    }

    [Fact]
    public void Generate_NullCallerKey_StillProducesKey()
    {
        var k = _sut.Generate("t", "welcome.v1", "user@example.com", null);

        k.Should().NotBeNullOrWhiteSpace();
        k.Length.Should().Be(64); // SHA-256 hex
    }

    [Fact]
    public void Generate_DifferentCallerKey_ProducesDifferentKey()
    {
        var k1 = _sut.Generate("t", "welcome.v1", "user@example.com", "caller-1");
        var k2 = _sut.Generate("t", "welcome.v1", "user@example.com", "caller-2");

        k1.Should().NotBe(k2);
    }

    [Fact]
    public void Generate_NullVsBlankCaller_ProduceSameKey()
    {
        var nullCaller = _sut.Generate("t", "welcome.v1", "user@example.com", null);
        var blankCaller = _sut.Generate("t", "welcome.v1", "user@example.com", "   ");

        nullCaller.Should().Be(blankCaller);
    }

    [Fact]
    public void Generate_KeyIsLowercaseHex()
    {
        var k = _sut.Generate("t", "welcome.v1", "u@e.com", "c");

        k.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Generate_BlankTemplate_Throws(string? templateId)
    {
        var act = () => _sut.Generate("t", templateId!, "u@e.com", null);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Generate_BlankRecipient_Throws(string? recipient)
    {
        var act = () => _sut.Generate("t", "welcome.v1", recipient!, null);

        act.Should().Throw<ArgumentException>();
    }
}
