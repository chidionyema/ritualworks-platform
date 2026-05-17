using Haworks.Contracts.Shipping;
using Haworks.Shipping.Api.Domain;
using Haworks.Shipping.Api.Infrastructure;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Shipping.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class ShipmentsController(
    ShippingDbContext db,
    IShippingProvider provider,
    IPublishEndpoint publisher) : ControllerBase
{
    /// <summary>Create shipment and fetch carrier rates.</summary>
    [HttpPost]
    public async Task<IActionResult> CreateShipment([FromBody] CreateShipmentHttpRequest request, CancellationToken ct)
    {
        var result = await provider.CreateShipmentAsync(new CreateShipmentRequest(
            new AddressDto(request.FromStreet, request.FromCity, request.FromState, request.FromZip, request.FromCountry),
            new AddressDto(request.ToStreet, request.ToCity, request.ToState, request.ToZip, request.ToCountry),
            new ParcelDto(request.LengthInches, request.WidthInches, request.HeightInches, request.WeightOz)
        ), ct);

        var shipment = Shipment.Create(request.OrderId, result.ShipmentId);
        shipment.SetAddresses(
            request.FromStreet, request.FromCity, request.FromState, request.FromZip, request.FromCountry,
            request.ToStreet, request.ToCity, request.ToState, request.ToZip, request.ToCountry);

        db.Shipments.Add(shipment);
        await db.SaveChangesAsync(ct);

        return Ok(new { shipment.Id, shipment.EasyPostShipmentId, result.Rates });
    }

    /// <summary>Buy the selected (or cheapest) rate — generates label.</summary>
    [HttpPost("{id:guid}/buy")]
    public async Task<IActionResult> BuyLabel(Guid id, [FromBody] BuyLabelRequest request, CancellationToken ct)
    {
        var shipment = await db.Shipments.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (shipment == null) return NotFound();

        var result = await provider.BuyLabelAsync(shipment.EasyPostShipmentId, request.RateId, ct);

        shipment.MarkLabelPurchased(
            result.Carrier, result.Service, result.TrackingNumber,
            result.TrackingUrl, result.LabelUrl, result.Amount, result.Currency, result.EstimatedDelivery);

        await db.SaveChangesAsync(ct);

        await publisher.Publish(new ShipmentCreatedEvent
        {
            ShipmentId = shipment.Id,
            OrderId = shipment.OrderId,
            CarrierCode = result.Carrier,
            TrackingNumber = result.TrackingNumber,
            TrackingUrl = result.TrackingUrl,
        }, ct);

        return Ok(new { shipment.TrackingNumber, shipment.TrackingUrl, shipment.LabelUrl, shipment.EstimatedDelivery });
    }

    /// <summary>Get shipment details + tracking.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetShipment(Guid id, CancellationToken ct)
    {
        var shipment = await db.Shipments.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        return shipment == null ? NotFound() : Ok(shipment);
    }

    /// <summary>Get shipments for an order.</summary>
    [HttpGet("by-order/{orderId:guid}")]
    public async Task<IActionResult> GetByOrder(Guid orderId, CancellationToken ct)
    {
        var shipments = await db.Shipments.AsNoTracking()
            .Where(s => s.OrderId == orderId)
            .ToListAsync(ct);
        return Ok(shipments);
    }

    /// <summary>EasyPost tracking webhook.</summary>
    [HttpPost("webhooks/easypost")]
    [AllowAnonymous]
    public async Task<IActionResult> HandleWebhook([FromBody] EasyPostWebhookPayload payload, CancellationToken ct)
    {
        if (payload.Description != "tracker.updated" && payload.Description != "tracker.created")
            return Ok();

        var trackingCode = payload.Result?.TrackingCode;
        if (string.IsNullOrEmpty(trackingCode)) return Ok();

        var shipment = await db.Shipments.FirstOrDefaultAsync(s => s.TrackingNumber == trackingCode, ct);
        if (shipment == null) return Ok();

        var newStatus = MapEasyPostStatus(payload.Result?.Status);
        shipment.UpdateStatus(newStatus);
        await db.SaveChangesAsync(ct);

        if (newStatus == ShipmentStatus.Delivered)
        {
            await publisher.Publish(new ShipmentDeliveredEvent
            {
                ShipmentId = shipment.Id,
                OrderId = shipment.OrderId,
                DeliveredAt = shipment.DeliveredAt ?? DateTime.UtcNow,
            }, ct);
        }
        else if (newStatus == ShipmentStatus.Exception)
        {
            await publisher.Publish(new ShipmentExceptionEvent
            {
                ShipmentId = shipment.Id,
                OrderId = shipment.OrderId,
                Reason = payload.Result?.StatusDetail ?? "Unknown exception",
            }, ct);
        }

        return Ok();
    }

    private static ShipmentStatus MapEasyPostStatus(string? status) => status switch
    {
        "in_transit" => ShipmentStatus.InTransit,
        "out_for_delivery" => ShipmentStatus.OutForDelivery,
        "delivered" => ShipmentStatus.Delivered,
        "failure" or "error" => ShipmentStatus.Exception,
        "cancelled" => ShipmentStatus.Cancelled,
        _ => ShipmentStatus.InTransit,
    };
}

public sealed record CreateShipmentHttpRequest(
    Guid OrderId,
    string FromStreet, string FromCity, string FromState, string FromZip, string FromCountry,
    string ToStreet, string ToCity, string ToState, string ToZip, string ToCountry,
    double LengthInches, double WidthInches, double HeightInches, double WeightOz);

public sealed record BuyLabelRequest(string RateId);

public sealed record EasyPostWebhookPayload(string? Description, EasyPostWebhookResult? Result);
public sealed record EasyPostWebhookResult(string? TrackingCode, string? Status, string? StatusDetail);
