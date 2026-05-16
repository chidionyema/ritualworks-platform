using Haworks.BuildingBlocks.CurrentUser;

namespace Haworks.Media.Api.Application;

public record ListMediaQuery(
    int Page = 1,
    int PageSize = 20,
    string? Status = null,
    string? MimeTypePrefix = null) : IRequest<Result<PagedResult<MediaSummary>>>;

public record MediaSummary(Guid Id, string FileName, string MimeType, long Size, string Status, DateTime CreatedAt);
public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

public class ListMediaValidator : AbstractValidator<ListMediaQuery>
{
    public ListMediaValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
    }
}

public class ListMediaHandler(
    MediaDbContext context,
    ICurrentUserService currentUser) : IRequestHandler<ListMediaQuery, Result<PagedResult<MediaSummary>>>
{
    public async Task<Result<PagedResult<MediaSummary>>> Handle(ListMediaQuery request, CancellationToken ct)
    {
        var ownerId = currentUser.UserId;
        if (string.IsNullOrEmpty(ownerId))
            return Result.Failure<PagedResult<MediaSummary>>(new Error("Media.Unauthorized", "Authenticated user identity could not be resolved."));

        var query = context.MediaFiles.AsNoTracking().Where(f => f.OwnerId == ownerId);

        if (!string.IsNullOrEmpty(request.Status) && Enum.TryParse<MediaStatus>(request.Status, true, out var status))
            query = query.Where(f => f.Status == status);

        if (!string.IsNullOrEmpty(request.MimeTypePrefix))
            query = query.Where(f => f.MimeType.StartsWith(request.MimeTypePrefix));

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(f => f.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(f => new MediaSummary(f.Id, f.FileName, f.MimeType, f.Size, f.Status.ToString(), f.CreatedAt))
            .ToListAsync(ct);

        return new PagedResult<MediaSummary>(items, totalCount, request.Page, request.PageSize);
    }
}
