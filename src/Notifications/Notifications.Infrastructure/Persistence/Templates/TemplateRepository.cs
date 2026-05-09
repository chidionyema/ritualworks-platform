using Haworks.Notifications.Application.Templates;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Notifications.Infrastructure.Persistence.Templates;

/// <summary>
/// EF Core-backed <see cref="ITemplateRepository"/>. Reads
/// <see cref="NotificationTemplate"/> rows from the <c>notifications</c>
/// schema. Read paths use <c>AsNoTracking</c> per the project's EF Core
/// performance rule; only Add/Update bring entities into the change tracker.
/// </summary>
internal sealed class TemplateRepository : ITemplateRepository
{
    private readonly NotificationsDbContext _dbContext;

    public TemplateRepository(NotificationsDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public Task<NotificationTemplate?> GetAsync(
        string templateId,
        string locale,
        NotificationChannel channel,
        int version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        ArgumentNullException.ThrowIfNull(locale);

        var channelString = channel.ToString();
        return _dbContext.NotificationTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t =>
                t.TemplateId == templateId &&
                t.Locale == locale &&
                t.Channel == channelString &&
                t.Version == version);
    }

    public async Task<IEnumerable<NotificationTemplate>> GetVersionsAsync(string templateId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);

        return await _dbContext.NotificationTemplates
            .AsNoTracking()
            .Where(t => t.TemplateId == templateId)
            .OrderByDescending(t => t.Version)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public async Task AddAsync(NotificationTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        await _dbContext.NotificationTemplates.AddAsync(template).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task UpdateAsync(NotificationTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        _dbContext.NotificationTemplates.Update(template);
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);
    }
}
