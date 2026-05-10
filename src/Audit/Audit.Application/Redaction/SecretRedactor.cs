using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

namespace Haworks.Audit.Application.Redaction;

public sealed class SecretRedactor : ISecretRedactor
{
    private static readonly string[] DenyListSuffixes = 
        { "token", "password", "secret", "key", "credential", "apikey", "authorization" };

    private static readonly Regex CreditCardRegex = new Regex(@"\b(?:\d[ -]*?){13,19}\b", RegexOptions.Compiled);
    private static readonly string[] CvvFields = { "cvv", "cvc", "securityCode" };

    public JsonElement Redact(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Null || input.ValueKind == JsonValueKind.Undefined)
            return input;

        var node = JsonNode.Parse(input.GetRawText());
        if (node == null) return input;

        RedactNode(node);

        return JsonSerializer.SerializeToElement(node);
    }

    private void RedactNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var properties = obj.ToList();
            foreach (var prop in properties)
            {
                var key = prop.Key;
                var value = prop.Value;

                if (IsDenyListed(key))
                {
                    obj.Remove(key);
                }
                else if (string.Equals(key, "RawBody", StringComparison.OrdinalIgnoreCase))
                {
                    var val = value?.ToString();
                    obj.Remove(key);
                    if (val != null)
                    {
                        obj["RawBodySha256"] = HashSha256(val);
                    }
                }
                else if (CvvFields.Any(f => string.Equals(f, key, StringComparison.OrdinalIgnoreCase)))
                {
                    obj.Remove(key);
                }
                else if (value is JsonValue jVal && jVal.TryGetValue<string>(out var str))
                {
                    obj[key] = RedactString(str);
                }
                else if (value != null)
                {
                    RedactNode(value);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                RedactNode(arr[i]);
            }
        }
    }

    private bool IsDenyListed(string key)
    {
        foreach (var suffix in DenyListSuffixes)
        {
            if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private string RedactString(string input)
    {
        return CreditCardRegex.Replace(input, m => 
        {
            var digitsOnly = new string(m.Value.Where(char.IsDigit).ToArray());
            if (IsValidLuhn(digitsOnly))
            {
                return string.Concat("****", digitsOnly.AsSpan(digitsOnly.Length - 4));
            }
            return m.Value;
        });
    }

    private bool IsValidLuhn(string digits)
    {
        if (digits.Length < 13) return false;
        int sum = 0;
        bool alternate = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int n = digits[i] - '0';
            if (alternate)
            {
                n *= 2;
                if (n > 9) n -= 9;
            }
            sum += n;
            alternate = !alternate;
        }
        return (sum % 10 == 0);
    }

    private string HashSha256(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
