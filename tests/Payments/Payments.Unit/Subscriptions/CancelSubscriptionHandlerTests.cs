using FluentAssertions;
using Haworks.Payments.Application.Commands.Subscriptions;
using Haworks.Payments.Application.Interfaces;
using Haworks.Contracts.Payments;
using Haworks.Payments.Domain;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Moq;
using Xunit;

namespace Haworks.Payments.Unit.Subscriptions;

public class CancelSubscriptionHandlerTests
{
    private readonly Mock<ISubscriptionManager> _managerMock = new();
    private readonly Mock<IPaymentDbContext> _dbMock = new();

    [Fact]
    public async Task Handle_UserDoesNotOwnSubscription_ReturnsForbidden()
    {
        // Arrange — subscription belongs to "owner-user", but request comes from "attacker-user"
        var subscription = Subscription.Create(
            "owner-user",
            PaymentProvider.Stripe,
            "sub_123",
            "plan_1",
            DateTime.UtcNow,
            DateTime.UtcNow.AddMonths(1));

        var subscriptions = new List<Subscription> { subscription };
        var mockDbSet = CreateMockDbSet(subscriptions);
        _dbMock.Setup(x => x.Subscriptions).Returns(mockDbSet.Object);

        var handler = new CancelSubscriptionCommandHandler(_managerMock.Object, _dbMock.Object);
        var command = new CancelSubscriptionCommand("attacker-user", "sub_123");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Subscription.Forbidden");
        _managerMock.Verify(x => x.CancelAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_UserOwnsSubscription_CallsCancelAsync()
    {
        // Arrange
        var subscription = Subscription.Create(
            "owner-user",
            PaymentProvider.Stripe,
            "sub_123",
            "plan_1",
            DateTime.UtcNow,
            DateTime.UtcNow.AddMonths(1));

        var subscriptions = new List<Subscription> { subscription };
        var mockDbSet = CreateMockDbSet(subscriptions);
        _dbMock.Setup(x => x.Subscriptions).Returns(mockDbSet.Object);
        _managerMock.Setup(x => x.CancelAsync("sub_123", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var handler = new CancelSubscriptionCommandHandler(_managerMock.Object, _dbMock.Object);
        var command = new CancelSubscriptionCommand("owner-user", "sub_123");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _managerMock.Verify(x => x.CancelAsync("sub_123", false, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Mock<DbSet<T>> CreateMockDbSet<T>(List<T> sourceList) where T : class
    {
        var queryable = sourceList.AsQueryable();
        var mockSet = new Mock<DbSet<T>>();

        mockSet.As<IAsyncEnumerable<T>>()
            .Setup(m => m.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(new TestAsyncEnumerator<T>(queryable.GetEnumerator()));

        mockSet.As<IQueryable<T>>().Setup(m => m.Provider).Returns(new TestAsyncQueryProvider<T>(queryable.Provider));
        mockSet.As<IQueryable<T>>().Setup(m => m.Expression).Returns(queryable.Expression);
        mockSet.As<IQueryable<T>>().Setup(m => m.ElementType).Returns(queryable.ElementType);
        mockSet.As<IQueryable<T>>().Setup(m => m.GetEnumerator()).Returns(queryable.GetEnumerator());

        return mockSet;
    }

    // Minimal async query provider to support FirstOrDefaultAsync in unit tests
    private sealed class TestAsyncQueryProvider<TEntity>(IQueryProvider inner) : IAsyncQueryProvider
    {
        public IQueryable CreateQuery(System.Linq.Expressions.Expression expression)
            => new TestAsyncEnumerable<TEntity>(expression);

        public IQueryable<TElement> CreateQuery<TElement>(System.Linq.Expressions.Expression expression)
            => new TestAsyncEnumerable<TElement>(expression);

        public object? Execute(System.Linq.Expressions.Expression expression)
            => inner.Execute(expression);

        public TResult Execute<TResult>(System.Linq.Expressions.Expression expression)
            => inner.Execute<TResult>(expression);

        public TResult ExecuteAsync<TResult>(System.Linq.Expressions.Expression expression, CancellationToken cancellationToken = default)
        {
            var resultType = typeof(TResult).GetGenericArguments()[0];
            var executeMethod = typeof(IQueryProvider).GetMethods()
                .First(m => string.Equals(m.Name, nameof(IQueryProvider.Execute), StringComparison.Ordinal) && m.IsGenericMethod)
                .MakeGenericMethod(resultType);
            var result = executeMethod.Invoke(inner, new object[] { expression });
            return (TResult)typeof(Task).GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType).Invoke(null, new[] { result })!;
        }
    }

    private sealed class TestAsyncEnumerable<T> : EnumerableQuery<T>, IAsyncEnumerable<T>, IQueryable<T>
    {
        public TestAsyncEnumerable(IEnumerable<T> enumerable) : base(enumerable) { }
        public TestAsyncEnumerable(System.Linq.Expressions.Expression expression) : base(expression) { }

        IQueryProvider IQueryable.Provider => new TestAsyncQueryProvider<T>(this);

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new TestAsyncEnumerator<T>(this.AsEnumerable().GetEnumerator());
    }

    private sealed class TestAsyncEnumerator<T>(IEnumerator<T> inner) : IAsyncEnumerator<T>
    {
        public T Current => inner.Current;
        public ValueTask<bool> MoveNextAsync() => new(inner.MoveNext());
        public ValueTask DisposeAsync() { inner.Dispose(); return ValueTask.CompletedTask; }
    }
}
