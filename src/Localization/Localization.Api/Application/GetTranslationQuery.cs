using Haworks.BuildingBlocks.Common;
using MediatR;

namespace Haworks.Localization.Api.Application;

public record GetTranslationQuery(string Key, string Locale) : IRequest<Result<string>>;
