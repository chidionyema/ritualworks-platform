using System.Diagnostics.Metrics;
using Haworks.Contracts.Checkout;
using Haworks.RulesEngine.Api.Domain;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Haworks.RulesEngine.Api.Infrastructure;

/// <summary>
/// Consumes FraudCheckRequestedEvent from CheckoutOrchestrator.
/// Evaluates all active fraud rules against the checkout context.
/// Publishes FraudCheckPassedEvent or FraudCheckFailedEvent.
/// </summary>
public sealed class FraudCheckConsumer(
    RulesDbContext db,
    IRulesEvaluator evaluator,
    IPublishEndpoint publisher,
    ILogger<FraudCheckConsumer> logger) : IConsumer<FraudCheckRequestedEvent>
{
    private static readonly Meter Meter = new("Haworks.RulesEngine", "1.0.0");
    private static readonly Counter<long> ChecksRun = Meter.CreateCounter<long>(
        "fraud.checks.total", description: "Total fraud checks evaluated");
    private static readonly Counter<long> ChecksFailed = Meter.CreateCounter<long>(
        "fraud.checks.failed", description: "Fraud checks that triggered rules");

    public async Task Consume(ConsumeContext<FraudCheckRequestedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        // Load all active fraud rules (category = "fraud")
        var rules = await db.Set<Rule>()
            .Where(r => r.IsActive && r.Name.StartsWith("fraud:"))
            .ToListAsync(ct);

        var triggeredRules = new List<string>();
        var riskScore = 0;

        // Build the evaluation context from the event
        var variables = new Dictionary<string, object>
        {
            ["totalAmount"] = (double)msg.TotalAmount,
            ["itemCount"] = msg.ItemCount,
            ["isGuest"] = msg.IsGuest,
            ["currency"] = msg.Currency,
            ["countryCode"] = msg.CustomerCountryCode ?? "US",
        };

        foreach (var rule in rules)
        {
            var result = await evaluator.EvaluateAsync(rule.Id, variables, ct);
            if (result.IsSuccess && result.Value.Outcome)
            {
                triggeredRules.Add(rule.Name);
                riskScore += 25; // Each triggered rule adds 25 points
            }
        }

        ChecksRun.Add(1);

        if (triggeredRules.Count > 0 && riskScore >= 50)
        {
            ChecksFailed.Add(1);
            logger.LogWarning(
                "Fraud check FAILED for order {OrderId} (score={Score}, rules={Rules})",
                msg.OrderId, riskScore, string.Join(", ", triggeredRules));

            await publisher.Publish(new FraudCheckFailedEvent
            {
                SagaId = msg.SagaId,
                OrderId = msg.OrderId,
                RiskScore = riskScore,
                Reason = $"Triggered {triggeredRules.Count} fraud rule(s)",
                TriggeredRules = triggeredRules,
            }, ct);
        }
        else
        {
            logger.LogInformation(
                "Fraud check PASSED for order {OrderId} (score={Score})",
                msg.OrderId, riskScore);

            await publisher.Publish(new FraudCheckPassedEvent
            {
                SagaId = msg.SagaId,
                OrderId = msg.OrderId,
                RiskScore = riskScore,
            }, ct);
        }
    }
}
