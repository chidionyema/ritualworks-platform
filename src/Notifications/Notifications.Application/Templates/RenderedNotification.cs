namespace Haworks.Notifications.Application.Templates;

/// <summary>
/// Result of rendering a <see cref="Haworks.Notifications.Domain.Entities.NotificationTemplate"/>
/// against a supplied variable bag.
/// </summary>
/// <param name="Subject">Rendered subject line (may be empty for SMS/Push).</param>
/// <param name="Body">Rendered primary body (HTML for email, plain for SMS/Push).</param>
/// <param name="TextFallback">Plain-text fallback for HTML bodies (null when not provided).</param>
public sealed record RenderedNotification(string Subject, string Body, string? TextFallback);
