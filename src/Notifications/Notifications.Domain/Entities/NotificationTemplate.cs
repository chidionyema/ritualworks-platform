using Haworks.BuildingBlocks.Persistence;
using Haworks.Notifications.Domain.Enums;

namespace Haworks.Notifications.Domain.Entities;

public sealed class NotificationTemplate : AuditableEntity
{
    public string TemplateId { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string Category { get; private set; } = string.Empty;
    public string Channel { get; private set; } = string.Empty;
    public string Locale { get; private set; } = string.Empty;
    public string SubjectTemplate { get; private set; } = string.Empty;
    public string BodyTemplate { get; private set; } = string.Empty;
    public string? TextFallbackTemplate { get; private set; }
    public bool IsActive { get; private set; }
    public int Version { get; private set; }
    public string? RequiredVariablesJson { get; private set; }

    private NotificationTemplate() { }

    public static NotificationTemplate Create(
        string templateId, string name, string category, string channel,
        string locale, string subjectTemplate, string bodyTemplate,
        string? textFallback = null, string? requiredVariablesJson = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectTemplate);
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyTemplate);
        return new NotificationTemplate
        {
            TemplateId = templateId, Name = name, Category = category,
            Channel = channel, Locale = locale,
            SubjectTemplate = subjectTemplate, BodyTemplate = bodyTemplate,
            TextFallbackTemplate = textFallback,
            IsActive = true, Version = 1,
            RequiredVariablesJson = requiredVariablesJson
        };
    }
}
