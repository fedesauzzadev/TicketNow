using Microsoft.EntityFrameworkCore;

namespace TicketNow.OrdersService;

/// <summary>
/// Límite por cuenta y evento (Fase 6, ADR-011): entradas ya en poder del
/// usuario = tickets emitidos + órdenes vivas (Pending) aún sin ticket.
/// Las confirmadas ya tienen su ticket (contar ambas duplicaría).
/// </summary>
public static class AccountLimits
{
    public static async Task<int> OwnedQtyAsync(
        OrdersDbContext db, string userId, Guid eventId, CancellationToken ct)
    {
        var tickets = await db.Tickets
            .Join(
                db.Orders.Where(o => o.UserId == userId),
                ticket => ticket.OrderId,
                order => order.Id,
                (ticket, _) => ticket)
            .Where(t => t.EventId == eventId)
            .SumAsync(t => (int?)t.Qty, ct) ?? 0;

        var pending = await db.Orders
            .Where(o => o.UserId == userId && o.EventId == eventId && o.Status == OrderStatus.Pending)
            .SelectMany(o => o.Items)
            .SumAsync(i => (int?)i.Qty, ct) ?? 0;

        return tickets + pending;
    }
}
