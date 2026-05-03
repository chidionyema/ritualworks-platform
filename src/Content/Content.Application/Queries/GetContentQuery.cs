using Haworks.Content.Application.DTOs;
using Haworks.BuildingBlocks.Common;
using Microsoft.Extensions.Logging;
using Haworks.Content.Domain.Interfaces;
using MediatR;

namespace Haworks.Content.Application.Queries;

public sealed record GetContentQuery(Guid ContentId) : IRequest<Result<ContentDto>>;

internal sealed class GetContentQueryHandler : IRequestHandler<GetContentQuery, Result<ContentDto>>
{
    private readonly IContentRepository _contentRepository;
    private readonly ILogger<GetContentQueryHandler> _logger;

    public GetContentQueryHandler(
        IContentRepository contentRepository,
        ILogger<GetContentQueryHandler> logger)
    {
        _contentRepository = contentRepository;
        _logger = logger;
    }

    public async Task<Result<ContentDto>> Handle(GetContentQuery request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Retrieving content. ContentId: {ContentId}", request.ContentId);

        var content = await _contentRepository.GetContentByIdAsync(request.ContentId, cancellationToken);

        if (content == null)
        {
            return Result.Failure<ContentDto>(Error.NotFound("Content.NotFound",
                $"Content with ID {request.ContentId} not found."));
        }

        return Result.Success(new ContentDto(
            content.Id,
            content.EntityId,
            content.EntityType,
            content.Path,
            content.ContentType.ToString(),
            content.FileSize));
    }
}
