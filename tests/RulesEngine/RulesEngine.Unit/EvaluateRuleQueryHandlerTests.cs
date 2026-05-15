using Haworks.BuildingBlocks.Common;
using Haworks.RulesEngine.Api.Application;
using Haworks.RulesEngine.Api.Domain;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Haworks.RulesEngine.Unit;

public class EvaluateRuleQueryHandlerTests
{
    private readonly Mock<IRulesEvaluator> _rulesEvaluatorMock;
    private readonly Mock<ILogger<EvaluateRuleQueryHandler>> _loggerMock;
    private readonly EvaluateRuleQueryHandler _handler;

    public EvaluateRuleQueryHandlerTests()
    {
        _rulesEvaluatorMock = new Mock<IRulesEvaluator>();
        _loggerMock = new Mock<ILogger<EvaluateRuleQueryHandler>>();
        _handler = new EvaluateRuleQueryHandler(_rulesEvaluatorMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task Handle_ShouldReturnSuccess_WhenEvaluationSucceeds()
    {
        // Arrange
        var query = new EvaluateRuleQuery("rule-1", new Dictionary<string, object> { { "age", 25 } });
        _rulesEvaluatorMock.Setup(x => x.EvaluateAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(true));

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
    }

    [Fact]
    public async Task Handle_ShouldReturnFailure_WhenEvaluationFails()
    {
        // Arrange
        var query = new EvaluateRuleQuery("rule-1", new Dictionary<string, object> { { "age", 15 } });
        _rulesEvaluatorMock.Setup(x => x.EvaluateAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(false));

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.False(result.Value);
    }

    [Fact]
    public async Task Handle_ShouldReturnTimeout_WhenCanceled()
    {
        // Arrange
        var query = new EvaluateRuleQuery("rule-1", new Dictionary<string, object>());
        _rulesEvaluatorMock.Setup(x => x.EvaluateAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Timeout, result.Error.Type);
    }
}
