using Xunit;
using System.Text.Json;
using FluentAssertions;
using Haworks.Audit.Application.Redaction;

namespace Haworks.Audit.Unit.Redaction;

public class SecretRedactorTests
{
    private readonly SecretRedactor _redactor = new();

    [Fact]
    public void Redact_DenyListedSuffix_DropsField()
    {
        var input = JsonSerializer.SerializeToElement(new
        {
            apiToken = "secret-token",
            password = "pwd",
            safeField = "safe"
        });

        var result = _redactor.Redact(input);

        result.TryGetProperty("apiToken", out _).Should().BeFalse();
        result.TryGetProperty("password", out _).Should().BeFalse();
        result.GetProperty("safeField").GetString().Should().Be("safe");
    }

    [Fact]
    public void Redact_RawBody_ReplacesWithHash()
    {
        var input = JsonSerializer.SerializeToElement(new
        {
            RawBody = "original-body"
        });

        var result = _redactor.Redact(input);

        result.TryGetProperty("RawBody", out _).Should().BeFalse();
        result.TryGetProperty("RawBodySha256", out _).Should().BeTrue();
        result.GetProperty("RawBodySha256").GetString().Should().NotBe("original-body");
    }

    [Fact]
    public void Redact_CreditCard_RedactsValidLuhn()
    {
        // Valid Luhn (Visa test card)
        var input = JsonSerializer.SerializeToElement(new
        {
            card = "4242 4242 4242 4242",
            notACard = "1234 5678 9012 3456" // Invalid Luhn
        });

        var result = _redactor.Redact(input);

        result.GetProperty("card").GetString().Should().Be("****4242");
        result.GetProperty("notACard").GetString().Should().Be("1234 5678 9012 3456");
    }

    [Fact]
    public void Redact_CvvField_DropsField()
    {
        var input = JsonSerializer.SerializeToElement(new
        {
            cvv = "123",
            securityCode = "456"
        });

        var result = _redactor.Redact(input);

        result.TryGetProperty("cvv", out _).Should().BeFalse();
        result.TryGetProperty("securityCode", out _).Should().BeFalse();
    }

    [Fact]
    public void Redact_Fuzzer_AssertsNoDenyListSurvives()
    {
        for (int i = 0; i < 200; i++)
        {
            var doc = GenerateRandomJson(3);
            var result = _redactor.Redact(doc);
            
            AssertNoDenyList(result);
        }
    }

    private JsonElement GenerateRandomJson(int depth)
    {
        var obj = new Dictionary<string, object>();
        var keys = new[] { "token", "password", "safe", "cvv", "RawBody", "creditCard" };
        var random = new Random();

        int count = random.Next(1, 5);
        for (int i = 0; i < count; i++)
        {
            var key = keys[random.Next(keys.Length)] + i; // Append i to avoid key collisions
            if (depth > 0 && random.Next(2) == 0)
            {
                obj[key] = GenerateRandomJson(depth - 1);
            }
            else
            {
                obj[key] = key.Contains("creditCard") ? "4539123456789012" : "value-" + i;
            }
        }
        return JsonSerializer.SerializeToElement(obj);
    }

    private void AssertNoDenyList(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var key = prop.Name;
                if (key.EndsWith("token", StringComparison.OrdinalIgnoreCase) ||
                    key.EndsWith("password", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "cvv", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "RawBody", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.Fail($"Key {key} should have been redacted");
                }
                
                AssertNoDenyList(prop.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                AssertNoDenyList(item);
            }
        }
    }
}
