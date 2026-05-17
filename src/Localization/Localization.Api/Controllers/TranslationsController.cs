using Microsoft.AspNetCore.Authorization;
using Haworks.BuildingBlocks.Common;
using Haworks.Localization.Api.Application;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Localization.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class TranslationsController : ControllerBase
{
    private readonly IMediator _mediator;

    public TranslationsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("{key}")]
    public async Task<IActionResult> Get(string key, [FromQuery] string locale = "en-US")
    {
        var result = await _mediator.Send(new GetTranslationQuery(key, locale));
        return result.ToActionResult();
    }

    [HttpPut("{key}")]
    public async Task<IActionResult> Upsert(string key, [FromBody] UpsertTranslationRequest request)
    {
        var userId = User.FindFirst("sub")?.Value ?? "anonymous";
        var result = await _mediator.Send(new UpsertTranslationCommand(key, request.Locale, request.Value, userId));
        return result.ToActionResult();
    }
}

public sealed record UpsertTranslationRequest(string Locale, string Value);
