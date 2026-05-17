using EasyPost;
using EasyPost.Models.API;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Haworks.Shipping.Api.Infrastructure;

public sealed class EasyPostOptions
{
    public const string SectionName = "EasyPost";
    public string ApiKey { get; set; } = string.Empty;
    public bool TestMode { get; set; } = true;
}

public sealed class EasyPostShippingProvider : IShippingProvider
{
    private readonly Client _client;
    private readonly ILogger<EasyPostShippingProvider> _logger;

    public EasyPostShippingProvider(IOptions<EasyPostOptions> opts, ILogger<EasyPostShippingProvider> logger)
    {
        _client = new Client(new ClientConfiguration(opts.Value.ApiKey));
        _logger = logger;
    }

    public async Task<CreateShipmentResult> CreateShipmentAsync(CreateShipmentRequest request, CancellationToken ct)
    {
        var shipment = await _client.Shipment.Create(new EasyPost.Parameters.Shipment.Create
        {
            FromAddress = new EasyPost.Parameters.Address.Create
            {
                Street1 = request.From.Street,
                City = request.From.City,
                State = request.From.State,
                Zip = request.From.Zip,
                Country = request.From.Country,
            },
            ToAddress = new EasyPost.Parameters.Address.Create
            {
                Street1 = request.To.Street,
                City = request.To.City,
                State = request.To.State,
                Zip = request.To.Zip,
                Country = request.To.Country,
            },
            Parcel = new EasyPost.Parameters.Parcel.Create
            {
                Length = request.Parcel.LengthInches,
                Width = request.Parcel.WidthInches,
                Height = request.Parcel.HeightInches,
                Weight = request.Parcel.WeightOz,
            },
        });

        var rates = shipment.Rates?.Select(r => new RateDto(
            r.Id,
            r.Carrier ?? "unknown",
            r.Service ?? "standard",
            decimal.TryParse(r.Rate, out var amt) ? amt : 0m,
            r.Currency ?? "USD",
            r.DeliveryDays
        )).ToList() ?? [];

        _logger.LogInformation("Created EasyPost shipment {ShipmentId} with {RateCount} rates",
            shipment.Id, rates.Count);

        return new CreateShipmentResult(shipment.Id, rates);
    }

    public async Task<BuyLabelResult> BuyLabelAsync(string shipmentId, string rateId, CancellationToken ct)
    {
        var shipment = await _client.Shipment.Retrieve(shipmentId);
        var rate = shipment.Rates?.FirstOrDefault(r => r.Id == rateId)
            ?? shipment.LowestRate();

        var bought = await _client.Shipment.Buy(shipmentId, rate);

        return new BuyLabelResult(
            bought.TrackingCode ?? "",
            bought.Tracker?.PublicUrl ?? "",
            bought.PostageLabel?.LabelUrl ?? "",
            rate.Carrier ?? "unknown",
            rate.Service ?? "standard",
            decimal.TryParse(rate.Rate, out var amt) ? amt : 0m,
            rate.Currency ?? "USD",
            bought.Tracker?.EstDeliveryDate);
    }

    public async Task<TrackingInfo> GetTrackingAsync(string trackingNumber, CancellationToken ct)
    {
        var tracker = await _client.Tracker.Create(trackingNumber);

        var events = tracker.TrackingDetails?.Select(d => new TrackingEvent(
            d.Datetime ?? DateTime.UtcNow,
            d.Status ?? "unknown",
            d.TrackingLocation?.City,
            d.Message
        )).ToList() ?? [];

        return new TrackingInfo(
            tracker.Status ?? "unknown",
            tracker.StatusDetail,
            tracker.EstDeliveryDate,
            events);
    }
}
