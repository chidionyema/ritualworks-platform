namespace Haworks.Contracts.Privacy;

public record PrivacyErasureRequested(Guid RequestId, Guid UserId);
public record PrivacyErasureCompleted(Guid RequestId, Guid UserId, string ServiceName);
public record PrivacyErasureFailed(Guid RequestId, Guid UserId, string ServiceName, string ErrorMessage);
public record PrivacyErasureTimedOut(Guid RequestId); // PR-02: timeout event

public record PrivacyDataExportRequested(Guid RequestId, Guid UserId);
public record PrivacyDataExportCompleted(Guid RequestId, Guid UserId, string ServiceName, string? DataLink);
public record PrivacyDataExportFailed(Guid RequestId, Guid UserId, string ServiceName, string ErrorMessage);

// PR-10: moved from saga file to contracts
public record InitiatePrivacyRequestMessage(Guid RequestId, Guid UserId);
