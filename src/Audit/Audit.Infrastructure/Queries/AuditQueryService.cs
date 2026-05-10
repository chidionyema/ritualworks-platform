using Haworks.Audit.Domain;
using Haworks.Audit.Infrastructure.Persistence;
using Haworks.Audit.Application.Queries;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace Haworks.Audit.Infrastructure.Queries;

public class AuditQueryService : IAuditQueryService
{
    private readonly AuditDbContext _db;

    public AuditQueryService(AuditDbContext db)
    {
        _db = db;
    }

    public async Task<AuditPageResult> ListAsync(AuditQueryRequest request, CancellationToken ct)
    {
        var query = _db.AuditEvents.AsNoTracking();

        if (!string.IsNullOrEmpty(request.EntityType))
            query = query.Where(e => e.EntityType == request.EntityType);
        if (!string.IsNullOrEmpty(request.EntityId))
            query = query.Where(e => e.EntityId == request.EntityId);
        if (!string.IsNullOrEmpty(request.EventType))
            query = query.Where(e => e.EventType == request.EventType);
        if (request.From.HasValue)
            query = query.Where(e => e.OccurredAt >= request.From.Value);
        if (request.To.HasValue)
            query = query.Where(e => e.OccurredAt <= request.To.Value);

        // Cursor pagination
        if (!string.IsNullOrEmpty(request.Cursor))
        {
            var (lastOccurredAt, lastId) = DecodeCursor(request.Cursor);
            query = query.Where(e => e.OccurredAt < lastOccurredAt || (e.OccurredAt == lastOccurredAt && e.Id.CompareTo(lastId) < 0));
        }

        var items = await query
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Take(request.Limit + 1)
            .ToListAsync(ct);

        string? nextCursor = null;
        if (items.Count > request.Limit)
        {
            var last = items[request.Limit - 1];
            nextCursor = EncodeCursor(last.OccurredAt, last.Id);
            items.RemoveAt(request.Limit);
        }

        return new AuditPageResult(items, nextCursor);
    }

    public async Task<AuditEvent?> GetByIdAsync(Guid id, DateTimeOffset occurredAt, CancellationToken ct)
    {
        return await _db.AuditEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id && e.OccurredAt == occurredAt, ct);
    }

    private static (DateTimeOffset, Guid) DecodeCursor(string cursor)
    {
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        var parts = decoded.Split('|');
        return (DateTimeOffset.Parse(parts[0]), Guid.Parse(parts[1]));
    }

    private static string EncodeCursor(DateTimeOffset occurredAt, Guid id)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{occurredAt:O}|{id}"));
    }
}
