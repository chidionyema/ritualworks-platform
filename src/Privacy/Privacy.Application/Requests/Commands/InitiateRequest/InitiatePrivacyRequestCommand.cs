using FluentValidation;
using Haworks.Privacy.Application.Common.Interfaces;
using Haworks.Contracts.Privacy;
using Haworks.Privacy.Domain.Aggregates;
using Haworks.Privacy.Domain.Enums;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;

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
        var existing = await _context.PrivacyRequests.FirstOrDefaultAsync(
            r => r.UserId == request.UserId &&
                 (r.Status == PrivacyRequestStatus.Pending || r.Status == PrivacyRequestStatus.InProgress),
            cancellationToken);

        if (existing is not null)
            return existing.Id;

        var privacyRequest = PrivacyRequest.Create(request.UserId, request.Type);
        
        _context.PrivacyRequests.Add(privacyRequest);

        // SaveChanges first so the record is durable before the message is
        // dispatched; the outbox pattern then guarantees at-least-once delivery.
        await _context.SaveChangesAsync(cancellationToken);

        await _publishEndpoint.Publish(new InitiatePrivacyRequestMessage { RequestId = privacyRequest.Id, UserId = request.UserId }, cancellationToken);

        return privacyRequest.Id;
    }
}
