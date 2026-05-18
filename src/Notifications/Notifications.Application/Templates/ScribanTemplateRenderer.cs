using System.Text.Json;
using Haworks.Notifications.Domain.Entities;
using Scriban;
using Scriban.Runtime;

namespace Haworks.Notifications.Application.Templates;

/// <summary>
/// Default <see cref="ITemplateRenderer"/> backed by Scriban
/// (sandboxed, fast, .NET-native — see <c>docs/architecture/notification-service.md §7</c>).
///
/// In addition to the raw <see cref="RenderAsync"/> string-render contract the L0
/// interface ships, this implementation exposes
/// <see cref="Render(NotificationTemplate, IDictionary{string, object?})"/> which:
/// <list type="bullet">
///   <item>renders Subject + Body + (optional) TextFallback in a single pass</item>
///   <item>fails fast (<see cref="ArgumentException"/>) if a variable declared in
///         <see cref="NotificationTemplate.RequiredVariablesJson"/> is missing
///         from the supplied bag — catches drift between event schemas and
///         template authors at the dispatch boundary.</item>
/// </list>
/// </summary>
public sealed class ScribanTemplateRenderer : ITemplateRenderer
{
    private static readonly IReadOnlyList<string> EmptyRequired = Array.Empty<string>();

    /// <inheritdoc/>
    public Task<string> RenderAsync(string template, IDictionary<string, object> data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(data);

        var bag = data.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        var context = BuildContext(bag);
        return Task.FromResult(RenderOne(template, context));
    }

    /// <summary>
    /// Render a full <see cref="NotificationTemplate"/> (Subject + Body + TextFallback)
    /// against <paramref name="data"/>, validating required variables first.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when a required variable declared by the template is missing from
    /// <paramref name="data"/>. The exception message contains the missing key name.
    /// </exception>
    public static RenderedNotification Render(NotificationTemplate template, IDictionary<string, object?> data)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(data);

        ValidateRequiredVariables(template, data);

        var context = BuildContext(data);
        var subject = RenderOne(template.SubjectTemplate, context);
        var body = RenderOne(template.BodyTemplate, context);
        var textFallback = string.IsNullOrEmpty(template.TextFallbackTemplate)
            ? null
            : RenderOne(template.TextFallbackTemplate!, context);

        return new RenderedNotification(subject, body, textFallback);
    }

    private static void ValidateRequiredVariables(
        NotificationTemplate template,
        IDictionary<string, object?> data)
    {
        if (string.IsNullOrWhiteSpace(template.RequiredVariablesJson)) return;

        IReadOnlyList<string> required;
        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(template.RequiredVariablesJson!);
            required = parsed is null ? EmptyRequired : parsed;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Template '{template.TemplateId}' v{template.Version} has malformed RequiredVariablesJson.",
                ex);
        }

        foreach (var key in required)
        {
            if (string.IsNullOrEmpty(key)) continue;
            if (!data.TryGetValue(key, out var value) || value is null)
            {
                throw new ArgumentException(
                    $"missing variable: {key}",
                    nameof(data));
            }
        }
    }

    private static TemplateContext BuildContext(IDictionary<string, object?> data)
    {
        var script = new ScriptObject();
        foreach (var (key, value) in data)
        {
            script[key] = value;
        }

        var context = new TemplateContext { StrictVariables = false };
        context.PushGlobal(script);
        return context;
    }

    private static string RenderOne(string source, TemplateContext context)
    {
        if (string.IsNullOrEmpty(source)) return string.Empty;

        var parsed = Template.Parse(source);
        if (parsed.HasErrors)
        {
            var messages = string.Join("; ", parsed.Messages);
            throw new InvalidOperationException($"Scriban parse error: {messages}");
        }

        return parsed.Render(context);
    }
}
