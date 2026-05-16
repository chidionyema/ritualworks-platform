namespace Haworks.Merchant.Application.Merchants.DTOs;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Skip, int Take);
