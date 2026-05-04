using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace Haworks.Catalog.Application.Helpers;

public static partial class TextSanitizer
{
    [GeneratedRegex("<[^>]*>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagPattern();

    [GeneratedRegex(@"\s{2,}", RegexOptions.Compiled)]
    private static partial Regex MultipleWhitespacePattern();

    public static string SanitizePlainText(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var noHtml = HtmlTagPattern().Replace(input, string.Empty);
        noHtml = System.Net.WebUtility.HtmlDecode(noHtml);
        noHtml = MultipleWhitespacePattern().Replace(noHtml, " ");
        noHtml = noHtml.Trim();

        return HtmlEncoder.Default.Encode(noHtml);
    }

    public static string SanitizeMultilineText(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var normalized = input.Replace("\r\n", "\n").Replace("\r", "\n");
        var noHtml = HtmlTagPattern().Replace(normalized, string.Empty);
        noHtml = System.Net.WebUtility.HtmlDecode(noHtml);

        var lines = noHtml.Split('\n')
            .Select(line => line.Trim())
            .ToList();

        var result = new List<string>();
        var consecutiveBlankLines = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                consecutiveBlankLines++;
                if (consecutiveBlankLines <= 2)
                    result.Add(line);
            }
            else
            {
                consecutiveBlankLines = 0;
                result.Add(HtmlEncoder.Default.Encode(line));
            }
        }

        return string.Join("\n", result).Trim();
    }
}
