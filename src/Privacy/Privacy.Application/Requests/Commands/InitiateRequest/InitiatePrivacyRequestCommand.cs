using FluentValidation;
using Haworks.Privacy.Application.Common.Interfaces;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.Contracts.Privacy;
using Haworks.Privacy.Domain.Aggregates;
using Haworks.Privacy.Domain.Enums;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Haworks.Privacy.Application.Requests.Commands.InitiateRequest;

public record InitiatePrivacyRequestCommand(Guid UserId, PrivacyRequestType Type, string IdempotencyKey = "") : IIdempotentCommand, IRequest<Guid>;

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

        // Publish BEFORE SaveChanges — UseBusOutbox writes the outbox row
        // into the same EF transaction, so SaveChanges commits both the
        // entity and the message atomically. No crash window.
        await _publishEndpoint.Publish(new InitiatePrivacyRequestMessage { RequestId = privacyRequest.Id, UserId = request.UserId }, cancellationToken);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Concurrent insert for the same user — re-query and return existing
            var concurrentExisting = await _context.PrivacyRequests.FirstOrDefaultAsync(
                r => r.UserId == request.UserId &&
                     (r.Status == PrivacyRequestStatus.Pending || r.Status == PrivacyRequestStatus.InProgress),
                cancellationToken);
            if (concurrentExisting is not null)
                return concurrentExisting.Id;
            throw;
        }

        return privacyRequest.Id;
    }
}
