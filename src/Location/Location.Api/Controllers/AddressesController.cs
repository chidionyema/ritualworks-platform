using Haworks.Location.Application.Commands;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Location.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AddressesController(IMediator mediator) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<Guid>> Create(CreateAddressCommand command)
    {
        var id = await mediator.Send(command);
        return Ok(id);
    }
}
