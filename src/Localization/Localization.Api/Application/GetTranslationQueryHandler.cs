using Haworks.BuildingBlocks.Common;
using Haworks.Contracts.Localization;
using Haworks.Localization.Api.Domain;
using Haworks.Localization.Api.Infrastructure;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Localization.Api.Application;

public class GetTranslationQueryHandler : IRequestHandler<GetTranslationQuery, Result<string>>
{
    private readonly LocalizationDbContext _dbContext;
    private readonly IPublishEndpoint? _publishEndpoint;
    private const string DefaultLocale = "en-US";

    public GetTranslationQueryHandler(LocalizationDbContext dbContext, IPublishEndpoint? publishEndpoint = null)
    {
        _dbContext = dbContext;
        _publishEndpoint = publishEndpoint;
    }

    public async Task<Result<string>> Handle(GetTranslationQuery request, CancellationToken cancellationToken)
    {
        var translation = await _dbContext.Translations
            .FirstOrDefaultAsync(t => t.Key == request.Key, cancellationToken);

        if (translation == null)
        {
            await PublishMissingEvent(request.Key, request.Locale, cancellationToken);
            return Result.Failure<string>(Error.NotFound("Translation.NotFound", $"Translation for key '{request.Key}' not found."));
        }

        var localesToTry = GetLocaleHierarchy(request.Locale);
        foreach (var locale in localesToTry)
        {
            if (translation.Values.TryGetValue(locale, out var value))
            {
                return Result.Success(value);
            }
        }

        await PublishMissingEvent(request.Key, request.Locale, cancellationToken);
        return Result.Failure<string>(Error.NotFound("Translation.ValueNotFound", $"Translation for key '{request.Key}' and locale '{request.Locale}' (and fallbacks) not found."));
    }

    private static IEnumerable<string> GetLocaleHierarchy(string locale)
    {
        yield return locale;

        if (locale.Contains('-', StringComparison.Ordinal))
        {
            yield return locale.Split('-')[0];
        }

        if (!string.Equals(locale, DefaultLocale, StringComparison.OrdinalIgnoreCase))
        {
            yield return DefaultLocale;
        }
    }

    private async Task PublishMissingEvent(string key, string locale, CancellationToken cancellationToken)
    {
        if (_publishEndpoint is null) return;

        await _publishEndpoint.Publish(
            new TranslationMissingEvent { Key = key, Locale = locale },
            cancellationToken);
    }
}
