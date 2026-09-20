namespace TicketNow.QueueService;

public sealed record EnterQueueRequest(string OnsaleId, string? ChallengeId = null, string? Nonce = null);
public sealed record EnterQueueResponse(string SessionId, long Position, int EtaSeconds, bool Admitted, string? AdmissionToken, DateTimeOffset? AdmissionExpiresAt);
public sealed record PowChallengeResponse(string ChallengeId, int Difficulty, int ExpiresInSeconds);

public sealed record HeartbeatRequest(string SessionId);

public sealed record QueueStatusResponse(string OnsaleId, long Position, int EtaSeconds, bool Admitted, string? AdmissionToken = null, DateTimeOffset? TokenExpiresAt = null);

public sealed record QueueStatsResponse(string OnsaleId, long Waiting, int Admitted, int Rate, int Capacity);

public sealed record ConfigureQueueRequest(int? MaxConcurrentInside, int? InitialRate, int? MinRate, int? MaxRate, bool? RequiresQueue, int? PowDifficulty = null);
