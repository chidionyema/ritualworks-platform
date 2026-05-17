using FluentValidation;
using Haworks.BuildingBlocks.Common;
using Haworks.Contracts.Localization;
using Haworks.Localization.Api.Domain;
using Haworks.Localization.Api.Infrastructure;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Haworks.Localization.Api.Application;

public sealed record UpsertTranslationCommand(string Key, string Locale, string Value, string UpdatedBy) : IRequest<Result<Guid>>;

public sealed class UpsertTranslationCommandValidator : AbstractValidator<UpsertTranslationCommand>
{
    // Matches locale patterns like "en", "en-US", "zh-Hans" (2-5 char segments separated by hyphen)
    private const string LocalePattern = @"^[a-z]{2,3}(-[A-Za-z]{2,8})?$";

    public UpsertTranslationCommandValidator()
    {
        RuleFor(x => x.Key).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Locale)
            .NotEmpty()
            .Matches(LocalePattern)
            .WithMessage("Locale must match a valid format (e.g. 'en', 'en-US', 'fr-FR').");
        RuleFor(x => x.Value).NotEmpty().MaximumLength(10000);
        RuleFor(x => x.UpdatedBy).NotEmpty().MaximumLength(256);
    }
}

public sealed class UpsertTranslationCommandHandler : IRequestHandler<UpsertTranslationCommand, Result<Guid>>
{
    private readonly LocalizationDbContext _dbContext;
    private readonly IPublishEndpoint _publishEndpoint;

    public UpsertTranslationCommandHandler(LocalizationDbContext dbContext, IPublishEndpoint publishEndpoint)
    {
        _dbContext = dbContext;
        _publishEndpoint = publishEndpoint;
    }

    public async Task<Result<Guid>> Handle(UpsertTranslationCommand request, CancellationToken cancellationToken)
    {
        var translation = await _dbContext.Translations
            .FirstOrDefaultAsync(t => t.Key == request.Key, cancellationToken);

        if (translation is null)
        {
            translation = new Translation(request.Key, new Dictionary<string, string>
            {
                [request.Locale] = request.Value
            });
            _dbContext.Translations.Add(translation);
        }
        else
        {
            translation.UpdateValue(request.Locale, request.Value, request.UpdatedBy);
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Concurrent insert for the same key — retry as update
            _dbContext.ChangeTracker.Clear();
            translation = await _dbContext.Translations
                .FirstOrDefaultAsync(t => t.Key == request.Key, cancellationToken)
                ?? throw new InvalidOperationException("Translation disappeared after unique constraint violation.");
            translation.UpdateValue(request.Locale, request.Value, request.UpdatedBy);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        await _publishEndpoint.Publish(new TranslationUpdatedEvent
        {
            TranslationId = translation.Id,
            Key = request.Key,
            Locale = request.Locale,
            Value = request.Value,
            UpdatedBy = request.UpdatedBy
        }, cancellationToken);

        return Result.Success(translation.Id);
    }
}
