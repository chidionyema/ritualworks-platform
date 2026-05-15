using Haworks.BuildingBlocks.Common;
using Haworks.Localization.Api.Application;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Localization.Api.Controllers;

[ApiController]
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
}
