using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Haworks.Catalog.Application.Commands;
using Haworks.Catalog.Application.Queries;

namespace Haworks.Catalog.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class CategoriesController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => (await mediator.Send(new ListCategoriesQuery(), ct)).ToActionResult();

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] CreateCategoryCommand command, CancellationToken ct)
    {
        var result = await mediator.Send(command, ct);
        return result.ToCreatedActionResult(nameof(List), new { id = result.IsSuccess ? result.Value : Guid.Empty });
    }
}
