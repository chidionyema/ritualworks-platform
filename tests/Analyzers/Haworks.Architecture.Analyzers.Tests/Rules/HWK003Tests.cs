using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK003Tests
{
    [Fact]
    public async Task FinancialDecimal_WithoutHasColumnType_Reports()
    {
        const string source = """
            using System;
            using System.Linq.Expressions;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;
            public class Payment { public decimal Amount { get; set; } }
            public class PaymentConfig
            {
                public void Configure(EntityTypeBuilder<Payment> builder)
                {
                    {|#0:builder.Property(x => x.Amount)|}.IsRequired();
                }
            }
            """;

        var expected = CSharpAnalyzerVerifier<HWK003_FinancialDecimalRequiresNumericColumnTypeAnalyzer>
            .Diagnostic(Diagnostics.FinancialDecimalRequiresColumnType)
            .WithLocation(0)
            .WithArguments("Amount");

        await CSharpAnalyzerVerifier<HWK003_FinancialDecimalRequiresNumericColumnTypeAnalyzer>
            .VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task FinancialDecimal_WithHasColumnType_NoDiagnostic()
    {
        const string source = """
            using System;
            using System.Linq.Expressions;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;
            public class Payment { public decimal Amount { get; set; } }
            public class PaymentConfig
            {
                public void Configure(EntityTypeBuilder<Payment> builder)
                {
                    builder.Property(x => x.Amount).HasColumnType("numeric(18,2)");
                }
            }
            """;

        await CSharpAnalyzerVerifier<HWK003_FinancialDecimalRequiresNumericColumnTypeAnalyzer>
            .VerifyNoDiagnosticsAsync(source);
    }

    [Fact]
    public async Task NonFinancialDecimal_NoDiagnostic()
    {
        const string source = """
            using System;
            using System.Linq.Expressions;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;
            public class Location { public decimal Latitude { get; set; } }
            public class LocationConfig
            {
                public void Configure(EntityTypeBuilder<Location> builder)
                {
                    builder.Property(x => x.Latitude).IsRequired();
                }
            }
            """;

        await CSharpAnalyzerVerifier<HWK003_FinancialDecimalRequiresNumericColumnTypeAnalyzer>
            .VerifyNoDiagnosticsAsync(source);
    }
}
