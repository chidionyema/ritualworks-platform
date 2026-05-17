using FluentAssertions;
using Haworks.Realtime.Api.Application.Common;
using Haworks.Realtime.Api.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Haworks.Realtime.Unit.Infrastructure;

public class RedisInboxServiceTests
{
    private readonly Mock<IConnectionMultiplexer> _redisMock;
    private readonly Mock<IDatabase> _dbMock;
    private readonly Mock<ILogger<RedisInboxService>> _loggerMock;
    private readonly RedisInboxService _sut;

    public RedisInboxServiceTests()
    {
        _redisMock = new Mock<IConnectionMultiplexer>();
        _dbMock = new Mock<IDatabase>();
        _loggerMock = new Mock<ILogger<RedisInboxService>>();

        _redisMock.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_dbMock.Object);

        _sut = new RedisInboxService(_redisMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task StoreMessageAsync_trims_inbox_at_max_size()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var message = new { MessageType = "Test", Data = "payload" };

        _dbMock.Setup(x => x.ListLeftPushAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(1);

        _dbMock.Setup(x => x.ListTrimAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .Returns(Task.CompletedTask);

        _dbMock.Setup(x => x.KeyExpireAsync(
                It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        // Act
        await _sut.StoreMessageAsync(userId, message);

        // Assert — LTRIM is called with 0..(MaxInboxSize-1), capping at 1000 entries
        _dbMock.Verify(x => x.ListTrimAsync(
            It.Is<RedisKey>(k => k.ToString() == $"inbox:{userId}"),
            0,
            InboxConstants.MaxInboxSize - 1,
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public void MaxInboxSize_is_1000()
    {
        InboxConstants.MaxInboxSize.Should().Be(1000);
    }

    [Fact]
    public async Task GetMessagesAsync_returns_empty_when_no_messages()
    {
        // Arrange
        var userId = Guid.NewGuid();
        _dbMock.Setup(x => x.ListRangeAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(Array.Empty<RedisValue>());

        // Act
        var result = await _sut.GetMessagesAsync(userId);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task AcknowledgeMessagesAsync_deletes_inbox_key()
    {
        // Arrange
        var userId = Guid.NewGuid();
        _dbMock.Setup(x => x.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        // Act
        await _sut.AcknowledgeMessagesAsync(userId);

        // Assert
        _dbMock.Verify(x => x.KeyDeleteAsync(
            It.Is<RedisKey>(k => k.ToString() == $"inbox:{userId}"),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task Inbox_overflow_trims_oldest_entries()
    {
        // Arrange — simulate writing 1001 messages; verify LTRIM ensures max 1000
        var userId = Guid.NewGuid();
        var trimCalls = new List<(long start, long stop)>();

        _dbMock.Setup(x => x.ListLeftPushAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(1);

        _dbMock.Setup(x => x.ListTrimAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey, long, long, CommandFlags>((_, start, stop, _) => trimCalls.Add((start, stop)))
            .Returns(Task.CompletedTask);

        _dbMock.Setup(x => x.KeyExpireAsync(
                It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        // Act — write 1001 messages
        for (var i = 0; i < 1001; i++)
        {
            await _sut.StoreMessageAsync(userId, new { Index = i });
        }

        // Assert — every write issues LTRIM 0..999, so messages beyond 1000 are trimmed
        trimCalls.Should().HaveCount(1001);
        trimCalls.Should().AllSatisfy(call =>
        {
            call.start.Should().Be(0);
            call.stop.Should().Be(999); // MaxInboxSize - 1
        });
    }
}
