using FluentAssertions;
using Haworks.Catalog.Application.Consumers;
using Haworks.Contracts.Catalog;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Haworks.Catalog.Unit.Consumers;

/// <summary>
/// StockReleaseFaultConsumer is the final-defense observer for stock-release
/// failures. After all retries + redeliveries are exhausted, MassTransit
/// publishes a Fault&lt;StockReleaseRequestedEvent&gt; and this consumer
/// catches it. Its only job is to log CRITICAL with the orderId / sagaId /
/// items / exception context an operator needs to unstick the reservation.
/// These tests verify that contract.
/// </summary>
public sealed class StockReleaseFaultConsumerTests
{
    [Fact]
    public async Task Logs_critical_with_full_orderId_sagaId_items_context()
    {
        var logger = new Mock<ILogger<StockReleaseFaultConsumer>>();
        var sut = new StockReleaseFaultConsumer(logger.Object);

        var orderId = Guid.NewGuid();
        var sagaId = Guid.NewGuid();
        var productId = Guid.NewGuid();

        var faultMessage = new TestFault
        {
            Message = new StockReleaseRequestedEvent
            {
                OrderId = orderId,
                SagaId = sagaId,
                Reason = "payment_session_failed",
                Items = new[]
                {
                    new StockReservationItem { ProductId = productId, ProductName = "Widget", Quantity = 3, RemainingStock = 0 },
                },
            },
            Exceptions = new[]
            {
                MakeExceptionInfo("Npgsql.PostgresException", "deadlock detected"),
            },
        };

        var ctxMock = new Mock<ConsumeContext<Fault<StockReleaseRequestedEvent>>>();
        ctxMock.SetupGet(c => c.Message).Returns(faultMessage);

        await sut.Consume(ctxMock.Object);

        logger.Verify(l => l.Log(
            LogLevel.Critical,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) =>
                state.ToString()!.Contains(orderId.ToString()) &&
                state.ToString()!.Contains(sagaId.ToString()) &&
                state.ToString()!.Contains(productId.ToString()) &&
                state.ToString()!.Contains("payment_session_failed") &&
                state.ToString()!.Contains("Npgsql.PostgresException") &&
                state.ToString()!.Contains("deadlock detected")),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once,
            "the fault log must include every datum an on-call engineer needs to unstick the reservation");
    }

    [Fact]
    public async Task Returns_completed_task_so_the_fault_message_acks_and_does_not_loop()
    {
        var logger = new Mock<ILogger<StockReleaseFaultConsumer>>();
        var sut = new StockReleaseFaultConsumer(logger.Object);

        var faultMessage = new TestFault
        {
            Message = new StockReleaseRequestedEvent
            {
                OrderId = Guid.NewGuid(),
                SagaId = Guid.NewGuid(),
                Items = Array.Empty<StockReservationItem>(),
                Reason = "test",
            },
            Exceptions = Array.Empty<ExceptionInfo>(),
        };
        var ctxMock = new Mock<ConsumeContext<Fault<StockReleaseRequestedEvent>>>();
        ctxMock.SetupGet(c => c.Message).Returns(faultMessage);

        var task = sut.Consume(ctxMock.Object);

        await task;
        task.IsCompletedSuccessfully.Should().BeTrue(
            "throwing here would re-loop the fault back into the broker forever");
    }

    [Fact]
    public async Task Handles_no_exceptions_gracefully()
    {
        // Defensive: if MT ever publishes a Fault<T> with an empty
        // Exceptions array (shouldn't happen, but contract defines it
        // as IReadOnlyList not non-empty), we should still log without
        // throwing on a null deref.
        var logger = new Mock<ILogger<StockReleaseFaultConsumer>>();
        var sut = new StockReleaseFaultConsumer(logger.Object);

        var faultMessage = new TestFault
        {
            Message = new StockReleaseRequestedEvent
            {
                OrderId = Guid.NewGuid(),
                SagaId = Guid.NewGuid(),
                Items = Array.Empty<StockReservationItem>(),
                Reason = "test",
            },
            Exceptions = Array.Empty<ExceptionInfo>(),
        };
        var ctxMock = new Mock<ConsumeContext<Fault<StockReleaseRequestedEvent>>>();
        ctxMock.SetupGet(c => c.Message).Returns(faultMessage);

        var act = async () => await sut.Consume(ctxMock.Object);

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Minimal Fault&lt;T&gt; double — MT's Fault is an interface, easier
    /// to roll a record than to mock a generic interface with required
    /// reference-type members.
    /// </summary>
    private sealed record TestFault : Fault<StockReleaseRequestedEvent>
    {
        public Guid FaultId { get; init; } = Guid.NewGuid();
        public Guid? FaultedMessageId { get; init; } = Guid.NewGuid();
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;
        public string[] FaultMessageTypes { get; init; } = new[] { "urn:message:Haworks.Contracts.Catalog:StockReleaseRequestedEvent" };
        public ExceptionInfo[] Exceptions { get; init; } = Array.Empty<ExceptionInfo>();
        public HostInfo Host { get; init; } = null!;
        public StockReleaseRequestedEvent Message { get; init; } = null!;
    }

    /// <summary>
    /// MT's <see cref="ExceptionInfo"/> is an interface; Moq can produce
    /// a quick double when only ExceptionType + Message are needed (which
    /// is all the fault consumer reads).
    /// </summary>
    private static ExceptionInfo MakeExceptionInfo(string exceptionType, string message)
    {
        var mock = new Mock<ExceptionInfo>();
        mock.SetupGet(e => e.ExceptionType).Returns(exceptionType);
        mock.SetupGet(e => e.Message).Returns(message);
        return mock.Object;
    }
}
