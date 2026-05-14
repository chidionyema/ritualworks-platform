using FluentValidation;
using Haworks.Privacy.Application.Common.Interfaces;
using Haworks.Contracts.Privacy;
using Haworks.Privacy.Domain.Aggregates;
using Haworks.Privacy.Domain.Enums;
using MassTransit;
using MediatR;

namespace Haworks.Privacy.Application.Requests.Commands.InitiateRequest;

public record InitiatePrivacyRequestCommand(Guid UserId, PrivacyRequestType Type) : IRequest<Guid>;

public class InitiatePrivacyRequestCommandValidator : AbstractValidator<InitiatePrivacyRequestCommand>
{
    public InitiatePrivacyRequestCommandValidator()
    {
        RuleFor(v => v.UserId).NotEmpty();
        RuleFor(v => v.Type).IsInEnum();
    }
}

public class InitiatePrivacyRequestCommandHandler : IRequestHandler<InitiatePrivacyRequestCommand, Guid>
{
    private readonly IPrivacyDbContext _context;
    private readonly IPublishEndpoint _publishEndpoint;

    public InitiatePrivacyRequestCommandHandler(IPrivacyDbContext context, IPublishEndpoint publishEndpoint)
    {
        _context = context;
        _publishEndpoint = publishEndpoint;
    }

    public async Task<Guid> Handle(InitiatePrivacyRequestCommand request, CancellationToken cancellationToken)
    {
        var privacyRequest = PrivacyRequest.Create(request.UserId, request.Type);
        
        _context.PrivacyRequests.Add(privacyRequest);
        
        await _publishEndpoint.Publish(new InitiatePrivacyRequestMessage(privacyRequest.Id, request.UserId), cancellationToken);
        
        await _context.SaveChangesAsync(cancellationToken);

        return privacyRequest.Id;
    }
}
