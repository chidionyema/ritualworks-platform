using Haworks.BuildingBlocks.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Haworks.BffWeb.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/brand")]
[AllowAnonymous]
public class BrandController(IOptionsSnapshot<BrandOptions> brandOptions) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Get()
    {
        return Ok(new
        {
            name = brandOptions.Value.Name,
            supportEmail = brandOptions.Value.SupportEmail,
            primaryUrl = brandOptions.Value.PrimaryUrl,
            logoUrl = brandOptions.Value.LogoUrl,
            legalName = brandOptions.Value.LegalName
        });
    }
}
