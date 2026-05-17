using FluentValidation;
using Haworks.Merchant.Application.Merchants.Queries;

namespace Haworks.Merchant.Application.Merchants.Validators;

public sealed class GetMerchantByIdQueryValidator : AbstractValidator<GetMerchantByIdQuery>
{
    public GetMerchantByIdQueryValidator()
    {
        RuleFor(x => x.MerchantId).NotEqual(Guid.Empty);
    }
}

public sealed class ListMerchantsQueryValidator : AbstractValidator<ListMerchantsQuery>
{
    public ListMerchantsQueryValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Take).InclusiveBetween(1, 100);
    }
}

public sealed class GetMerchantBySlugQueryValidator : AbstractValidator<GetMerchantBySlugQuery>
{
    public GetMerchantBySlugQueryValidator()
    {
        RuleFor(x => x.Slug).NotEmpty();
    }
}

public sealed class GetMerchantByOwnerQueryValidator : AbstractValidator<GetMerchantByOwnerQuery>
{
    public GetMerchantByOwnerQueryValidator()
    {
        RuleFor(x => x.OwnerId).NotEmpty();
    }
}
