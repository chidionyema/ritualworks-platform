using Haworks.Identity.Api.Models;
using Haworks.Identity.Application.Commands.Users;
using Haworks.BuildingBlocks.CurrentUser;
using Haworks.Identity.Application.Queries.Users;
using Haworks.BuildingBlocks.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Identity.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v{version:apiVersion}/[controller]")]
public class UserProfileController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ICurrentUserService _currentUserService;

    public UserProfileController(
        IMediator mediator,
        ICurrentUserService currentUserService)
    {
        _mediator = mediator;
        _currentUserService = currentUserService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(ProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        var userId = _currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var result = await _mediator.Send(new GetUserProfileQuery(userId), cancellationToken);

        if (!result.IsSuccess)
            return result.ToActionResult();

        var dto = result.Value;
        var response = new ProfileResponse(
            dto.FirstName,
            dto.LastName,
            dto.Email,
            dto.Phone,
            dto.Address,
            dto.City,
            dto.State,
            dto.PostalCode,
            dto.Country,
            dto.Bio,
            dto.Website,
            dto.AvatarUrl);

        return Ok(response);
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        var userId = _currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var result = await _mediator.Send(new UpdateUserProfileCommand(
            userId,
            request.FirstName,
            request.LastName,
            request.Phone,
            request.Address,
            request.City,
            request.State,
            request.PostalCode,
            request.Country,
            request.Bio,
            request.Website
        ), cancellationToken);

        if (result.IsSuccess)
            return Ok(new { message = "Profile updated successfully" });

        return result.ToActionResult();
    }

    [HttpPost("shipping-info")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SaveShippingInfo([FromBody] SaveShippingInfoRequest request, CancellationToken cancellationToken)
    {
        var userId = _currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var result = await _mediator.Send(new SaveShippingInfoCommand(
            userId,
            request.FirstName,
            request.LastName,
            request.Address,
            request.City,
            request.State,
            request.PostalCode,
            request.Country,
            request.Phone
        ), cancellationToken);

        if (result.IsSuccess)
            return Ok(new { message = "Shipping information saved successfully" });

        return result.ToActionResult();
    }
}
