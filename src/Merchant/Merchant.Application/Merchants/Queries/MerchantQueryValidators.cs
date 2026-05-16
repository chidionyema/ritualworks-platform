using FluentValidation;

namespace Haworks.Merchant.Application.Merchants.Queries;

internal sealed class GetMerchantByIdQueryValidator : AbstractValidator<GetMerchantByIdQuery>
{
    public GetMerchantByIdQueryValidator()
    {
        RuleFor(x => x.MerchantId).NotEqual(Guid.Empty);
    }
}

internal sealed class GetMerchantByOwnerQueryValidator : AbstractValidator<GetMerchantByOwnerQuery>
{
    public GetMerchantByOwnerQueryValidator()
    {
        RuleFor(x => x.OwnerId).NotEqual(Guid.Empty);
    }
}

internal sealed class GetMerchantBySlugQueryValidator : AbstractValidator<GetMerchantBySlugQuery>
{
    public GetMerchantBySlugQueryValidator()
    {
        RuleFor(x => x.Slug).NotEmpty().MaximumLength(200);
    }
}

internal sealed class ListMerchantsQueryValidator : AbstractValidator<ListMerchantsQuery>
{
    public ListMerchantsQueryValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Take).InclusiveBetween(1, 100);
    }
}
