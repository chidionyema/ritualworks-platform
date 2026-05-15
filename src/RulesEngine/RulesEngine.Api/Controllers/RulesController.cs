using Haworks.RulesEngine.Api.Application;
using Haworks.BuildingBlocks.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.RulesEngine.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RulesController : ControllerBase
{
    private readonly IMediator _mediator;

    public RulesController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("evaluate")]
    public async Task<IActionResult> Evaluate([FromBody] EvaluateRuleQuery query)
    {
        var result = await _mediator.Send(query);
        return result.ToActionResult();
    }
}
