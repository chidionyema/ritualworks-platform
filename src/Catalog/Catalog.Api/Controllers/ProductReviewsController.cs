using System.Security.Claims;
using Haworks.BuildingBlocks.CurrentUser;
using Haworks.Catalog.Api.Models;
using Haworks.Catalog.Application.Commands;
using Haworks.Catalog.Application.Queries;
using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Extensions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Catalog.Api.Controllers;

[Route("api/v{version:apiVersion}/products/{productId}/reviews")]
[ApiController]
public class ProductReviewsController : ControllerBase
{
    private readonly IMediator _mediator;

    public ProductReviewsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    [ProducesResponseType(typeof(List<ProductReviewResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReviews(Guid productId, [FromQuery] int skip = 0, [FromQuery] int take = 20, CancellationToken cancellationToken = default)
    {
        skip = Math.Max(skip, 0);
        take = Math.Clamp(take, 1, 100);
        // Currently omitting the "includeUnapproved" check per the simplified query ported
        var result = await _mediator.Send(new GetProductReviewsQuery(productId, skip, take), cancellationToken);

        if (!result.IsSuccess)
            return result.ToActionResult();

        var responses = result.Value.Select(dto => new ProductReviewResponse(
            dto.Id,
            dto.ProductId,
            dto.UserId,
            dto.AuthorName,
            dto.Title,
            dto.Body,
            dto.Rating,
            dto.IsApproved,
            dto.CreatedAt,
            dto.UpdatedAt)).ToList();

        return Ok(responses);
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ProductReviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReview(Guid productId, Guid id, CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetForwardedUserId();
        bool isAdmin = User.IsInRole("Admin");

        var result = await _mediator.Send(new GetProductReviewQuery(productId, id, userId, isAdmin), cancellationToken);

        if (!result.IsSuccess)
            return result.ToActionResult();

        var dto = result.Value;
        var response = new ProductReviewResponse(
            dto.Id,
            dto.ProductId,
            dto.UserId,
            dto.AuthorName,
            dto.Title,
            dto.Body,
            dto.Rating,
            dto.IsApproved,
            dto.CreatedAt,
            dto.UpdatedAt);

        return Ok(response);
    }

    [HttpPost]
    [Authorize]
    [ProducesResponseType(typeof(ProductReviewResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateReview(Guid productId, [FromBody] CreateProductReviewRequest request, CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetForwardedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        bool isAdmin = User.IsInRole("Admin");

        var result = await _mediator.Send(new CreateProductReviewCommand(
            productId,
            request.Title,
            request.Content,
            request.Rating,
            userId,
            request.AuthorName,
            isAdmin), cancellationToken);

        if (!result.IsSuccess)
            return result.ToActionResult();

        var dto = result.Value;
        var response = new ProductReviewResponse(
            dto.Id,
            dto.ProductId,
            dto.UserId,
            dto.AuthorName,
            dto.Title,
            dto.Body,
            dto.Rating,
            dto.IsApproved,
            dto.CreatedAt,
            dto.UpdatedAt);

        return CreatedAtAction(nameof(GetReview), new { productId, id = dto.Id }, response);
    }

    [HttpPut("{id}")]
    [Authorize]
    [ProducesResponseType(typeof(ProductReviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateReview(Guid productId, Guid id, [FromBody] CreateProductReviewRequest request, CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetForwardedUserId();
        bool isAdmin = User.IsInRole("Admin");

        var result = await _mediator.Send(new UpdateProductReviewCommand(
            productId,
            id,
            request.Title,
            request.Content,
            request.Rating,
            userId,
            isAdmin), cancellationToken);

        if (!result.IsSuccess)
            return result.ToActionResult();

        var dto = result.Value;
        var response = new ProductReviewResponse(
            dto.Id,
            dto.ProductId,
            dto.UserId,
            dto.AuthorName,
            dto.Title,
            dto.Body,
            dto.Rating,
            dto.IsApproved,
            dto.CreatedAt,
            dto.UpdatedAt);

        return Ok(response);
    }

    [HttpDelete("{id}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteReview(Guid productId, Guid id, CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetForwardedUserId();
        bool isAdmin = User.IsInRole("Admin");

        var result = await _mediator.Send(new DeleteProductReviewCommand(productId, id, userId, isAdmin), cancellationToken);
        if (result.IsFailure) return result.ToActionResult();

        return NoContent();
    }

    [HttpPost("{id}/approve")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ApproveReview(Guid productId, Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ApproveProductReviewCommand(productId, id), cancellationToken);
        if (result.IsFailure) return result.ToActionResult();

        return NoContent();
    }
}
