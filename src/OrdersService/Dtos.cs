using TicketNow.Contracts.Commands;

namespace TicketNow.OrdersService;

public sealed record CreateOrderItemDto(Guid ZoneId, int Qty, decimal UnitPrice);

public sealed record CreateOrderRequest(
    Guid HoldId,
    Guid EventId,
    int MaxPerAccount,
    IReadOnlyList<CreateOrderItemDto> Items,
    string PaymentToken);

public sealed record OrderAcceptedResponse(Guid OrderId, string Status);

public sealed record OrderDetailResponse(
    Guid OrderId, Guid HoldId, string Status, decimal Total,
    IReadOnlyList<OrderItemResponse> Items, IReadOnlyList<Guid> Tickets);

public sealed record OrderItemResponse(Guid ZoneId, int Qty, decimal UnitPrice);

public sealed record TokenizeRequest(string Last4, string? CaptchaId = null, string? CaptchaAnswer = null);

public sealed record CaptchaChallenge(string CaptchaId, string Question, DateTimeOffset ExpiresAt);

public sealed record TokenizeResponse(string PaymentToken);

public sealed record VerifyTicketRequest(string QrJwt);

public sealed record VerifyTicketResponse(string Status, Guid? TicketId, Guid? OrderId);
