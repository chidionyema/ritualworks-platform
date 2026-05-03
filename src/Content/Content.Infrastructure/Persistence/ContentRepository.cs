using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Haworks.Content.Domain.Entities;
using Haworks.Content.Domain.Interfaces;

namespace Haworks.Content.Infrastructure.Persistence;

/// <summary>
/// Repository for Content bounded context using ContentDbContext.
/// Implements IContentRepository.
/// </summary>
public class ContentContextRepository : IContentRepository
{
    private readonly ContentDbContext _context;
    private readonly ILogger<ContentContextRepository> _logger;

    public ContentContextRepository(
        ContentDbContext context,
        ILogger<ContentContextRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ContentEntity?> GetContentByIdAsync(Guid id, CancellationToken ct = default)
    {
        _logger.LogInformation("Fetching content {ContentId}", id);
        return await _context.Contents
            .AsNoTracking()
            .Include(c => c.Metadata)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<IEnumerable<ContentEntity>> GetContentsByEntityIdAsync(Guid entityId, string entityType, CancellationToken ct = default)
    {
        _logger.LogInformation("Fetching contents for entity {EntityId} type {EntityType}", entityId, entityType);
        return await _context.Contents
            .AsNoTracking()
            .Include(c => c.Metadata)
            .Where(c => c.EntityId == entityId && c.EntityType == entityType)
            .ToListAsync(ct);
    }

    public async Task<ContentEntity?> GetContentBySlugAsync(string slug, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(slug)) return null;

        _logger.LogInformation("Fetching content by slug");
        return await _context.Contents
            .AsNoTracking()
            .Include(c => c.Metadata)
            .FirstOrDefaultAsync(c => c.Slug == slug, ct);
    }

    public async Task<IEnumerable<ContentEntity>> GetContentsByTypeAsync(ContentType contentType, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        _logger.LogInformation("Fetching contents of type {ContentType}", contentType);
        return await _context.Contents
            .AsNoTracking()
            .Where(c => c.ContentType == contentType)
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
    }

    public async Task AddContentAsync(ContentEntity content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        _logger.LogInformation("Adding content {ContentId}", content.Id);
        await _context.Contents.AddAsync(content, ct);
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Content added {ContentId}", content.Id);
    }

    public async Task AddContentsAsync(IEnumerable<ContentEntity> contents, CancellationToken ct = default)
    {
        if (contents == null || !contents.Any())
        {
            _logger.LogWarning("Attempted to add empty contents list");
            throw new ArgumentException("Contents list cannot be null or empty", nameof(contents));
        }

        _logger.LogInformation("Adding {Count} contents", contents.Count());
        await _context.Contents.AddRangeAsync(contents, ct);
        // Note: SaveChanges should be called separately or as part of unit of work
    }

    public async Task UpdateContentAsync(ContentEntity content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        _logger.LogInformation("Updating content {ContentId}", content.Id);
        var existing = await _context.Contents.FindAsync(new object[] { content.Id }, ct);

        if (existing == null)
        {
            _logger.LogWarning("Content {ContentId} not found for update", content.Id);
            throw new KeyNotFoundException($"Content {content.Id} not found");
        }

        _context.Entry(existing).CurrentValues.SetValues(content);
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Content updated {ContentId}", content.Id);
    }

    public void RemoveContents(IEnumerable<ContentEntity> contents)
    {
        if (contents == null || !contents.Any())
        {
            _logger.LogWarning("Attempted to remove empty contents list");
            throw new ArgumentException("Contents list cannot be null or empty", nameof(contents));
        }

        _logger.LogInformation("Removing {Count} contents", contents.Count());
        _context.Contents.RemoveRange(contents);
        // Note: SaveChanges should be called separately or as part of unit of work
    }

    public async Task RemoveContentAsync(ContentEntity content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        _logger.LogInformation("Removing content {ContentId}", content.Id);

        // The argument was loaded via AsNoTracking elsewhere, so its xmin
        // concurrency token isn't tracked. Re-fetch the tracked entity so EF
        // includes the right xmin in the DELETE WHERE clause and avoids a
        // spurious DbUpdateConcurrencyException.
        var tracked = await _context.Contents.FirstOrDefaultAsync(c => c.Id == content.Id, ct);
        if (tracked is null)
        {
            return;
        }
        _context.Contents.Remove(tracked);
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Content removed {ContentId}", content.Id);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Saving content changes");
        await _context.SaveChangesAsync(ct);
    }

    #region Content Metadata Methods

    public async Task AddContentMetadataAsync(ContentMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        _logger.LogInformation("Adding metadata for content {ContentId}", metadata.ContentId);
        await _context.ContentMetadata.AddAsync(metadata, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<IEnumerable<ContentMetadata>> GetContentMetadataAsync(Guid contentId, CancellationToken ct = default)
    {
        return await _context.ContentMetadata
            .AsNoTracking()
            .Where(m => m.ContentId == contentId)
            .ToListAsync(ct);
    }

    public async Task UpdateContentMetadataAsync(ContentMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var existing = await _context.ContentMetadata.FindAsync(new object[] { metadata.Id }, ct);
        if (existing == null)
        {
            throw new KeyNotFoundException($"Metadata {metadata.Id} not found");
        }

        _context.Entry(existing).CurrentValues.SetValues(metadata);
        await _context.SaveChangesAsync(ct);
    }

    public async Task DeleteContentMetadataAsync(Guid metadataId, CancellationToken ct = default)
    {
        var metadata = await _context.ContentMetadata.FindAsync(new object[] { metadataId }, ct);
        if (metadata == null) return;

        _context.ContentMetadata.Remove(metadata);
        await _context.SaveChangesAsync(ct);
    }

    #endregion

    #region Content Version Methods

    public async Task<IEnumerable<ContentVersion>> GetContentVersionsAsync(Guid contentId, CancellationToken ct = default)
    {
        return await _context.ContentVersions
            .AsNoTracking()
            .Where(v => v.ContentId == contentId)
            .OrderByDescending(v => v.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task AddContentVersionAsync(ContentVersion version, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(version);

        _logger.LogInformation("Adding version for content {ContentId}", version.ContentId);
        await _context.ContentVersions.AddAsync(version, ct);
        await _context.SaveChangesAsync(ct);
    }

    #endregion
}
