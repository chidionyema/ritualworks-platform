using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;
using Haworks.RulesEngine.Api.Domain;
using Haworks.RulesEngine.Api.Application;

namespace Haworks.RulesEngine.Integration;

[Collection("RulesEngine Integration")]
public sealed class RuleFlowsTests : IAsyncLifetime
{
    private readonly RulesEngineWebAppFactory _factory;
    private readonly HttpClient _client;

    public RuleFlowsTests(RulesEngineWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.EnsureSchemaAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Health_returns_200()
    {
        var response = await _client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Create_and_evaluate_rule_round_trips()
    {
        // 1. Create a rule: "age > 18"
        var createResp = await _client.PostAsJsonAsync("/api/rules", new CreateRuleCommand(
            Name: "Adult Check",
            Expression: "age > 18"
        ));
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var rule = await createResp.Content.ReadFromJsonAsync<Rule>();
        rule.Should().NotBeNull();
        rule!.Id.Should().NotBeEmpty();

        // 2. Evaluate rule (True)
        var evalTrueResp = await _client.PostAsJsonAsync("/api/rules/evaluate", new EvaluateRuleQuery(
            RuleId: rule.Id,
            Inputs: new Dictionary<string, object> { ["age"] = 25 }
        ));
        evalTrueResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var resultTrue = await evalTrueResp.Content.ReadFromJsonAsync<RuleEvaluationResult>();
        resultTrue!.Outcome.Should().BeTrue();

        // 3. Evaluate rule (False)
        var evalFalseResp = await _client.PostAsJsonAsync("/api/rules/evaluate", new EvaluateRuleQuery(
            RuleId: rule.Id,
            Inputs: new Dictionary<string, object> { ["age"] = 15 }
        ));
        evalFalseResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var resultFalse = await evalFalseResp.Content.ReadFromJsonAsync<RuleEvaluationResult>();
        resultFalse!.Outcome.Should().BeFalse();
    }

    [Fact]
    public async Task Get_missing_rule_returns_404()
    {
        var resp = await _client.GetAsync($"/api/rules/{Guid.NewGuid()}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
