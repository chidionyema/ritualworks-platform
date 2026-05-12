namespace Haworks.Webhooks.Application.Common;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Skip, int Take);
