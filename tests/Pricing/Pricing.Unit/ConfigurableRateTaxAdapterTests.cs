using FluentAssertions;
using Haworks.Pricing.Application.Options;
using Haworks.Pricing.Domain.Exceptions;
using Haworks.Pricing.Infrastructure.Adapters;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Haworks.Pricing.Unit;

[Trait("Category", "TaxAdapter")]
public sealed class ConfigurableRateTaxAdapterTests
{
    private static ConfigurableRateTaxAdapter CreateAdapter(TaxOptions options)
    {
        return new ConfigurableRateTaxAdapter(
            Options.Create(options),
            NullLogger<ConfigurableRateTaxAdapter>.Instance);
    }

    private static TaxOptions DefaultOptions() => new()
    {
        FailOpen = false,
        Rates = new List<TaxRateEntry>
        {
            new() { Country = "US", State = "CA", Rate = 0.0925m },
            new() { Country = "US", State = "TX", Rate = 0.0625m },
            new() { Country = "US", State = null, Rate = 0.05m },
            new() { Country = "GB", State = null, Rate = 0.20m },
            new() { Country = "*", State = null, Rate = 0.0m },
        },
    };

    [Fact]
    public async Task CalculateAsync_ExactCountryAndState_ReturnsRate()
    {
        var adapter = CreateAdapter(DefaultOptions());

        var result = await adapter.CalculateAsync("US", "CA", 100m, "USD");

        result.EffectiveRate.Should().Be(0.0925m);
        result.TaxAmount.Should().Be(9.25m);
        result.Source.Should().Be("RateTable");
    }

    [Fact]
    public async Task CalculateAsync_FallsBackToCountryWildcard()
    {
        var adapter = CreateAdapter(DefaultOptions());

        var result = await adapter.CalculateAsync("US", "ZZ", 100m, "USD");

        result.EffectiveRate.Should().Be(0.05m);
        result.TaxAmount.Should().Be(5m);
    }

    [Fact]
    public async Task CalculateAsync_FallsBackToGlobalWildcard()
    {
        var adapter = CreateAdapter(DefaultOptions());

        var result = await adapter.CalculateAsync("ZZ", null, 100m, "USD");

        result.EffectiveRate.Should().Be(0m);
        result.TaxAmount.Should().Be(0m);
    }

    [Fact]
    public async Task CalculateAsync_NoMatch_FailOpenTrue_ReturnsZero()
    {
        var options = new TaxOptions
        {
            FailOpen = true,
            Rates = new List<TaxRateEntry>(), // Empty — no match possible
        };
        var adapter = CreateAdapter(options);

        var result = await adapter.CalculateAsync("US", "CA", 100m, "USD");

        result.TaxAmount.Should().Be(0m);
        result.EffectiveRate.Should().Be(0m);
    }

    [Fact]
    public async Task CalculateAsync_NoMatch_FailOpenFalse_Throws()
    {
        var options = new TaxOptions
        {
            FailOpen = false,
            Rates = new List<TaxRateEntry>(), // Empty — no match possible
        };
        var adapter = CreateAdapter(options);

        var act = () => adapter.CalculateAsync("US", "CA", 100m, "USD");

        await act.Should().ThrowAsync<TaxCalculationException>()
            .WithMessage("*No tax rate configured*");
    }

    [Fact]
    public async Task CalculateAsync_NullCountry_ReturnsZeroTax()
    {
        var adapter = CreateAdapter(DefaultOptions());

        var result = await adapter.CalculateAsync(null, null, 100m, "USD");

        result.TaxAmount.Should().Be(0m);
        result.Source.Should().Be("None");
    }

    [Fact]
    public async Task CalculateAsync_RoundsCorrectly()
    {
        var options = new TaxOptions
        {
            Rates = new List<TaxRateEntry>
            {
                new() { Country = "US", State = "CA", Rate = 0.0925m },
            },
        };
        var adapter = CreateAdapter(options);

        // 33.33 * 0.0925 = 3.083025 -> rounds to 3.083
        var result = await adapter.CalculateAsync("US", "CA", 33.33m, "USD");

        result.TaxAmount.Should().Be(3.083m);
    }

    [Fact]
    public async Task CalculateAsync_CaseInsensitiveMatching()
    {
        var adapter = CreateAdapter(DefaultOptions());

        var result = await adapter.CalculateAsync("us", "ca", 100m, "USD");

        result.EffectiveRate.Should().Be(0.0925m);
    }
}
